using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The non-mouth half of a talking face, owned by <see cref="NPCFace"/>: eyebrow beats, head beats and posture
/// shifts, speech-timed blinks and gaze (aversions, face scanning) for one NPC. A voiced line is planned once from
/// its <see cref="ProsodyTrack"/> (the whole clip is analysed before it plays); table chatter without audio gets
/// the same gestures from its babble words; thinking and listening only move the eyes.
/// Timings and sizes follow the co-speech literature: brows lead an accent by ~0.16 s and mark about one accent per
/// phrase (Flecha-Garcia 2010, House 2001); the head nods on most accents through a ~3 Hz spring and drifts with
/// pitch (Graf 2002, Busso 2007, Munhall 2004); blinks fall on pauses and line ends (Cassell 1994, Pelachaud 1996);
/// speakers look away while starting a turn and are back in mutual gaze well before its end (Andrist 2013), and
/// saccades take 25 ms + 2.4 ms per degree (Lee et al. 2002, "Eyes Alive"). Emotion scales activity and picks the
/// brow kind (Pelachaud 1996). Not a MonoBehaviour: NPCFace calls <see cref="Update"/> once per frame and reads
/// the outputs.
/// </summary>
public sealed class FaceGestures
{
    /// <summary>Which brows mark an accent.</summary>
    public enum BrowKind { Raise, Knit, Worry }

    // Brow beat envelope (seconds, relative to the accented vowel): rise early, hold through the vowel, ease off
    private const float BrowLead = 0.16f, BrowHold = 0.12f, BrowRelease = 0.3f;
    private const float SecondBrowGap = 1.2f;           // A second brow beat in one phrase needs this much room...
    private const float SecondBrowStrength = 0.75f;     // ...and a strong accent
    // Head beat: a short push into the beat spring around the accent
    private const float BeatLead = 0.05f, BeatLength = 0.17f;
    private const float BeatSpringHz = 3f, BeatDamping = 0.6f, OvershootDamping = 0.45f;
    private const float PostureSpringHz = 1.1f, PostureDamping = 0.9f;
    private const float PitchDriftPerSemitone = 0.35f, MaxPitchDrift = 2.5f;  // Degrees of chin-up per semitone of F0
    private const float MaxHeadPitch = 7f, MaxHeadYaw = 8f, MaxHeadRoll = 5f;
    // Blinks
    private const float PauseBlinkSeconds = 0.25f;
    // Gaze (degrees and seconds)
    private const float ScanEyeDegrees = 1.2f, ScanMouthDegrees = 3f;  // The player's eyes and mouth at ~1.2 m
    private const float HeadFollowsGaze = 0.3f, HeadFollowFrom = 8f;   // Big glances turn the head a little too
    private const float LineTail = 1.5f;                // Gestures finish easing out this long after a line ends

    /// <summary>Gesture style of one emotion: gains on the neutral behaviour plus its brow kind.</summary>
    private struct Style
    {
        public float Head;          // Head motion amplitude
        public float Speed;         // Head motion speed
        public float Damping;       // Beat spring damping (lower = more overshoot)
        public BrowKind Brow;
        public float BrowRate;      // Share of phrases that get a brow beat
        public bool FirstAccentOnly;
        public float Blink;         // Speech-blink probability gain
        public float Aversion;      // Gaze aversion gain (how often and how long)
        public float DownBias;      // 0..1: aversions favour looking down
        public float Squint;        // Duchenne squint on accents
        public float Sneer;         // Nose wrinkle on loud accents
        public float Wide;          // Eye widening on accents
        public float BasePitch;     // Posture: + chin down (sad, angry), - chin up (disgust)
    }

    private struct BrowBeat
    {
        public float Time, Amount;
        public BrowKind Kind;
    }

    private struct HeadBeat
    {
        public float Time, Pitch, Yaw;
        public bool Overshoot;
    }

    private struct PoseShift
    {
        public float Time;
        public Vector3 Pose;    // x pitch, y yaw, z roll (degrees)
    }

    private enum GazeMode { Mutual, Away }

    // ---- Outputs (read by NPCFace after Update) ----
    /// <summary>Brow offsets 0..1 by kind (already scaled by expressiveness).</summary>
    public float BrowRaise, BrowKnit, BrowWorry;
    /// <summary>Accent extras 0..1: Duchenne squint, nose wrinkle, eye widening.</summary>
    public float Squint, Sneer, EyeWide;
    /// <summary>Head offset in degrees: + pitch = chin down, + yaw = toward the character's right, + roll = tilt left.</summary>
    public float HeadPitch, HeadYaw, HeadRoll;
    /// <summary>Eye offset in degrees from where the eyes would look (+ yaw right, + pitch down).</summary>
    public float GazeYaw, GazePitch;
    /// <summary>0..1 around accented syllables: the mouth articulates a little more there (JALI's stressed vowels).</summary>
    public float Emphasis;

    // ---- Line state ----
    private bool _lineActive;
    private bool _audioLine;            // Planned from prosody (else babble, planned live)
    private ProsodyTrack _track;
    private Style _style;
    private float _clock;               // Line time: the clip's time while it plays, then advanced by dt
    private float _lineEnd;             // Line time the speech ends
    private bool _question;
    private bool _whQuestion;
    private float _questionFrom;        // Line time the question's ending gesture starts
    private readonly List<BrowBeat> _brows = new List<BrowBeat>(8);
    private readonly List<HeadBeat> _beats = new List<HeadBeat>(16);
    private readonly List<PoseShift> _shifts = new List<PoseShift>(8);
    private readonly List<float> _blinks = new List<float>(8);
    private int _nextBlink;
    private float _lastPitchDrift;
    private float _babbleNextShift;
    private float _speechStartClock;

    // ---- Springs ----
    private Vector3 _beatPos, _beatVel;         // x pitch, y yaw
    private Vector3 _posturePos, _postureVel;   // x pitch, y yaw, z roll

    // ---- Gaze ----
    private GazeMode _gazeMode = GazeMode.Mutual;
    private float _gazeUntil;                   // Unscaled time the current fixation or aversion ends
    private Vector2 _gazeFrom, _gazeTo, _gazeNow;
    private float _saccadeStart, _saccadeLength;
    private float _plannedAversionStart = -1f, _plannedAversionEnd;   // Line time
    private Vector2 _plannedAversion;
    private float _nextIntimacyAversion;        // Line time
    private bool _wasThinking;
    private bool _endAversion;                  // A cut line ends its speaking look-away at the next update

    // ---- Per NPC ----
    private uint _rng;
    private readonly float _sideBias;           // -1..1: which way this NPC tends to look away
    private bool _blinkRequested;

    /// <summary>Overall gain on brows, accent extras and head motion; look-aways scale within 0.5-1.25.</summary>
    public float Expressiveness = 1f;

    /// <summary>What the last <see cref="BeginLine"/> planned, for logs.</summary>
    public string PlanSummary { get; private set; } = "";

    public FaceGestures(uint seed)
    {
        _rng = seed != 0u ? seed : 0x2545F491u;
        _sideBias = Random01() * 2f - 1f;
    }

    /// <summary>True once per requested blink (speech pauses, line ends, accents, big glances).</summary>
    public bool ConsumeBlinkRequest()
    {
        bool requested = _blinkRequested;
        _blinkRequested = false;
        return requested;
    }

    // ================================================================
    // Line planning
    // ================================================================

    /// <summary>
    /// Plans the gestures of a voiced line from its prosody. <paramref name="clipTime"/> is where the clip is now
    /// (the analysis finishes a few ms after playback starts); events already behind it are skipped.
    /// </summary>
    public void BeginLine(ProsodyTrack track, string text, FaceEmotion emotion, float clipTime)
    {
        ResetLine();
        if (track == null) return;

        _lineActive = true;
        _audioLine = true;
        _track = track;
        _style = StyleFor(emotion);
        _clock = clipTime;
        _lineEnd = track.Syllables.Length > 0 ? track.SpeechEnd : track.Duration;
        _speechStartClock = track.SpeechStart;

        string trimmed = text != null ? text.Trim() : string.Empty;
        bool questionMark = trimmed.EndsWith("?");
        _whQuestion = questionMark && StartsWithWhWord(LastSentence(trimmed));
        _question = track.EndsAsQuestion || (questionMark && !_whQuestion);

        PlanBrows(track);
        PlanHeadBeats(track);
        PlanPosture(track);
        PlanBlinks(track);
        PlanGaze(track);

        while (_nextBlink < _blinks.Count && _blinks[_nextBlink] < clipTime) _nextBlink++;

        PlanSummary = $"{emotion}, {track.Duration:0.0}s, {track.Accents.Length} accents, {_brows.Count} brow beats " +
                      $"({_style.Brow}), {_beats.Count} head beats, {_shifts.Count} postures, {_blinks.Count} blinks, " +
                      $"aversion {(_plannedAversionStart >= 0f ? $"to {_plannedAversionEnd:0.0}s" : "none")}, " +
                      $"{(_question ? "yes/no question" : _whQuestion ? "wh-question" : "statement")}";
    }

    /// <summary>
    /// Starts gestures for speech without audio (table chatter): brows and nods come from the babble words.
    /// </summary>
    public void BeginBabble(FaceEmotion emotion)
    {
        ResetLine();
        _lineActive = true;
        _audioLine = false;
        _style = StyleFor(emotion);
        _clock = 0f;
        _lineEnd = float.PositiveInfinity;
        _babbleNextShift = 0f;
        _nextIntimacyAversion = Gaussian(4.75f, 1.4f, 2.5f, 7f) * Mathf.Lerp(1.4f, 0.8f, _style.Aversion * 0.5f);
    }

    /// <summary>A babble word starts now: maybe a brow beat and a nod on its first syllable.</summary>
    public void OnBabbleWord()
    {
        if (!_lineActive || _audioLine) return;

        float t = _clock + 0.12f;   // The beat lands on the word's first vowel
        float strength = RandomRange(0.35f, 0.85f);
        if (Random01() < 0.3f * _style.BrowRate)
            _brows.Add(new BrowBeat { Time = t, Amount = BrowAmount(strength), Kind = _style.Brow });
        if (Random01() < 0.55f)
            AddHeadBeat(t, strength, ProsodyTrack.AccentKind.Loud);
        if (_clock >= _babbleNextShift)
        {
            _shifts.Add(new PoseShift { Time = _clock, Pose = PhrasePose() });
            _babbleNextShift = _clock + RandomRange(2f, 3.5f);
        }
    }

    /// <summary>
    /// The line's speech stopped (audio finished or babble ended): gestures already under way finish, held
    /// question gestures release, and the line closes after a short tail. A line cut short (dialogue closed
    /// mid-sentence, babble over) drops everything planned after the cut and ends with a blink, and its eyes
    /// come back from any look-away.
    /// </summary>
    public void EndLine()
    {
        if (!_lineActive) return;
        _plannedAversionStart = -1f;
        float cut = _clock;
        if (cut >= _lineEnd) return;   // Ended naturally: the planned tail (end blink, question hold) plays out

        _brows.RemoveAll(b => b.Time - BrowLead > cut);
        _beats.RemoveAll(b => b.Time - BeatLead > cut);
        _shifts.RemoveAll(p => p.Time > cut);
        if (_nextBlink < _blinks.Count) _blinks.RemoveRange(_nextBlink, _blinks.Count - _nextBlink);
        _blinks.Add(cut + 0.03f);
        if ((_question || _whQuestion) && _questionFrom - 0.1f > cut)
        {
            _question = false;
            _whQuestion = false;
        }
        _lineEnd = cut;
        _endAversion = true;
    }

    private void ResetLine()
    {
        _lineActive = false;
        _track = null;
        _brows.Clear();
        _beats.Clear();
        _shifts.Clear();
        _blinks.Clear();
        _nextBlink = 0;
        _question = false;
        _whQuestion = false;
        _lastPitchDrift = 0f;
        _plannedAversionStart = -1f;
        _nextIntimacyAversion = float.PositiveInfinity;
    }

    /// <summary>One brow beat per phrase on its strongest accent, plus a second for strong accents with room.</summary>
    private void PlanBrows(ProsodyTrack track)
    {
        ProsodyTrack.Accent[] accents = track.Accents;
        ProsodyTrack.PhraseEnd[] phrases = track.PhraseEnds;
        bool first = true;

        for (int p = 0; p < phrases.Length; p++)
        {
            float from = p == 0 ? float.NegativeInfinity : phrases[p].Start - 0.01f;
            float to = phrases[p].Time + 0.01f;
            int best = -1, second = -1;
            for (int a = 0; a < accents.Length; a++)
            {
                if (accents[a].Time < from || accents[a].Time > to) continue;
                if (best < 0 || accents[a].Strength > accents[best].Strength)
                {
                    second = best;
                    best = a;
                }
                else if (second < 0 || accents[a].Strength > accents[second].Strength)
                {
                    second = a;
                }
            }
            if (best < 0) continue;
            if (_style.FirstAccentOnly && !first) break;
            first = false;
            if (Random01() > _style.BrowRate) continue;

            _brows.Add(new BrowBeat { Time = accents[best].Time, Amount = BrowAmount(accents[best].Strength), Kind = _style.Brow });
            if (second >= 0 && !_style.FirstAccentOnly && accents[second].Strength >= SecondBrowStrength &&
                Mathf.Abs(accents[second].Time - accents[best].Time) >= SecondBrowGap)
            {
                _brows.Add(new BrowBeat { Time = accents[second].Time, Amount = BrowAmount(accents[second].Strength), Kind = _style.Brow });
            }
        }
    }

    /// <summary>Nods on most accents and on some other prominent syllables (about 6 per 10 s of speech).</summary>
    private void PlanHeadBeats(ProsodyTrack track)
    {
        foreach (ProsodyTrack.Accent accent in track.Accents)
        {
            if (accent.Strength >= 0.5f || Random01() < 0.6f)
                AddHeadBeat(accent.Time, accent.Strength, accent.Kind);
        }

        float last = float.NegativeInfinity;
        foreach (ProsodyTrack.Syllable syllable in track.Syllables)
        {
            if (syllable.Prominence < 0.3f || Random01() > 0.25f) continue;
            if (NearBeat(syllable.Time, 0.5f) || syllable.Time - last < 0.8f) continue;
            AddHeadBeat(syllable.Time, syllable.Prominence * 0.7f, ProsodyTrack.AccentKind.Loud);
            last = syllable.Time;
        }
        _beats.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private void AddHeadBeat(float time, float strength, ProsodyTrack.AccentKind kind)
    {
        float amount = (1.2f + 2.3f * Mathf.Clamp01(strength)) * _style.Head;
        // Rising accents lift the chin a little; falling and loud ones nod down
        float pitch = kind == ProsodyTrack.AccentKind.Rise ? -0.6f * amount : amount;
        float yaw = Random01() < 0.2f ? (Random01() < 0.5f ? -1f : 1f) * 0.4f * amount : 0f;
        _beats.Add(new HeadBeat { Time = time, Pitch = pitch, Yaw = yaw, Overshoot = Random01() < 0.2f });
    }

    private bool NearBeat(float time, float radius)
    {
        foreach (HeadBeat beat in _beats)
            if (Mathf.Abs(beat.Time - time) < radius) return true;
        return false;
    }

    /// <summary>A new head posture at the start of the line and of each phrase after a pause.</summary>
    private void PlanPosture(ProsodyTrack track)
    {
        ProsodyTrack.PhraseEnd[] phrases = track.PhraseEnds;
        _shifts.Add(new PoseShift { Time = 0f, Pose = PhrasePose() });
        for (int p = 1; p < phrases.Length; p++)
            _shifts.Add(new PoseShift { Time = phrases[p].Start - 0.1f, Pose = PhrasePose() });

        if (_question || _whQuestion)
        {
            // The final word: chin up and a tilt toward the listener, held until they answer
            float lastStart = phrases.Length > 0 ? phrases[phrases.Length - 1].Start : track.SpeechStart;
            _questionFrom = Mathf.Max(lastStart, _lineEnd - 0.4f);
        }
    }

    private Vector3 PhrasePose()
    {
        float yaw = RandomRange(1f, 3.5f) * (Random01() < 0.5f ? -1f : 1f);
        float roll = RandomRange(0.4f, 2f) * (Random01() < 0.5f ? -1f : 1f);
        float pitch = RandomRange(-1f, 1f);
        return new Vector3(pitch, yaw, roll) * _style.Head;
    }

    /// <summary>Blinks on phrase pauses, at the line's end and now and then on a strong accent.</summary>
    private void PlanBlinks(ProsodyTrack track)
    {
        float gain = _style.Blink;
        foreach (ProsodyTrack.Pause pause in track.Pauses)
        {
            if (pause.End - pause.Start >= PauseBlinkSeconds && Random01() < 0.7f * gain)
                _blinks.Add(pause.Start + 0.03f);
        }
        foreach (ProsodyTrack.Accent accent in track.Accents)
        {
            if (accent.Strength >= 0.75f && Random01() < 0.2f * gain)
                _blinks.Add(accent.Time - 0.08f);
        }
        if (Random01() < Mathf.Min(0.95f, 0.85f * gain))
            _blinks.Add(_lineEnd + 0.03f);
        _blinks.Sort();
    }

    /// <summary>
    /// A turn-start aversion (most lines over 1.5 s), back in mutual gaze well before the end, and short
    /// intimacy aversions every ~5 s in lines over 6 s (none in their last 2.4 s).
    /// </summary>
    private void PlanGaze(ProsodyTrack track)
    {
        float length = _lineEnd - track.SpeechStart;
        if (length >= 1.5f && Random01() < Mathf.Clamp01(0.73f * _style.Aversion))
        {
            float latestEnd = length > 4f ? _lineEnd - 2.4f : track.SpeechStart + 0.45f * length;
            float end = Mathf.Min(track.SpeechStart + Gaussian(1.3f, 0.5f, 0.5f, 2.3f), latestEnd);
            if (end > track.SpeechStart + 0.35f)
            {
                _plannedAversionStart = 0f;
                _plannedAversionEnd = end;
                _plannedAversion = AversionOffset(0.29f, 0.21f, RandomRange(8f, 14f));
            }
        }

        _nextIntimacyAversion = length > 6f
            ? track.SpeechStart + Gaussian(4.75f, 1.4f, 3f, 7f) / Mathf.Max(0.3f, _style.Aversion)
            : float.PositiveInfinity;
    }

    // ================================================================
    // Per frame
    // ================================================================

    /// <summary>
    /// Advances every gesture. <paramref name="clipTime"/> is the voiced line's playback time, or negative when no
    /// clip plays; <paramref name="thinking"/> is the Animator's isThinking; <paramref name="emotion"/> is what the
    /// face currently shows (it styles idle gaze and refreshes the line's style).
    /// </summary>
    public void Update(float dt, float now, float clipTime, bool thinking, FaceEmotion emotion)
    {
        dt = Mathf.Clamp(dt, 0f, 0.1f);

        if (_lineActive)
        {
            _clock = clipTime >= 0f && _audioLine ? clipTime : _clock + dt;
            if (_clock > _lineEnd + LineTail) ResetLine();
        }

        float expressiveness = Mathf.Max(0f, Expressiveness);
        Vector3 beatTarget = Vector3.zero;
        Vector3 postureTarget = Vector3.zero;
        BrowRaise = BrowKnit = BrowWorry = Squint = Sneer = EyeWide = Emphasis = 0f;
        bool overshoot = false;

        if (_lineActive)
        {
            EvaluateBrows(expressiveness);
            beatTarget = EvaluateBeats(out overshoot);
            postureTarget = EvaluatePosture();
            FireBlinks();
        }

        UpdateGaze(now, thinking, emotion);

        // Large glances bring the head along a little (it lags the eyes through the posture spring)
        Vector2 gaze = _gazeTo;
        if (gaze.magnitude > HeadFollowFrom)
        {
            postureTarget.y += gaze.x * HeadFollowsGaze;
            postureTarget.x += gaze.y * HeadFollowsGaze;
        }

        float speed = _lineActive ? Mathf.Lerp(1f, _style.Speed, 0.5f) : 1f;
        float damping = overshoot ? OvershootDamping : _lineActive ? _style.Damping : BeatDamping;
        StepSpring(ref _beatPos, ref _beatVel, beatTarget * expressiveness, BeatSpringHz * speed, damping, dt);
        StepSpring(ref _posturePos, ref _postureVel, postureTarget * expressiveness, PostureSpringHz * Mathf.Sqrt(speed),
            PostureDamping, dt);

        HeadPitch = Mathf.Clamp(_beatPos.x + _posturePos.x, -MaxHeadPitch, MaxHeadPitch);
        HeadYaw = Mathf.Clamp(_beatPos.y + _posturePos.y, -MaxHeadYaw, MaxHeadYaw);
        HeadRoll = Mathf.Clamp(_posturePos.z, -MaxHeadRoll, MaxHeadRoll);
        GazeYaw = _gazeNow.x;
        GazePitch = _gazeNow.y;
    }

    private void EvaluateBrows(float expressiveness)
    {
        float raise = 0f, knit = 0f, worry = 0f, accent = 0f;
        foreach (BrowBeat beat in _brows)
        {
            float v = BrowEnvelope(_clock - beat.Time) * beat.Amount;
            if (v <= 0f) continue;
            accent = Mathf.Max(accent, v);
            switch (beat.Kind)
            {
                case BrowKind.Raise: raise = Mathf.Max(raise, v); break;
                case BrowKind.Knit: knit = Mathf.Max(knit, v); break;
                default: worry = Mathf.Max(worry, v); break;
            }
        }

        // Question endings: brows raised (yes/no) or drawn together (asking for information) over the last word,
        // held while the listener is expected to answer
        if (_question || _whQuestion)
        {
            float held = Ramp(_clock, _questionFrom - 0.1f, _questionFrom + 0.2f) *
                         (1f - Ramp(_clock, _lineEnd + 0.8f, _lineEnd + LineTail));
            if (_whQuestion) knit = Mathf.Max(knit, 0.18f * held);
            else raise = Mathf.Max(raise, 0.28f * held);
        }

        // Stressed vowels open a little more: a triangle over each accented syllable
        if (_audioLine && _track != null)
        {
            foreach (ProsodyTrack.Accent a in _track.Accents)
            {
                float d = Mathf.Abs(_clock - a.Time);
                if (d < 0.15f) Emphasis = Mathf.Max(Emphasis, (1f - d / 0.15f) * a.Strength);
            }
        }

        BrowRaise = raise * expressiveness;
        BrowKnit = knit * expressiveness;
        BrowWorry = worry * expressiveness;
        Squint = accent * _style.Squint * 0.35f * expressiveness;
        Sneer = accent * _style.Sneer * 0.3f * expressiveness;
        EyeWide = accent * _style.Wide * 0.4f * expressiveness;
    }

    /// <summary>The beat spring's target now; <paramref name="overshoot"/> when an "overshooting" beat is pushing it.</summary>
    private Vector3 EvaluateBeats(out bool overshoot)
    {
        Vector3 target = Vector3.zero;
        overshoot = false;
        foreach (HeadBeat beat in _beats)
        {
            float d = _clock - beat.Time;
            if (d < -BeatLead || d > BeatLength + 0.25f) continue;
            overshoot |= beat.Overshoot;   // Lower damping through the beat and its settle
            if (d > BeatLength) continue;
            target.x += beat.Pitch;
            target.y += beat.Yaw;
        }
        return target;
    }

    private Vector3 EvaluatePosture()
    {
        Vector3 pose = Vector3.zero;
        for (int i = _shifts.Count - 1; i >= 0; i--)
        {
            if (_shifts[i].Time > _clock) continue;
            pose = _shifts[i].Pose;
            break;
        }
        pose.x += _style.BasePitch;

        // Higher pitch lifts the chin a little (low-passed by the posture spring); unvoiced stretches hold it
        if (_audioLine && _track != null && _clock <= _lineEnd)
        {
            float semitones = _track.PitchAt(_clock);
            if (!float.IsNaN(semitones))
                _lastPitchDrift = Mathf.Clamp(-semitones * PitchDriftPerSemitone, -MaxPitchDrift, MaxPitchDrift) * _style.Head;
            pose.x += _lastPitchDrift;
        }

        if (_question || _whQuestion)
        {
            float q = Ramp(_clock, _questionFrom, _questionFrom + 0.35f) * (1f - Ramp(_clock, _lineEnd + 0.8f, _lineEnd + LineTail));
            pose.x -= (_question ? 2f : 0.5f) * q;   // Chin up
            pose.z += 2.5f * q * Mathf.Sign(_sideBias + 0.001f);
        }
        else if (_audioLine)
        {
            // Statements settle down a little on their final word
            pose.x += 1.5f * Ramp(_clock, _lineEnd - 0.35f, _lineEnd) * (1f - Ramp(_clock, _lineEnd + 0.6f, _lineEnd + LineTail));
        }

        if (_clock > _lineEnd)
            pose *= 1f - Ramp(_clock, _lineEnd + 0.6f, _lineEnd + LineTail) * 0.85f;
        return pose;
    }

    private void FireBlinks()
    {
        while (_nextBlink < _blinks.Count && _blinks[_nextBlink] <= _clock)
        {
            _nextBlink++;
            _blinkRequested = true;
        }
    }

    // ---- Gaze ----

    private void UpdateGaze(float now, bool thinking, FaceEmotion emotion)
    {
        Style idle = _lineActive ? _style : StyleFor(emotion);

        if (_endAversion)
        {
            _endAversion = false;
            if (_gazeMode == GazeMode.Away && !thinking) _gazeUntil = now;
        }

        // A planned turn-start aversion owns the eyes while it lasts
        if (_lineActive && _plannedAversionStart >= 0f && _clock >= _plannedAversionStart)
        {
            if (_clock < _plannedAversionEnd)
            {
                if (_gazeMode != GazeMode.Away || _gazeTo != _plannedAversion)
                    StartSaccade(now, GazeMode.Away, _plannedAversion, now + (_plannedAversionEnd - _clock));
            }
            else
            {
                _plannedAversionStart = -1f;
                StartSaccade(now, GazeMode.Mutual, ScanPoint(), now + RandomRange(0.4f, 1.2f));
            }
        }
        else if (thinking && !_wasThinking)
        {
            // Thinking: a long look away (no single direction: up, down or to the side)
            StartSaccade(now, GazeMode.Away, AversionOffset(0.29f, 0.39f, RandomRange(9f, 15f)),
                now + Gaussian(3.5f, 1.3f, 1.5f, 5f));
        }
        else if (now >= _gazeUntil)
        {
            if (_gazeMode == GazeMode.Away)
            {
                StartSaccade(now, GazeMode.Mutual, ScanPoint(), now + (thinking ? RandomRange(0.6f, 1.5f) : RandomRange(0.4f, 1.5f)));
            }
            else if (thinking)
            {
                StartSaccade(now, GazeMode.Away, AversionOffset(0.29f, 0.39f, RandomRange(9f, 15f)),
                    now + Gaussian(3.5f, 1.3f, 1.5f, 5f));
            }
            else if (_lineActive && _clock >= _nextIntimacyAversion && _clock < _lineEnd - 2.4f)
            {
                // Back in mutual gaze at least 0.4 s before the last word, whatever the emotion's aversion length
                float hold = Mathf.Min(RandomRange(1f, 2f) * Mathf.Clamp(idle.Aversion, 0.6f, 1.4f), _lineEnd - 0.4f - _clock);
                StartSaccade(now, GazeMode.Away, AversionOffset(0.29f, 0.14f + 0.3f * idle.DownBias, RandomRange(6f, 12f)),
                    now + hold);
                _nextIntimacyAversion = _clock + Gaussian(4.75f, 1.4f, 3f, 7f) / Mathf.Max(0.3f, idle.Aversion);
            }
            else if (!_lineActive && Random01() < 0.12f * Mathf.Max(0.2f, idle.Aversion))
            {
                // Listening: a brief glance away now and then (otherwise keep scanning the face)
                StartSaccade(now, GazeMode.Away, AversionOffset(0.29f, 0.14f + 0.3f * idle.DownBias, RandomRange(4f, 10f)),
                    now + Gaussian(0.6f, 0.25f, 0.3f, 1.2f));
            }
            else
            {
                StartSaccade(now, GazeMode.Mutual, ScanPoint(), now + RandomRange(0.4f, 1.5f));
            }
        }
        _wasThinking = thinking;

        // Saccade: 25 ms + 2.4 ms per degree, fast start and soft landing
        float p = _saccadeLength > 0f ? Mathf.Clamp01((now - _saccadeStart) / _saccadeLength) : 1f;
        float eased = 1f - (1f - p) * (1f - p) * (1f - p);
        _gazeNow = Vector2.LerpUnclamped(_gazeFrom, _gazeTo, eased);
    }

    private void StartSaccade(float now, GazeMode mode, Vector2 to, float until)
    {
        float amplitude = Vector2.Distance(_gazeNow, to);
        _gazeFrom = _gazeNow;
        _gazeTo = to;
        _gazeMode = mode;
        _gazeUntil = until;
        _saccadeStart = now;
        _saccadeLength = 0.025f + 0.0024f * amplitude;

        // Big gaze shifts usually carry a blink with them
        if (amplitude >= 12f && Random01() < 0.5f) _blinkRequested = true;
    }

    /// <summary>The player's left eye, right eye or mouth, as small offsets from the look-at point.</summary>
    private Vector2 ScanPoint()
    {
        float r = Random01();
        if (r < 0.42f) return new Vector2(-ScanEyeDegrees, 0f);
        if (r < 0.84f) return new Vector2(ScanEyeDegrees, 0f);
        return new Vector2(0f, ScanMouthDegrees);
    }

    /// <summary>
    /// A look-away offset: up, down or to the side (with this NPC's side bias), about <paramref name="degrees"/> long
    /// (scaled by expressiveness, within limits that keep a calm NPC's glance readable and a lively one's natural).
    /// </summary>
    private Vector2 AversionOffset(float upShare, float downShare, float degrees)
    {
        degrees *= Mathf.Clamp(Expressiveness, 0.5f, 1.25f);
        float r = Random01();
        float side = Random01() < 0.5f + 0.3f * _sideBias ? 1f : -1f;
        Vector2 direction;
        if (r < upShare) direction = new Vector2(side * RandomRange(0.2f, 0.6f), -1f);
        else if (r < upShare + downShare) direction = new Vector2(side * RandomRange(0.2f, 0.6f), 1f);
        else direction = new Vector2(side, RandomRange(-0.25f, 0.25f));
        return direction.normalized * degrees;
    }

    // ================================================================
    // Styles
    // ================================================================

    private static Style StyleFor(FaceEmotion emotion)
    {
        var s = new Style
        {
            Head = 1f, Speed = 1f, Damping = BeatDamping, Brow = BrowKind.Raise, BrowRate = 1f,
            Blink = 1f, Aversion = 1f, DownBias = 0f
        };
        switch (emotion)
        {
            case FaceEmotion.Content:
            case FaceEmotion.Smug:
                s.Head = 1.15f; s.Speed = 1.2f; s.Squint = 0.6f; s.Aversion = 0.9f;
                break;
            case FaceEmotion.Happy:
            case FaceEmotion.Delighted:
                s.Head = 1.45f; s.Speed = 1.6f; s.Damping = 0.55f; s.Squint = 1f; s.BrowRate = 1f; s.Blink = 1.2f; s.Aversion = 0.85f;
                break;
            case FaceEmotion.Grumpy:
                s.Head = 1.2f; s.Speed = 1.3f; s.Damping = 0.7f; s.Brow = BrowKind.Knit; s.Sneer = 0.4f; s.Aversion = 0.7f; s.BasePitch = 0.8f;
                break;
            case FaceEmotion.Angry:
                s.Head = 1.5f; s.Speed = 1.9f; s.Damping = 0.7f; s.Brow = BrowKind.Knit; s.Sneer = 0.8f; s.Blink = 1.2f; s.Aversion = 0.5f; s.BasePitch = 1.2f;
                break;
            case FaceEmotion.Suspicious:
                s.Head = 0.8f; s.Speed = 0.9f; s.Brow = BrowKind.Knit; s.BrowRate = 0.7f; s.Squint = 0.6f; s.Aversion = 0.8f;
                break;
            case FaceEmotion.Disgusted:
                s.Head = 0.9f; s.Brow = BrowKind.Knit; s.BrowRate = 0.5f; s.Sneer = 1f; s.Aversion = 1.2f; s.BasePitch = -0.8f;
                break;
            case FaceEmotion.Worried:
                s.Head = 1.1f; s.Brow = BrowKind.Worry; s.Aversion = 1.3f; s.DownBias = 0.5f;
                break;
            case FaceEmotion.Sad:
                s.Head = 0.9f; s.Speed = 0.7f; s.Damping = 0.9f; s.Brow = BrowKind.Worry; s.BrowRate = 0.5f; s.FirstAccentOnly = true;
                s.Blink = 0.8f; s.Aversion = 1.4f; s.DownBias = 1f; s.BasePitch = 2f;
                break;
            case FaceEmotion.Scared:
                s.Head = 1.25f; s.Speed = 1.5f; s.Damping = 0.7f; s.Brow = BrowKind.Worry; s.Wide = 0.7f; s.Blink = 1.3f; s.Aversion = 1.5f; s.BasePitch = -0.5f;
                break;
            case FaceEmotion.Surprised:
                s.Head = 1.15f; s.Speed = 1.3f; s.BrowRate = 1f; s.FirstAccentOnly = true; s.Wide = 1f; s.Blink = 0.8f; s.BasePitch = -0.6f;
                break;
        }
        return s;
    }

    // ================================================================
    // Helpers
    // ================================================================

    /// <summary>Brow apex 0.2 (a plain accent) to 0.45 (the strongest), before expressiveness.</summary>
    private static float BrowAmount(float strength)
    {
        return Mathf.Lerp(0.2f, 0.45f, Mathf.InverseLerp(0.35f, 1f, strength));
    }

    /// <summary>0..1 brow beat shape at <paramref name="d"/> seconds from the accented vowel.</summary>
    private static float BrowEnvelope(float d)
    {
        if (d < -BrowLead || d > BrowHold + BrowRelease) return 0f;
        if (d < 0f) return Mathf.SmoothStep(0f, 1f, (d + BrowLead) / BrowLead);
        if (d <= BrowHold) return 1f;
        return 1f - Mathf.SmoothStep(0f, 1f, (d - BrowHold) / BrowRelease);
    }

    private static float Ramp(float x, float from, float to)
    {
        return to > from ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((x - from) / (to - from))) : (x >= to ? 1f : 0f);
    }

    /// <summary>Damped spring toward <paramref name="target"/>, in sub-steps so a slow frame stays stable.</summary>
    private static void StepSpring(ref Vector3 x, ref Vector3 v, Vector3 target, float hz, float damping, float dt)
    {
        float omega = 2f * Mathf.PI * hz;
        int steps = Mathf.Max(1, Mathf.CeilToInt(dt / (1f / 120f)));
        float h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            Vector3 a = omega * omega * (target - x) - 2f * damping * omega * v;
            v += a * h;
            x += v * h;
        }
    }

    /// <summary>The final sentence of <paramref name="text"/> (after the last . ! ? before its end).</summary>
    private static string LastSentence(string text)
    {
        int end = text.Length - 1;
        while (end >= 0 && (text[end] == '?' || text[end] == '!' || text[end] == '.' || text[end] == '"')) end--;
        int start = text.LastIndexOfAny(new[] { '.', '!', '?' }, Mathf.Max(0, end));
        return start >= 0 ? text.Substring(start + 1) : text;
    }

    private static bool StartsWithWhWord(string text)
    {
        string lower = text.TrimStart('"', '\'', ' ').ToLowerInvariant();
        string[] words = { "who", "what", "where", "when", "why", "how", "which", "whose", "whom" };
        foreach (string w in words)
        {
            if (lower.StartsWith(w) && (lower.Length == w.Length || !char.IsLetter(lower[w.Length])))
                return true;
        }
        return false;
    }

    /// <summary>xorshift32, uniform in [0, 1).</summary>
    private float Random01()
    {
        uint x = _rng;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rng = x;
        return (x & 0xFFFFFFu) / 16777216f;
    }

    private float RandomRange(float min, float max) => min + (max - min) * Random01();

    /// <summary>Normal(mean, sd) clamped to [min, max] (Box-Muller).</summary>
    private float Gaussian(float mean, float sd, float min, float max)
    {
        float u1 = Mathf.Max(1e-6f, Random01());
        float u2 = Random01();
        float z = Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        return Mathf.Clamp(mean + sd * z, min, max);
    }
}
