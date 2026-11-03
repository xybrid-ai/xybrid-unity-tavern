using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// One Rhubarb Lip Sync mouth cue: <see cref="shape"/> (A..H, or X for rest) held from
/// <see cref="start"/> to <see cref="end"/> seconds into the clip.
/// </summary>
[System.Serializable]
public struct LipSyncCue
{
    public float start;
    public float end;
    public char shape;
}

/// <summary>
/// Per-frame blend shape weights for one spoken clip (e.g. from Audio2Face), sampled at <see cref="Fps"/> from the
/// clip's start: <see cref="Weights"/>[frame * Names.Length + shape], 0..1, names without the "MESHBlends." prefix.
/// </summary>
public sealed class FaceTrack
{
    public readonly float Fps;
    public readonly string[] Names;
    public readonly float[] Weights;
    public readonly int Frames;

    public FaceTrack(float fps, string[] names, float[] weights)
    {
        Fps = fps > 0f ? fps : 30f;
        Names = names ?? new string[0];
        Weights = weights ?? new float[0];
        Frames = Names.Length > 0 ? Weights.Length / Names.Length : 0;
    }
}

/// <summary>
/// Drives an NPC's face in five layers, all written once per frame in LateUpdate as offsets on top of the
/// character's sculpted rest weights and the animated pose:
/// (1) expression: the base mood, replaced by a transient emotion (<see cref="ShowEmotion"/>) or the
/// Thinking face while the Animator's isThinking is on, with the library's idle face capture looping on top;
/// (2) blinks, at random and on the speech's pauses and line ends; (3) mouth: TTS audio given to
/// <see cref="Speak"/> (Rhubarb cues once they arrive, the clip's loudness until then, or a face track such as
/// Audio2Face's given to <see cref="SetFaceTrack"/>, which takes over the lower face), procedural babble
/// while isTalking is on without audio (table chatter), otherwise rest; (4) speech gestures (<see cref="FaceGestures"/>):
/// brow beats, Duchenne squints and sneers on the line's accents (found by <see cref="SpeechProsody"/>), head
/// beats, posture shifts and question tilts on the neck and head bones, and gaze (look-aways, face scanning) on
/// the eye bones, which also counter-rotate against the head motion so the look-at target holds; (5) the jaw bone,
/// held at its closed bind pose (clips without a jaw curve leave a humanoid jaw open) plus an optional opening.
/// Body-shape blend shapes (defaultHeavy, defaultBuff, defaultSkinny, masculineFeminine) are never written.
/// Attach to the NPC root, next to the Animator.
/// </summary>
[DefaultExecutionOrder(200)]
[DisallowMultipleComponent]
public class NPCFace : MonoBehaviour
{
    private const string ShapePrefix = "MESHBlends.";
    private const string IsTalkingParam = "isTalking";
    private const string IsThinkingParam = "isThinking";
    private static readonly int IsTalkingHash = Animator.StringToHash(IsTalkingParam);
    private static readonly int IsThinkingHash = Animator.StringToHash(IsThinkingParam);

    // Character-shape blend shapes on the same mesh; writing them would reshape the body
    private static readonly string[] BodyShapes = { "defaultHeavy", "defaultBuff", "defaultSkinny", "masculineFeminine" };

    // Mouth shapes driven by the lip-sync layer (columns of CuePoses)
    private const int MJaw = 0, MClose = 1, MPressL = 2, MPressR = 3, MStretchL = 4, MStretchR = 5, MFunnel = 6,
                      MPucker = 7, MRollLower = 8, MRollUpper = 9, MLowerDownL = 10, MLowerDownR = 11,
                      MUpperUpL = 12, MUpperUpR = 13, MTongueUp = 14, MTongueOut = 15, MouthCount = 16;
    private static readonly string[] MouthShapeNames =
    {
        "jawOpen", "mouthClose", "mouthPressLeft", "mouthPressRight", "mouthStretchLeft", "mouthStretchRight",
        "mouthFunnel", "mouthPucker", "mouthRollLower", "mouthRollUpper", "mouthLowerDownLeft", "mouthLowerDownRight",
        "mouthUpperUpLeft", "mouthUpperUpRight", "tongueUp", "tongueOut"
    };

    // Rhubarb cue rows: A..H, then X (rest)
    private const int CueA = 0, CueB = 1, CueC = 2, CueD = 3, CueE = 4, CueF = 5, CueX = 8, CueCount = 9;

    // Mouth pose per Rhubarb cue, 0..1, after Rhubarb's shape guide and JALI (jaw and lips move separately).
    // Lips meet by pressing (A; G adds a raised upper lip so the upper teeth show over the lower one), never by
    // mouthClose alone: on the Sidekick rig that corrective only makes sense against an open jaw (Compose caps it
    // to one). mouthRollLower is left out: it folds a second dark crease under the lower lip.
    // B keeps the teeth together with the lips parted; rounded E/F stay soft (a 0.8 pucker read as a kiss); H lifts
    // the tongue behind the teeth (any tongueOut pokes it past them on the Sidekick rig).
    private static readonly float[,] CuePoses =
    {
        //  jaw   close pressL pressR strL  strR  funnel pucker rollLo rollUp lowL  lowR  upL   upR   tUp   tOut
        { 0f,    0f,   .45f,  .45f,  0f,   0f,   0f,    0f,    0f,    .05f,  0f,   0f,   0f,   0f,   0f,   0f   }, // A  closed (M, B, P)
        { .08f,  0f,   0f,    0f,    .25f, .25f, 0f,    0f,    0f,    0f,    .35f, .35f, .2f,  .2f,  0f,   0f   }, // B  teeth together (K, S, T, EE)
        { .36f,  0f,   0f,    0f,    .25f, .25f, 0f,    0f,    0f,    0f,    .25f, .25f, .08f, .08f, 0f,   0f   }, // C  open (EH, AE)
        { .66f,  0f,   0f,    0f,    .08f, .08f, 0f,    0f,    0f,    0f,    .25f, .25f, .08f, .08f, 0f,   0f   }, // D  wide open (AA)
        { .3f,   0f,   0f,    0f,    0f,   0f,   .45f,  .12f,  0f,    0f,    .1f,  .1f,  0f,   0f,   0f,   0f   }, // E  rounded (AO, ER)
        { .14f,  .3f,  0f,    0f,    0f,   0f,   .22f,  .5f,   0f,    0f,    0f,   0f,   0f,   0f,   0f,   0f   }, // F  puckered (UW, OW, W)
        { .06f,  0f,   .2f,   .2f,   0f,   0f,   0f,    0f,    0f,    0f,    0f,   0f,   .4f,  .4f,  0f,   0f   }, // G  teeth on lip (F, V)
        { .3f,   0f,   0f,    0f,    0f,   0f,   0f,    0f,    0f,    0f,    .15f, .15f, 0f,   0f,   .4f,  0f   }, // H  tongue up (L)
        { 0f,    0f,   0f,    0f,    0f,   0f,   0f,    0f,    0f,    0f,    0f,   0f,   0f,   0f,   0f,   0f   }, // X  rest
    };

    private const float RestingMouthOpenScale = 0.15f;   // Expressions: keep only a hint of lip-parting shapes
    private const float MouthCloseCancelsJaw = 0.37f;    // Sidekick: 1.0 mouthClose seals the lips over 0.37 jawOpen
    private const float CueLeadSeconds = 0.03f;          // Mouths shape a sound slightly before it is heard
    private const float CueBlendSeconds = 0.04f;         // Between consecutive Rhubarb cues
    private const float CueArrivalBlendSeconds = 0.15f;  // Loudness jaw -> cue poses once cues arrive
    private const float TrackBlendSeconds = 0.15f;       // Cue mouth <-> face track
    private const float EnvelopeHop = 0.01f;             // Loudness envelope resolution (seconds)
    private const float SpeechStartGrace = 0.5f;         // Speak() before the source starts playing
    private const float AudioMouthSmoothing = 0.02f;     // Time constants (seconds) of the mouth follow
    private const float BabbleMouthSmoothing = 0.045f;
    private const float RestMouthSmoothing = 0.06f;
    private const float MouthActiveFadeSeconds = 0.2f;   // Lip-safety scaling of expression mouth shapes
    private const float BlinkRefractorySeconds = 0.6f;   // Speech-timed blinks never follow another this closely
    private const float SpeakingBlinkRate = 1.25f;       // Random blinks come this much more often while talking
    private const float NeckShare = 0.4f;                // Of each head gesture, the rest turns the head bone
    private const float EmphasisOpen = 0.25f;            // Extra opening on accented syllables
    private const float MinStylingIntensity = 0.45f;     // Weaker line emotions keep the base mood's gesture style
    private const float BindRetrySeconds = 1f;
    private const int MaxBindAttempts = 5;

    [Header("References")]
    [Tooltip("Auto-found on this GameObject (or its children) when empty. Supplies isTalking / isThinking and the jaw bone.")]
    [SerializeField] private Animator animator;
    [Tooltip("Face mesh. Auto-found when empty: the child SkinnedMeshRenderer with the most 'MESHBlends.' blend shapes.")]
    [SerializeField] private SkinnedMeshRenderer faceMesh;
    [Tooltip("Expression weights baked from the Synty face poses (Tavern > Bake Face Expression Library). " +
             "A small built-in table is used when empty.")]
    [SerializeField] private FaceExpressionLibrary library;

    [Header("Mood")]
    [Tooltip("Resting expression that fits this character.")]
    [SerializeField] private FaceEmotion baseMood = FaceEmotion.Neutral;
    [SerializeField, Range(0f, 1f)] private float baseMoodStrength = 0.45f;
    [Tooltip("Strength of the Thinking face while the Animator's isThinking is on.")]
    [SerializeField, Range(0f, 1f)] private float thinkingStrength = 1f;
    [Tooltip("Seconds to blend into a transient emotion or the Thinking face.")]
    [SerializeField] private float emotionFadeIn = 0.3f;
    [Tooltip("Seconds to release back to the base mood.")]
    [SerializeField] private float emotionFadeOut = 0.8f;
    [Tooltip("Strength of the library's idle face capture (brows, nose, cheeks and lips drifting a little), looped " +
             "on top of every expression so a resting face is never frozen. 0 = off.")]
    [SerializeField, Range(0f, 1.5f)] private float ambientMotion = 0.8f;

    [Header("Blinks")]
    [Tooltip("Seconds between blinks, picked randomly in this range (x = min, y = max).")]
    [SerializeField] private Vector2 blinkInterval = new Vector2(2.2f, 5.5f);
    [Tooltip("Seconds for one close-and-open, picked randomly in this range (a fast close, a slower opening).")]
    [SerializeField] private Vector2 blinkDuration = new Vector2(0.2f, 0.28f);
    [SerializeField, Range(0f, 1f)] private float doubleBlinkChance = 0.15f;

    [Header("Mouth")]
    [Tooltip("Scales every mouth layer (lip-sync cues, loudness jaw, babble). Stylised faces and beards read as " +
             "over-acted above ~0.6.")]
    [SerializeField, Range(0f, 1f)] private float mouthAmplitude = 0.55f;
    [Tooltip("Opening shapes are scaled by the line's loudness at that moment, from this floor (quiet) to 1 (loud), " +
             "so soft syllables and word ends nearly close instead of holding the cue pose.")]
    [SerializeField, Range(0f, 1f)] private float quietOpenFloor = 0.25f;
    [Tooltip("jawOpen at full loudness while TTS audio plays without Rhubarb cues (yet).")]
    [SerializeField] private float loudnessJaw = 0.55f;
    [Tooltip("Mouth movement while the Animator's isTalking is on but no audio plays for this face (table chatter).")]
    [SerializeField, Range(0f, 1f)] private float babbleAmount = 0.7f;
    [Tooltip("Babble syllables per second, picked randomly per syllable in this range.")]
    [SerializeField] private Vector2 babbleRate = new Vector2(4f, 6f);
    [Tooltip("Babble stops after isTalking has been on this long (covers TableConversation's longest turn). 0 = no limit.")]
    [SerializeField] private float maxBabbleSeconds = 8f;
    [Tooltip("Babble limit while this NPC is in dialogue with the player: DialogueManagerV2 leaves isTalking on " +
             "after the line is shown, and TTS audio (when available) takes over the mouth anyway. 0 = no limit.")]
    [SerializeField] private float dialogueBabbleSeconds = 4f;
    [Tooltip("Expression mouth/jaw/cheekPuff shapes are scaled by this while the mouth moves, so lip shapes stay readable.")]
    [SerializeField, Range(0f, 1f)] private float speakingExpressionMouth = 0.4f;

    [Header("Speech Gestures")]
    [Tooltip("Overall size of brow beats, head motion and look-aways (1 = a typical speaker; calm or muttering " +
             "characters lower, animated ones higher). Look-aways scale within 0.5-1.25 of their natural size.")]
    [SerializeField, Range(0f, 2f)] private float expressiveness = 1f;
    [Tooltip("Brow beats, nods, blinks and glances timed to each voiced line's emphasis and pauses (and to babble).")]
    [SerializeField] private bool speechGestures = true;
    [Tooltip("Move the neck and head bones: beats, posture shifts, question tilts.")]
    [SerializeField] private bool headMotion = true;
    [Tooltip("Move the eye bones: look-aways while talking or thinking and small saccades across the listener's face. " +
             "They also hold their target while the head moves.")]
    [SerializeField] private bool eyeMotion = true;

    [Header("Jaw Bone")]
    [Tooltip("Hold the humanoid Jaw bone at the avatar's closed bind pose every frame. Clips without a jaw curve " +
             "(the Mixamo ones) otherwise leave a Sidekick jaw about 10° open, so the lips never meet.")]
    [SerializeField] private bool holdJawClosed = true;
    [Tooltip("Degrees the humanoid Jaw bone opens at full jawOpen, about its local X axis (from the closed pose, " +
             "or on top of the animation when Hold Jaw Closed is off). 0 = off.")]
    [SerializeField] private float jawBoneDegrees = 0f;

    [Header("Debug")]
    [SerializeField] private bool logEvents = false;

    // ---- Binding ----
    private SkinnedMeshRenderer _face;
    private Mesh _boundMesh;
    private bool _bound;
    private int _bindAttempts;
    private float _nextBindAttempt;
    private int _channelCount;
    private int[] _shapeIndex;      // Blend shape index on the mesh, per channel
    private float[] _rest;          // Rest weight (0..100) captured at bind
    private float[] _written;       // Last weight written, to skip unchanged writes
    private bool[] _isMouthChannel; // mouth*, jaw*, cheekPuff*: scaled while the mouth layer is active
    private bool[] _isLowerFace;    // mouth channels plus tongue*: what a face track drives
    private bool[] _opensMouth;     // Shapes that part the lips: damped in expressions (a held grin reads as mid-sentence)
    private bool[] _isEyeWide;      // eyeWide*: suppressed while blinking
    private float[] _add;           // Blink + mouth offsets of the current frame
    private readonly Dictionary<string, int> _channelByName = new Dictionary<string, int>(System.StringComparer.Ordinal);
    private readonly int[] _mouthChannel = new int[MouthCount];
    private int _blinkUpperL = -1, _blinkUpperR = -1, _blinkLowerL = -1, _blinkLowerR = -1;
    private Transform _jawBone;
    private NPCIdentity _identity;

    // ---- Animator flags ----
    private RuntimeAnimatorController _checkedController;
    private bool _hasTalkingParam;
    private bool _hasThinkingParam;

    // ---- Expression layer ----
    private enum ExprKind { Base, Transient, Thinking }
    private static readonly int EmotionCount = System.Enum.GetValues(typeof(FaceEmotion)).Length;
    private int[][] _emotionChannels;   // Sparse weights per emotion, resolved to channels at bind
    private float[][] _emotionWeights;
    private FaceExpressionLibrary _builtLibrary;
    private float[] _exprFrom, _exprTarget, _exprState;
    private bool _exprStarted;
    private ExprKind _exprKind;
    private FaceEmotion _exprEmotion;
    private int _exprSerial;
    private float _fadeStart;
    private float _fadeDuration;
    private bool _transientActive;
    private FaceEmotion _transientEmotion;
    private float _transientIntensity;
    private float _transientEnd;
    private int _transientSerial;

    // ---- Ambient motion (looping idle capture) ----
    private int[] _ambientChannels;
    private float[][] _ambientSamples;
    private int _ambientCount;          // Samples per channel (one cycle)
    private float _ambientRate;
    private float _ambientPhase;        // Seconds into the cycle at time 0, per instance
    private float _ambientSpeed = 1f;   // Per instance, so NPCs don't twitch in step
    private float[] _ambient;           // This frame's offsets per channel

    // ---- Blink layer ----
    private bool _blinking;
    private float _blinkStart;
    private float _blinkLength;
    private float _nextBlinkTime;
    private bool _doubleAfterThis;
    private bool _secondBlinkQueued;

    // ---- Mouth layer ----
    private readonly float[] _mouthTarget = new float[MouthCount];
    private readonly float[] _mouthState = new float[MouthCount];
    private float _mouthActive;     // 0..1, eased

    // Audio speech
    private bool _hasSpeech;
    private AudioSource _speechSource;
    private AudioClip _speechClip;
    private float _speechGivenAt;
    private bool _speechSeenPlaying;
    private LipSyncCue[] _cues;
    private float _cuesArrivedAt = -1f;
    private float[] _envelope;
    private float[] _envelopeSort;
    private int _envelopeLength;
    private float[] _pcmChunk;
    private readonly float[] _outputBuffer = new float[256];
    private float _livePeak = 0.05f;

    // Face track (Audio2Face): takes over the lower face of the line it belongs to
    private FaceTrack _track;
    private int[] _trackNames;      // Index into the track's names, per driven shape
    private int[] _trackChannels;   // Channel of each driven shape
    private float[] _trackValues;   // This frame's track weights per channel (held while the mix fades out)
    private float _trackMix;        // 0..1, eased

    // Babble
    private bool _wasTalking;
    private float _talkStart;
    private bool _stretchHadAudio;
    private bool _firstSyllable;
    private int _syllablesLeftInWord;
    private float _syllableStart;
    private float _syllableLength;
    private bool _syllableIsGap;
    private int _syllableShape = CueX;
    private float _syllableAmp;

    // ---- Jaw bone ----
    private bool _hasJawClosed;
    private Quaternion _jawClosed;  // Local rotation of the closed jaw (bind pose)
    private bool _jawWritten;
    private Quaternion _jawBase;
    private Quaternion _jawLastWritten;

    // ---- Speech gestures ----
    private FaceGestures _gestures;
    private Task<ProsodyTrack> _prosodyTask;
    private AudioClip _prosodyClip;
    private string _speechText;
    private bool _gestureLine;          // A voiced line's gestures are running
    private bool _babbleGestures;       // Babble's gestures are running
    private float _asymmetry;           // Left/right gain difference of gesture shapes, per instance
    private GestureShape[] _raiseShapes, _knitShapes, _worryShapes, _squintShapes, _sneerShapes, _wideShapes;
    private BoneOffset _neck, _head, _eyeL, _eyeR;
    private float _lastBlinkEnd = -10f;

    /// <summary>A blend shape a gesture moves: channel, weight and side (-1 left, 0 both, 1 right).</summary>
    private struct GestureShape
    {
        public int Channel;
        public float Weight;
        public int Side;
    }

    /// <summary>
    /// A bone this component rotates on top of the animation. Remembers the animated rotation so a culled or
    /// paused Animator (which doesn't rewrite the bone) never stacks our offset on our own last write.
    /// </summary>
    private struct BoneOffset
    {
        public Transform Bone;
        private bool _written;
        private Quaternion _base, _last;

        public void Bind(Transform bone)
        {
            Restore();
            Bone = bone;
        }

        public void BeginFrame()
        {
            if (Bone == null) return;
            if (_written && Bone.localRotation == _last) Bone.localRotation = _base;
            _base = Bone.localRotation;
        }

        public void EndFrame()
        {
            if (Bone == null) return;
            _last = Bone.localRotation;
            _written = true;
        }

        public void Restore()
        {
            if (_written && Bone != null && Bone.localRotation == _last) Bone.localRotation = _base;
            _written = false;
        }
    }

    // ---- Per-instance randomness (keeps NPCs out of sync without touching UnityEngine.Random's sequence) ----
    private uint _rngState = 0x9E3779B9u;
    private float _noiseSeed;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>Resting expression, shown at baseMoodStrength. Changing it crossfades to the new mood.</summary>
    public FaceEmotion BaseMood
    {
        get => baseMood;
        set => baseMood = value;
    }

    /// <summary>True while the clip given to <see cref="Speak"/> is playing on its AudioSource.</summary>
    public bool IsSpeakingAudio =>
        _hasSpeech && _speechSource != null && _speechClip != null &&
        _speechSource.clip == _speechClip && _speechSource.isPlaying;

    /// <summary>
    /// Drives the mouth from <paramref name="clip"/> while it plays on <paramref name="source"/> (time follows
    /// the source). The loudness envelope is computed here, on the main thread, so the jaw moves immediately;
    /// pass Rhubarb cues later with <see cref="SetLipSyncCues"/>.
    /// </summary>
    public void Speak(AudioSource source, AudioClip clip, string text)
    {
        ClearSpeech();
        if (source == null || clip == null) return;

        _hasSpeech = true;
        _speechSource = source;
        _speechClip = clip;
        _speechGivenAt = Time.unscaledTime;
        _speechSeenPlaying = source.clip == clip && source.isPlaying;
        _stretchHadAudio = true;  // Audio owns this line's mouth; no babble once it ends
        _livePeak = 0.05f;

        ComputeEnvelope(clip);
        StartProsody(clip, text);

        if (logEvents)
        {
            Debug.Log($"[NPCFace] {name} speaks {clip.length:F1}s " +
                      $"(envelope: {(_envelopeLength > 0 ? _envelopeLength + " hops" : "live")}): \"{text}\"");
        }
    }

    /// <summary>
    /// Switches the mouth of the current line to Rhubarb cue poses (crossfading from the loudness jaw).
    /// Ignored when <paramref name="clip"/> is not the clip currently given to <see cref="Speak"/>.
    /// </summary>
    public void SetLipSyncCues(AudioClip clip, LipSyncCue[] cues)
    {
        if (!_hasSpeech || clip == null || clip != _speechClip) return;
        if (cues == null || cues.Length == 0) return;

        LipSyncCue[] clean = SanitizeCues(cues);
        if (clean == null) return;

        // Only the first cues of a line fade in from the loudness jaw; a refinement (spectral -> Rhubarb) swaps in place
        if (_cues == null) _cuesArrivedAt = Time.unscaledTime;
        _cues = clean;

        if (logEvents)
            Debug.Log($"[NPCFace] {name} received {clean.Length} lip-sync cues");
    }

    /// <summary>
    /// Hands the current line's lower face (mouth, jaw, cheek puff, tongue) to <paramref name="track"/>, crossfading
    /// from the cue mouth; brows, eyes, blinks and head gestures stay NPCFace's own. Ignored when
    /// <paramref name="clip"/> is not the clip currently given to <see cref="Speak"/> or the face isn't bound yet.
    /// </summary>
    public void SetFaceTrack(AudioClip clip, FaceTrack track)
    {
        if (!_hasSpeech || clip == null || clip != _speechClip || !_bound) return;
        if (track == null || track.Frames == 0) return;

        var names = new List<int>(track.Names.Length);
        var channels = new List<int>(track.Names.Length);
        for (int i = 0; i < track.Names.Length; i++)
        {
            int c = Channel(StripPrefix(track.Names[i]));
            if (c < 0 || !_isLowerFace[c] || channels.Contains(c)) continue;
            names.Add(i);
            channels.Add(c);
        }
        if (channels.Count == 0) return;

        System.Array.Clear(_trackValues, 0, _trackValues.Length);
        _trackNames = names.ToArray();
        _trackChannels = channels.ToArray();
        _track = track;

        if (logEvents)
            Debug.Log($"[NPCFace] {name} face track: {track.Frames} frames at {track.Fps:0} fps, {channels.Count} shapes, " +
                      $"from {SpeechTime():0.00}s");
    }

    /// <summary>Stops driving the mouth from audio; it eases back to rest. Does not stop the AudioSource.</summary>
    public void StopSpeaking()
    {
        ClearSpeech();
    }

    /// <summary>
    /// Shows a transient emotion: crossfades in over emotionFadeIn, holds for <paramref name="holdSeconds"/>,
    /// then releases back to the base mood over emotionFadeOut. Intensity 0 releases immediately.
    /// </summary>
    public void ShowEmotion(FaceEmotion emotion, float intensity, float holdSeconds)
    {
        if (float.IsNaN(intensity) || intensity <= 0.001f)
        {
            _transientActive = false;
            return;
        }
        if (float.IsNaN(holdSeconds) || holdSeconds < 0f) holdSeconds = 0f;

        _transientEmotion = emotion;
        _transientIntensity = Mathf.Clamp01(intensity);
        _transientEnd = Time.time + Mathf.Max(0f, emotionFadeIn) + holdSeconds;
        _transientActive = true;
        _transientSerial++;

        if (logEvents)
            Debug.Log($"[NPCFace] {name} shows {emotion} at {_transientIntensity:F2} for {holdSeconds:F1}s");
    }

    // ================================================================
    // Unity lifecycle
    // ================================================================

    private void Awake()
    {
        if (animator == null && !TryGetComponent(out animator))
            animator = GetComponentInChildren<Animator>();

        if (!TryGetComponent(out _identity))
            _identity = GetComponentInParent<NPCIdentity>();

        SeedRandom();
        for (int m = 0; m < MouthCount; m++) _mouthChannel[m] = -1;
        _gestures = new FaceGestures((uint)(NextRandom() * uint.MaxValue) | 1u);
        _asymmetry = RandomRange(-0.12f, 0.12f);
    }

    private void OnEnable()
    {
        // Stagger the first blink so a room of NPCs doesn't blink together
        _blinking = false;
        _secondBlinkQueued = false;
        _nextBlinkTime = Time.time + RandomRange(0.3f, Mathf.Max(0.4f, blinkInterval.y));
    }

    private void Start()
    {
        _nextBindAttempt = 0f;
        EnsureBound();
    }

    private void OnDisable()
    {
        // Leave the face at its sculpted rest pose instead of frozen mid-blink or mid-word
        RestoreRestWeights();
        RestoreJawBone();
        RestoreHeadAndEyes();
    }

    private void OnValidate()
    {
        blinkInterval.x = Mathf.Max(0.3f, blinkInterval.x);
        blinkInterval.y = Mathf.Max(blinkInterval.x, blinkInterval.y);
        blinkDuration.x = Mathf.Clamp(blinkDuration.x, 0.05f, 0.5f);
        blinkDuration.y = Mathf.Clamp(blinkDuration.y, blinkDuration.x, 0.5f);
        babbleRate.x = Mathf.Max(0.5f, babbleRate.x);
        babbleRate.y = Mathf.Max(babbleRate.x, babbleRate.y);
        emotionFadeIn = Mathf.Max(0f, emotionFadeIn);
        emotionFadeOut = Mathf.Max(0f, emotionFadeOut);
        loudnessJaw = Mathf.Max(0f, loudnessJaw);
        maxBabbleSeconds = Mathf.Max(0f, maxBabbleSeconds);
        dialogueBabbleSeconds = Mathf.Max(0f, dialogueBabbleSeconds);
        jawBoneDegrees = Mathf.Max(0f, jawBoneDegrees);
    }

    private void LateUpdate()
    {
        if (!EnsureBound()) return;

        // Library assigned or swapped at runtime: re-resolve the expressions
        if (!ReferenceEquals(_builtLibrary, library))
            BuildExpressions();

        float now = Time.time;
        ReadAnimatorFlags(out bool talking, out bool thinking);
        UpdateSpeechState();

        UpdateExpression(now, thinking);
        UpdateAmbient(now);
        UpdateGestures(now, thinking);
        float blink = UpdateBlink(now);
        float jaw = UpdateMouth(now, talking);

        Compose(blink);
        ApplyJawBone(jaw);
        ApplyHeadAndEyes();
    }

    // ================================================================
    // Binding
    // ================================================================

    /// <summary>Binds the face mesh once (retrying for a few seconds if it isn't there yet); rebinds if its mesh changes.</summary>
    private bool EnsureBound()
    {
        if (_bound)
        {
            if (_face != null && _face.sharedMesh == _boundMesh) return true;
            _bound = false;  // Renderer destroyed or mesh swapped: bind again
            _bindAttempts = 0;
        }

        if (Time.unscaledTime < _nextBindAttempt) return false;
        _nextBindAttempt = Time.unscaledTime + BindRetrySeconds;

        if (Bind()) return true;

        if (++_bindAttempts >= MaxBindAttempts)
        {
            Debug.LogWarning($"[NPCFace] {name}: no face SkinnedMeshRenderer with '{ShapePrefix}' blend shapes found; " +
                             "face animation disabled.");
            enabled = false;
        }
        return false;
    }

    private bool Bind()
    {
        SkinnedMeshRenderer smr = faceMesh != null ? faceMesh : FindFaceMesh();
        if (smr == null || smr.sharedMesh == null) return false;

        Mesh mesh = smr.sharedMesh;
        int shapeCount = mesh.blendShapeCount;

        // Channels: every face shape except body shapes, keyed by ARKit name (prefix stripped)
        _channelByName.Clear();
        var indices = new List<int>(shapeCount);
        var names = new List<string>(shapeCount);
        for (int i = 0; i < shapeCount; i++)
        {
            string key = StripPrefix(mesh.GetBlendShapeName(i));
            if (key.Length == 0 || IsBodyShape(key) || _channelByName.ContainsKey(key)) continue;

            _channelByName.Add(key, indices.Count);
            indices.Add(i);
            names.Add(key);
        }

        if (indices.Count == 0) return false;

        int n = indices.Count;
        _channelCount = n;
        _shapeIndex = indices.ToArray();
        _rest = new float[n];
        _written = new float[n];
        _isMouthChannel = new bool[n];
        _isLowerFace = new bool[n];
        _trackValues = new float[n];
        _track = null;
        _isEyeWide = new bool[n];
        _opensMouth = new bool[n];
        _add = new float[n];
        _exprFrom = new float[n];
        _exprTarget = new float[n];
        _exprState = new float[n];
        _ambient = new float[n];

        for (int c = 0; c < n; c++)
        {
            // The Animator never touches face shapes here, so the current weight is the sculpted rest
            _rest[c] = smr.GetBlendShapeWeight(_shapeIndex[c]);
            _written[c] = _rest[c];

            string key = names[c];
            _isMouthChannel[c] = key.StartsWith("mouth", System.StringComparison.Ordinal) ||
                                 key.StartsWith("jaw", System.StringComparison.Ordinal) ||
                                 key.StartsWith("cheekPuff", System.StringComparison.Ordinal);
            _isLowerFace[c] = _isMouthChannel[c] || key.StartsWith("tongue", System.StringComparison.Ordinal);
            _isEyeWide[c] = key.StartsWith("eyeWide", System.StringComparison.Ordinal);
            _opensMouth[c] = key.StartsWith("jawOpen", System.StringComparison.Ordinal) ||
                             key.StartsWith("mouthLowerDown", System.StringComparison.Ordinal) ||
                             key.StartsWith("mouthUpperUp", System.StringComparison.Ordinal) ||
                             key.StartsWith("mouthFunnel", System.StringComparison.Ordinal) ||
                             key.StartsWith("mouthStretch", System.StringComparison.Ordinal);
        }

        for (int m = 0; m < MouthCount; m++)
            _mouthChannel[m] = Channel(MouthShapeNames[m]);

        _blinkUpperL = FirstChannel("eyeBlinkUpperLeft", "eyeBlinkLeft");
        _blinkUpperR = FirstChannel("eyeBlinkUpperRight", "eyeBlinkRight");
        _blinkLowerL = Channel("eyeBlinkLowerLeft");
        _blinkLowerR = Channel("eyeBlinkLowerRight");

        _jawBone = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Jaw) : null;
        _neck.Bind(HumanBone(HumanBodyBones.Neck));
        _head.Bind(HumanBone(HumanBodyBones.Head));
        _eyeL.Bind(HumanBone(HumanBodyBones.LeftEye));
        _eyeR.Bind(HumanBone(HumanBodyBones.RightEye));
        BuildGestureShapes();
        _jawWritten = false;
        _hasJawClosed = TryFindClosedJaw(smr, out _jawClosed);
        if (_jawBone == null && jawBoneDegrees > 0f)
            Debug.LogWarning($"[NPCFace] {name}: jawBoneDegrees is set but the avatar has no Jaw bone.");

        _face = smr;
        _boundMesh = mesh;
        _bound = true;
        _bindAttempts = 0;
        _exprStarted = false;  // Snap to the base mood on the first frame instead of fading in from nothing

        BuildExpressions();

        if (logEvents)
        {
            Debug.Log($"[NPCFace] {name}: bound {n}/{shapeCount} shapes on '{smr.name}', " +
                      $"jaw bone {(_jawBone != null ? "found" : "missing")}, " +
                      $"library {(library != null ? library.name : "built-in")}");
        }
        return true;
    }

    /// <summary>The child SkinnedMeshRenderer with the most "MESHBlends." shapes (ties prefer one named "mesh").</summary>
    private SkinnedMeshRenderer FindFaceMesh()
    {
        SkinnedMeshRenderer best = null;
        int bestCount = 0;
        SkinnedMeshRenderer jawFallback = null;

        foreach (SkinnedMeshRenderer r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh mesh = r.sharedMesh;
            if (mesh == null) continue;

            int count = 0;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string shape = mesh.GetBlendShapeName(i);
                if (shape.StartsWith(ShapePrefix, System.StringComparison.Ordinal)) count++;
                else if (jawFallback == null && shape.EndsWith("jawOpen", System.StringComparison.Ordinal)) jawFallback = r;
            }

            if (count > bestCount || (count > 0 && count == bestCount && r.name == "mesh" && best.name != "mesh"))
            {
                best = r;
                bestCount = count;
            }
        }

        return best != null ? best : jawFallback;
    }

    private int Channel(string key)
    {
        return key != null && _channelByName.TryGetValue(key, out int c) ? c : -1;
    }

    private int FirstChannel(string key, string alternative)
    {
        int c = Channel(key);
        return c >= 0 ? c : Channel(alternative);
    }

    private static string StripPrefix(string shapeName)
    {
        if (string.IsNullOrEmpty(shapeName)) return string.Empty;
        int dot = shapeName.LastIndexOf('.');
        return dot >= 0 ? shapeName.Substring(dot + 1) : shapeName;
    }

    private static bool IsBodyShape(string key)
    {
        for (int i = 0; i < BodyShapes.Length; i++)
        {
            if (string.Equals(key, BodyShapes[i], System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void RestoreRestWeights()
    {
        if (!_bound || _face == null || _written == null) return;

        for (int c = 0; c < _channelCount; c++)
        {
            if (_written[c] == _rest[c]) continue;
            _face.SetBlendShapeWeight(_shapeIndex[c], _rest[c]);
            _written[c] = _rest[c];
        }
    }

    // ================================================================
    // Expression tables
    // ================================================================

    /// <summary>A built-in shape weight. Bilateral names without Left/Right (e.g. "mouthSmile") apply to both sides.</summary>
    private readonly struct BuiltInShape
    {
        public readonly string Shape;
        public readonly float Weight;

        public BuiltInShape(string shape, float weight)
        {
            Shape = shape;
            Weight = weight;
        }
    }

    private static BuiltInShape S(string shape, float weight) => new BuiltInShape(shape, weight);

    // Small fallback used when no library is assigned (rough approximations of the Synty poses)
    private static readonly BuiltInShape[] NoShapes = new BuiltInShape[0];
    private static readonly BuiltInShape[] ContentFace =
        { S("mouthSmile", .3f), S("cheekSquint", .15f), S("eyeSquint", .15f), S("browInnerUp", .1f) };
    private static readonly BuiltInShape[] HappyFace =
        { S("mouthSmile", .65f), S("cheekSquint", .4f), S("eyeSquint", .45f), S("browInnerUp", .3f), S("mouthUpperUp", .08f) };
    private static readonly BuiltInShape[] GrumpyFace =
        { S("browInnerDown", .6f), S("browFrown", .3f), S("noseSneer", .35f), S("eyeSquint", .5f),
          S("mouthFrown", .4f), S("mouthPress", .25f), S("mouthShrugLower", .2f) };
    private static readonly BuiltInShape[] WorriedFace =
        { S("browInnerUp", .65f), S("browOuterDown", .25f), S("mouthFrown", .25f), S("mouthStretch", .15f), S("eyeWideUpper", .15f) };
    private static readonly BuiltInShape[] SuspiciousFace =
        { S("eyeSquint", .5f), S("browInnerDownLeft", .4f), S("browOuterUpRight", .3f), S("mouthPress", .2f), S("mouthLeft", .15f) };
    private static readonly BuiltInShape[] SurprisedFace =
        { S("browInnerUp", .8f), S("browRaise", .25f), S("eyeWideUpper", .55f), S("eyeWideLower", .4f),
          S("jawOpen", .2f), S("mouthFunnel", .15f) };
    // No Synty clip for Thinking: brows knit up, eyes narrowed and up, lips pressed off to one side
    private static readonly BuiltInShape[] ThinkingFace =
        { S("browInnerUp", .35f), S("browOuterDown", .12f), S("eyeSquint", .2f), S("mouthPress", .25f),
          S("eyeLookUp", .15f), S("mouthLeft", .1f) };

    private static BuiltInShape[] BuiltInTable(FaceEmotion emotion)
    {
        return emotion switch
        {
            FaceEmotion.Neutral => NoShapes,
            FaceEmotion.Content => ContentFace,
            FaceEmotion.Happy => HappyFace,
            FaceEmotion.Grumpy => GrumpyFace,
            FaceEmotion.Worried => WorriedFace,
            FaceEmotion.Suspicious => SuspiciousFace,
            FaceEmotion.Surprised => SurprisedFace,
            FaceEmotion.Thinking => ThinkingFace,
            _ => null
        };
    }

    /// <summary>Closest available emotion when neither the library nor the built-in table has one.</summary>
    private static FaceEmotion AliasOf(FaceEmotion emotion)
    {
        return emotion switch
        {
            FaceEmotion.Delighted => FaceEmotion.Happy,
            FaceEmotion.Smug => FaceEmotion.Content,
            FaceEmotion.Angry => FaceEmotion.Grumpy,
            FaceEmotion.Disgusted => FaceEmotion.Grumpy,
            FaceEmotion.Sad => FaceEmotion.Worried,
            FaceEmotion.Scared => FaceEmotion.Worried,
            _ => FaceEmotion.Neutral
        };
    }

    /// <summary>Resolves every emotion to sparse (channel, weight) arrays for this face. Bind-time only.</summary>
    private void BuildExpressions()
    {
        _builtLibrary = library;
        if (_emotionChannels == null || _emotionChannels.Length != EmotionCount)
        {
            _emotionChannels = new int[EmotionCount][];
            _emotionWeights = new float[EmotionCount][];
        }

        var channels = new List<int>(32);
        var weights = new List<float>(32);

        for (int e = 0; e < EmotionCount; e++)
        {
            var emotion = (FaceEmotion)e;
            channels.Clear();
            weights.Clear();

            if (!TryResolve(emotion, channels, weights))
            {
                FaceEmotion alias = AliasOf(emotion);
                if (alias != emotion) TryResolve(alias, channels, weights);
            }

            _emotionChannels[e] = channels.ToArray();
            _emotionWeights[e] = weights.ToArray();
        }

        BuildAmbientMotion();
    }

    /// <summary>Resolves the library's idle capture to this face's channels (shapes it lacks are skipped).</summary>
    private void BuildAmbientMotion()
    {
        _ambientChannels = null;
        _ambientSamples = null;
        _ambientCount = 0;
        if (_ambient != null) System.Array.Clear(_ambient, 0, _ambient.Length);

        FaceExpressionLibrary.MotionCycle cycle = library != null ? library.ambientMotion : null;
        if (cycle == null || !cycle.IsUsable) return;

        var channels = new List<int>(cycle.channels.Count);
        var samples = new List<float[]>(cycle.channels.Count);
        int count = int.MaxValue;
        foreach (FaceExpressionLibrary.MotionChannel channel in cycle.channels)
        {
            if (channel == null || channel.samples == null || channel.samples.Length < 2) continue;
            string key = StripPrefix(channel.shape);
            int c = IsBodyShape(key) ? -1 : Channel(key);
            if (c < 0 || channels.Contains(c)) continue;

            channels.Add(c);
            samples.Add(channel.samples);
            count = Mathf.Min(count, channel.samples.Length);
        }
        if (channels.Count == 0) return;

        _ambientChannels = channels.ToArray();
        _ambientSamples = samples.ToArray();
        _ambientCount = count;
        _ambientRate = cycle.sampleRate;
    }

    /// <summary>Library first, then the built-in table. False when neither produced any bound shape.</summary>
    private bool TryResolve(FaceEmotion emotion, List<int> channels, List<float> weights)
    {
        if (library != null && library.TryGet(emotion, out FaceExpressionLibrary.Expression expression) &&
            expression.weights != null)
        {
            for (int i = 0; i < expression.weights.Count; i++)
            {
                FaceExpressionLibrary.ShapeWeight sw = expression.weights[i];
                if (sw != null) AddResolved(channels, weights, sw.shape, sw.weight);
            }
            if (channels.Count > 0 || emotion == FaceEmotion.Neutral) return true;
        }

        BuiltInShape[] table = BuiltInTable(emotion);
        if (table == null) return false;

        for (int i = 0; i < table.Length; i++)
            AddResolved(channels, weights, table[i].Shape, table[i].Weight);
        return channels.Count > 0 || emotion == FaceEmotion.Neutral;
    }

    /// <summary>Maps a shape name to channel(s): exact, else both Left/Right sides, else the unsided shape.</summary>
    private void AddResolved(List<int> channels, List<float> weights, string shape, float weight)
    {
        if (string.IsNullOrEmpty(shape) || float.IsNaN(weight) || Mathf.Abs(weight) < 0.0001f) return;

        string key = StripPrefix(shape);
        if (key.Length == 0 || IsBodyShape(key)) return;
        weight = Mathf.Clamp(weight, -1f, 1f);

        int c = Channel(key);
        if (c >= 0)
        {
            Accumulate(channels, weights, c, weight);
            return;
        }

        int left = Channel(key + "Left");
        int right = Channel(key + "Right");
        if (left >= 0) Accumulate(channels, weights, left, weight);
        if (right >= 0) Accumulate(channels, weights, right, weight);
        if (left >= 0 || right >= 0) return;

        if (key.EndsWith("Left", System.StringComparison.Ordinal))
            c = Channel(key.Substring(0, key.Length - 4));
        else if (key.EndsWith("Right", System.StringComparison.Ordinal))
            c = Channel(key.Substring(0, key.Length - 5));
        if (c >= 0) Accumulate(channels, weights, c, weight);
    }

    private static void Accumulate(List<int> channels, List<float> weights, int channel, float weight)
    {
        int existing = channels.IndexOf(channel);
        if (existing < 0)
        {
            channels.Add(channel);
            weights.Add(weight);
        }
        else if (Mathf.Abs(weight) > Mathf.Abs(weights[existing]))
        {
            weights[existing] = weight;  // Two names landed on one shape (e.g. sided -> unsided): keep the stronger
        }
    }

    // ================================================================
    // Layer 1: expression
    // ================================================================

    private void UpdateExpression(float now, bool thinking)
    {
        if (_transientActive && now >= _transientEnd)
            _transientActive = false;

        ExprKind kind;
        FaceEmotion emotion;
        float strength;
        int serial = 0;

        if (thinking)
        {
            kind = ExprKind.Thinking;
            emotion = FaceEmotion.Thinking;
            strength = thinkingStrength;
        }
        else if (_transientActive)
        {
            kind = ExprKind.Transient;
            emotion = _transientEmotion;
            strength = _transientIntensity;
            serial = _transientSerial;
        }
        else
        {
            kind = ExprKind.Base;
            emotion = baseMood;
            strength = baseMoodStrength;
        }

        FillTarget(emotion, strength);
        // Moods and line emotions keep smiles, brows and eyes but only a hint of an open mouth: the lips part for
        // words, and a reaction held after the line ends would otherwise leave the mouth hanging open
        for (int c = 0; c < _channelCount; c++)
            if (_opensMouth[c]) _exprTarget[c] *= RestingMouthOpenScale;
        int n = _channelCount;

        if (!_exprStarted)
        {
            System.Array.Copy(_exprTarget, _exprState, n);
            _exprKind = kind;
            _exprEmotion = emotion;
            _exprSerial = serial;
            _fadeDuration = 0f;
            _exprStarted = true;
            return;
        }

        // A new source: crossfade from whatever the face shows right now
        if (kind != _exprKind || emotion != _exprEmotion || serial != _exprSerial)
        {
            System.Array.Copy(_exprState, _exprFrom, n);
            _fadeDuration = kind == ExprKind.Base ? emotionFadeOut : emotionFadeIn;
            _fadeStart = now;
            _exprKind = kind;
            _exprEmotion = emotion;
            _exprSerial = serial;
        }

        // The target is rebuilt every frame, so strength tweaks apply live once the fade is done
        float s = _fadeDuration > 0.0001f ? Mathf.Clamp01((now - _fadeStart) / _fadeDuration) : 1f;
        if (s >= 1f)
        {
            System.Array.Copy(_exprTarget, _exprState, n);
            return;
        }

        s = Mathf.SmoothStep(0f, 1f, s);
        for (int c = 0; c < n; c++)
            _exprState[c] = Mathf.Lerp(_exprFrom[c], _exprTarget[c], s);
    }

    private void FillTarget(FaceEmotion emotion, float strength)
    {
        System.Array.Clear(_exprTarget, 0, _channelCount);

        int e = (int)emotion;
        if (_emotionChannels == null || e < 0 || e >= _emotionChannels.Length || strength <= 0f) return;

        int[] channels = _emotionChannels[e];
        float[] weights = _emotionWeights[e];
        if (channels == null) return;

        for (int i = 0; i < channels.Length; i++)
            _exprTarget[channels[i]] = weights[i] * strength;
    }

    /// <summary>Samples the looping idle capture into _ambient (per-instance phase and speed).</summary>
    private void UpdateAmbient(float now)
    {
        if (_ambientChannels == null) return;

        float x = (now * _ambientSpeed + _ambientPhase) * _ambientRate % _ambientCount;
        int i = Mathf.Clamp((int)x, 0, _ambientCount - 1);
        int j = i + 1 < _ambientCount ? i + 1 : 0;
        float f = x - i;
        for (int k = 0; k < _ambientChannels.Length; k++)
        {
            float[] samples = _ambientSamples[k];
            _ambient[_ambientChannels[k]] = Mathf.Lerp(samples[i], samples[j], f) * ambientMotion;
        }
    }

    // ================================================================
    // Layer 2: blinks
    // ================================================================

    /// <summary>Returns the blink closure 0..1 for this frame.</summary>
    private float UpdateBlink(float now)
    {
        if (!_blinking)
        {
            if (now < _nextBlinkTime) return 0f;

            _blinking = true;
            _blinkStart = now;
            _blinkLength = Mathf.Max(0.05f, RandomRange(blinkDuration.x, blinkDuration.y));
            if (_secondBlinkQueued)
            {
                _secondBlinkQueued = false;
                _doubleAfterThis = false;
            }
            else
            {
                _doubleAfterThis = NextRandom() < doubleBlinkChance;
            }
        }

        float p = (now - _blinkStart) / _blinkLength;
        if (p >= 1f)
        {
            _blinking = false;
            _lastBlinkEnd = now;
            if (_doubleAfterThis)
            {
                _secondBlinkQueued = true;
                _nextBlinkTime = now + RandomRange(0.05f, 0.12f);
            }
            else
            {
                float interval = RandomRange(blinkInterval.x, blinkInterval.y);
                _nextBlinkTime = now + (IsSpeakingAudio ? interval / SpeakingBlinkRate : interval);
            }
            return 0f;
        }

        // Lids drop fast, stay shut a moment, then lift with an easing-out opening (Trutoiu et al. 2011)
        if (p < 0.35f)
        {
            float c = p / 0.35f;
            return c * c;
        }
        if (p < 0.45f) return 1f;
        float o = (p - 0.45f) / 0.55f;
        return 1f - (1f - Mathf.Exp(-4f * o)) / (1f - Mathf.Exp(-4f));
    }

    /// <summary>Blinks now (a speech pause, a line's end, a big glance) unless the lids are busy or just blinked.</summary>
    private void RequestBlink(float now)
    {
        if (_blinking || now - _lastBlinkEnd < BlinkRefractorySeconds) return;
        _nextBlinkTime = now;
        _secondBlinkQueued = false;
    }

    // ================================================================
    // Layer 3: mouth
    // ================================================================

    /// <summary>Updates the mouth layer and returns its jawOpen value (0..1) for the jaw bone.</summary>
    private float UpdateMouth(float now, bool talking)
    {
        System.Array.Clear(_mouthTarget, 0, MouthCount);
        bool speaking = IsSpeakingAudio;

        // Each isTalking stretch may babble, unless audio has already spoken for it
        if (talking && !_wasTalking)
        {
            _talkStart = now;
            _stretchHadAudio = speaking;
            ResetBabble();
        }
        _wasTalking = talking;
        if (speaking) _stretchHadAudio = true;

        float tau;
        bool active;
        bool babbling = false;
        if (speaking)
        {
            EvaluateSpeech();
            if (_track != null) EvaluateTrack(SpeechTime());
            // A face track takes over the mouth: the cue shapes give way as it fades in
            for (int m = 0; m < MouthCount && _trackMix > 0f; m++)
                _mouthTarget[m] *= 1f - _trackMix;
            tau = AudioMouthSmoothing;
            active = true;
        }
        else if (talking && !_stretchHadAudio && babbleAmount > 0f && now - _talkStart < BabbleLimit())
        {
            babbling = true;
            if (!_babbleGestures && speechGestures && _gestures != null)
            {
                _gestures.BeginBabble(CurrentEmotion());
                _babbleGestures = true;
            }
            EvaluateBabble(now);
            tau = BabbleMouthSmoothing;
            active = true;
        }
        else
        {
            tau = RestMouthSmoothing;
            active = false;
        }

        if (_babbleGestures && !babbling)
        {
            _babbleGestures = false;
            if (!_gestureLine) _gestures.EndLine();
        }

        // Audio keeps playing while timeScale is 0, so the mouth follows unscaled time
        float dt = Time.unscaledDeltaTime;
        float k = 1f - Mathf.Exp(-dt / tau);
        for (int m = 0; m < MouthCount; m++)
        {
            float v = _mouthState[m] + (_mouthTarget[m] - _mouthState[m]) * k;
            _mouthState[m] = Mathf.Abs(v) < 1e-5f ? 0f : v;  // Settle exactly, so resting shapes stop being written
        }

        _mouthActive = Mathf.MoveTowards(_mouthActive, active ? 1f : 0f, dt / MouthActiveFadeSeconds);
        _trackMix = Mathf.MoveTowards(_trackMix, speaking && _track != null ? 1f : 0f, dt / TrackBlendSeconds);

        int jawChannel = _mouthChannel[MJaw];
        float trackJaw = jawChannel >= 0 ? _trackValues[jawChannel] * _trackMix : 0f;
        return Mathf.Max(_mouthState[MJaw], trackJaw);
    }

    /// <summary>Samples the face track at clip time t into _trackValues (linear between frames, held at the ends).</summary>
    private void EvaluateTrack(float t)
    {
        FaceTrack track = _track;
        int stride = track.Names.Length;
        float x = Mathf.Clamp(t * track.Fps, 0f, track.Frames - 1);
        int i = (int)x;
        int j = Mathf.Min(i + 1, track.Frames - 1);
        float k = x - i;
        float[] w = track.Weights;
        for (int s = 0; s < _trackChannels.Length; s++)
        {
            int n = _trackNames[s];
            _trackValues[_trackChannels[s]] = Mathf.Lerp(w[i * stride + n], w[j * stride + n], k);
        }
    }

    private float BabbleLimit()
    {
        float limit = _identity != null && DialogueManagerV2.ActiveNPC == _identity ? dialogueBabbleSeconds : maxBabbleSeconds;
        return limit > 0f ? limit : float.PositiveInfinity;
    }

    // ---- Audio speech ----

    /// <summary>Drops the speech once its clip finished, was replaced on the source, or was destroyed.</summary>
    private void UpdateSpeechState()
    {
        if (!_hasSpeech) return;

        if (_speechSource == null || _speechClip == null)
        {
            ClearSpeech();
            return;
        }

        if (_speechSource.clip == _speechClip && _speechSource.isPlaying)
            _speechSeenPlaying = true;
        else if (_speechSeenPlaying || Time.unscaledTime - _speechGivenAt > SpeechStartGrace)
            ClearSpeech();
    }

    private void ClearSpeech()
    {
        _prosodyTask = null;
        _prosodyClip = null;
        _hasSpeech = false;
        _speechSource = null;
        _speechClip = null;
        _speechSeenPlaying = false;
        _cues = null;
        _cuesArrivedAt = -1f;
        _envelopeLength = 0;
        _track = null;   // Its last weights fade out with _trackMix
    }

    private void EvaluateSpeech()
    {
        AudioClip clip = _speechClip;
        float t = clip.frequency > 0 ? _speechSource.timeSamples / (float)clip.frequency : _speechSource.time;

        float cueMix = 0f;
        if (_cues != null && _cues.Length > 0)
            cueMix = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.unscaledTime - _cuesArrivedAt) / CueArrivalBlendSeconds));

        if (cueMix < 1f)
        {
            // Loudness jaw, with a slow drift between a wider and a rounder mouth so it isn't a pure hinge
            float level = LoudnessAt(t) * (1f - cueMix);
            float drift = Mathf.PerlinNoise(_noiseSeed, t * 2.3f) * 2f - 1f;
            float stretch = level * 0.12f * Mathf.Max(0f, drift);
            _mouthTarget[MJaw] += loudnessJaw * level * mouthAmplitude * (1f + EmphasisOpen * Emphasis());
            _mouthTarget[MStretchL] += stretch * mouthAmplitude;
            _mouthTarget[MStretchR] += stretch * mouthAmplitude;
            _mouthTarget[MFunnel] += level * 0.18f * Mathf.Max(0f, -drift) * mouthAmplitude;
        }

        if (cueMix > 0f)
        {
            EvaluateCues(t + CueLeadSeconds, out int previous, out int current, out float blend);
            // Cues say which shape; loudness says how far open. Closing shapes (M/B/P press) keep full strength
            float open = Mathf.Lerp(quietOpenFloor, 1f, LoudnessAt(t)) * mouthAmplitude * (1f + EmphasisOpen * Emphasis());
            for (int m = 0; m < MouthCount; m++)
            {
                float pose = Mathf.Lerp(CuePoses[previous, m], CuePoses[current, m], blend);
                _mouthTarget[m] += pose * cueMix * (IsClosingShape(m) ? 1f : open);
            }
        }
    }

    /// <summary>Lip contact shapes (press, roll, close): kept at full strength, unlike the opening ones.</summary>
    private static bool IsClosingShape(int mouthShape)
    {
        return mouthShape == MClose || mouthShape == MPressL || mouthShape == MPressR ||
               mouthShape == MRollLower || mouthShape == MRollUpper;
    }

    /// <summary>The cue at time t and the one before it, with a 40 ms blend between them (ported from XybridAvatarPlayer).</summary>
    private void EvaluateCues(float t, out int previous, out int current, out float blend)
    {
        LipSyncCue[] cues = _cues;
        previous = CueX;
        current = CueX;
        blend = 1f;

        int lo = 0, hi = cues.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (t < cues[mid].start) hi = mid - 1;
            else
            {
                found = mid;
                lo = mid + 1;
            }
        }

        if (found < 0 || t >= cues[found].end) return;

        current = CueIndex(cues[found].shape);
        if (found > 0 && cues[found - 1].end >= cues[found].start - 0.001f)
            previous = CueIndex(cues[found - 1].shape);
        blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - cues[found].start) / CueBlendSeconds));
    }

    private static int CueIndex(char shape)
    {
        char c = char.ToUpperInvariant(shape);
        return c >= 'A' && c <= 'H' ? c - 'A' : CueX;
    }

    /// <summary>Copies the cues, dropping invalid ones and sorting by start. Null if none are usable.</summary>
    private static LipSyncCue[] SanitizeCues(LipSyncCue[] cues)
    {
        int valid = 0;
        for (int i = 0; i < cues.Length; i++)
            if (IsValidCue(cues[i])) valid++;
        if (valid == 0) return null;

        var clean = new LipSyncCue[valid];
        bool sorted = true;
        int j = 0;
        for (int i = 0; i < cues.Length; i++)
        {
            if (!IsValidCue(cues[i])) continue;
            clean[j] = cues[i];
            if (j > 0 && clean[j].start < clean[j - 1].start) sorted = false;
            j++;
        }

        if (!sorted)
            System.Array.Sort(clean, (a, b) => a.start.CompareTo(b.start));
        return clean;
    }

    private static bool IsValidCue(LipSyncCue cue)
    {
        return !float.IsNaN(cue.start) && !float.IsNaN(cue.end) &&
               !float.IsInfinity(cue.start) && !float.IsInfinity(cue.end) && cue.end > cue.start;
    }

    /// <summary>Gated loudness 0..1 at time t: the precomputed envelope, else the source's live output.</summary>
    private float LoudnessAt(float t)
    {
        if (_envelopeLength > 0)
        {
            float x = Mathf.Max(0f, t) / EnvelopeHop;
            int i = Mathf.Clamp(Mathf.FloorToInt(x), 0, _envelopeLength - 1);
            return i + 1 < _envelopeLength ? Mathf.Lerp(_envelope[i], _envelope[i + 1], x - i) : _envelope[i];
        }

        // Clip samples couldn't be read ahead (streamed/compressed): follow the live output instead
        _speechSource.GetOutputData(_outputBuffer, 0);
        float sum = 0f;
        for (int i = 0; i < _outputBuffer.Length; i++)
            sum += _outputBuffer[i] * _outputBuffer[i];

        float rms = Mathf.Sqrt(sum / _outputBuffer.Length);
        _livePeak = Mathf.Max(_livePeak * 0.998f, rms);
        return Gate(rms / Mathf.Max(_livePeak, 1e-4f));
    }

    /// <summary>
    /// RMS in 10 ms hops, opened fast and closed slower, normalised to the clip's 95th percentile and gated
    /// so pauses close the mouth (ported from XybridAvatarPlayer.LoudnessEnvelope). Main thread only.
    /// Leaves _envelopeLength at 0 when the samples can't be read.
    /// </summary>
    private void ComputeEnvelope(AudioClip clip)
    {
        _envelopeLength = 0;
        int samples = clip.samples;
        int channels = clip.channels;
        if (samples <= 0 || channels <= 0 || clip.frequency <= 0) return;
        if (clip.loadType == AudioClipLoadType.Streaming) return;

        int hop = Mathf.Max(1, Mathf.RoundToInt(EnvelopeHop * clip.frequency));
        int frames = (samples + hop - 1) / hop;
        EnsureCapacity(ref _envelope, frames);
        EnsureCapacity(ref _envelopeSort, frames);

        // Read in hop-aligned chunks so a long line doesn't allocate its whole PCM buffer
        int chunkFrames = hop * 64;
        int chunkLength = chunkFrames * channels;
        if (_pcmChunk == null || _pcmChunk.Length != chunkLength)
            _pcmChunk = new float[chunkLength];

        float attack = 1f - Mathf.Exp(-EnvelopeHop / 0.025f);
        float release = 1f - Mathf.Exp(-EnvelopeHop / 0.08f);
        float level = 0f;
        int f = 0;

        for (int offset = 0; offset < samples; offset += chunkFrames)
        {
            int count = Mathf.Min(chunkFrames, samples - offset);
            // GetData reads buffer.Length / channels frames, so the tail gets an exact-size buffer
            float[] buffer = count == chunkFrames ? _pcmChunk : new float[count * channels];

            bool ok;
            try { ok = clip.GetData(buffer, offset); }
            catch (System.Exception) { ok = false; }
            if (!ok) return;

            for (int h = 0; h < count && f < frames; h += hop)
            {
                int end = Mathf.Min(count, h + hop);
                double sum = 0;
                for (int i = h; i < end; i++)
                {
                    float mono = 0f;
                    int baseIndex = i * channels;
                    for (int ch = 0; ch < channels; ch++) mono += buffer[baseIndex + ch];
                    mono /= channels;
                    sum += mono * mono;
                }

                float rms = Mathf.Sqrt((float)(sum / (end - h)));
                level += (rms - level) * (rms > level ? attack : release);
                _envelope[f++] = level;
            }
        }

        if (f == 0) return;

        System.Array.Copy(_envelope, _envelopeSort, f);
        System.Array.Sort(_envelopeSort, 0, f);
        float reference = Mathf.Max(_envelopeSort[Mathf.Clamp(Mathf.RoundToInt(f * 0.95f), 0, f - 1)], 1e-4f);
        for (int i = 0; i < f; i++)
            _envelope[i] = Gate(_envelope[i] / reference);

        _envelopeLength = f;
    }

    private static float Gate(float level) => Mathf.Clamp01((level - 0.12f) / 0.88f);

    private static void EnsureCapacity(ref float[] buffer, int length)
    {
        if (buffer == null || buffer.Length < length)
            buffer = new float[Mathf.NextPowerOfTwo(Mathf.Max(length, 64))];
    }

    // ---- Babble ----

    private void ResetBabble()
    {
        _firstSyllable = true;
        _syllablesLeftInWord = 0;
        _syllableLength = 0f;
        _syllableStart = _talkStart;
        _syllableIsGap = false;
    }

    /// <summary>Procedural syllables (~4-6 Hz) over the cue poses, grouped into short words.</summary>
    private void EvaluateBabble(float now)
    {
        if (now - _syllableStart >= _syllableLength)
            NextSyllable(now);

        if (_syllableIsGap) return;  // Word gap: mouth rests

        float age = now - _talkStart;
        float fade = Mathf.Clamp01(age / 0.08f);
        float limit = BabbleLimit();
        if (!float.IsPositiveInfinity(limit))
            fade *= Mathf.Clamp01((limit - age) / 0.4f);

        float p = Mathf.Clamp01((now - _syllableStart) / _syllableLength);
        float open = Mathf.Sin(p * Mathf.PI);
        // Lips stay barely parted between syllables of a word; a closed (A) syllable presses fully
        if (_syllableShape != CueA) open = 0.08f + 0.92f * open;

        float amount = open * _syllableAmp * babbleAmount * fade;
        for (int m = 0; m < MouthCount; m++)
            _mouthTarget[m] += CuePoses[_syllableShape, m] * amount * (IsClosingShape(m) ? 1f : mouthAmplitude);
    }

    private void NextSyllable(float now)
    {
        _syllableStart = now;

        if (_syllablesLeftInWord <= 0)
        {
            _syllablesLeftInWord = 1 + (int)(NextRandom() * 4f);  // 1..4 syllables per word
            if (!_firstSyllable)
            {
                _syllableIsGap = true;
                _syllableLength = RandomRange(0.1f, 0.28f);
                return;
            }
        }
        // A word's first syllable follows the line start or a word gap
        if (_babbleGestures && (_firstSyllable || _syllableIsGap)) _gestures.OnBabbleWord();

        _firstSyllable = false;
        _syllableIsGap = false;
        _syllablesLeftInWord--;
        _syllableLength = 1f / Mathf.Max(0.5f, RandomRange(babbleRate.x, babbleRate.y));
        _syllableAmp = RandomRange(0.6f, 1f);

        int shape = PickBabbleShape();
        if (shape == _syllableShape) shape = PickBabbleShape();  // Fewer repeats reads more like speech
        _syllableShape = shape;
    }

    private int PickBabbleShape()
    {
        float r = NextRandom();
        if (r < 0.18f) return CueA;
        if (r < 0.38f) return CueB;
        if (r < 0.60f) return CueC;
        if (r < 0.75f) return CueD;
        if (r < 0.88f) return CueE;
        return CueF;
    }

    // ================================================================
    // Layer 4: speech gestures
    // ================================================================

    /// <summary>
    /// Copies the clip's samples here (main thread) and analyses its prosody on a worker; the gestures are planned
    /// once the result is in (UpdateGestures), a few ms into the line.
    /// </summary>
    private void StartProsody(AudioClip clip, string text)
    {
        _speechText = text;
        if (!speechGestures || _gestures == null) return;

        int frames = clip.samples, channels = clip.channels, rate = clip.frequency;
        if (frames <= 0 || channels <= 0 || rate <= 0 || clip.loadType == AudioClipLoadType.Streaming) return;

        var data = new float[frames * channels];
        try
        {
            if (!clip.GetData(data, 0)) return;
        }
        catch (System.Exception)
        {
            return;
        }

        _prosodyClip = clip;
        _prosodyTask = Task.Run(() => SpeechProsody.Analyze(Downmix(data, channels), rate));
    }

    private static float[] Downmix(float[] data, int channels)
    {
        if (channels == 1) return data;
        var mono = new float[data.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += data[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    private void UpdateGestures(float now, bool thinking)
    {
        if (_gestures == null) return;

        // The playing line's prosody is in: plan its brows, beats, blinks and glances
        if (_prosodyTask != null && _prosodyTask.IsCompleted)
        {
            Task<ProsodyTrack> task = _prosodyTask;
            _prosodyTask = null;
            ProsodyTrack track = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            if (task.IsFaulted)
                Debug.LogWarning($"[NPCFace] {name}: prosody analysis failed ({task.Exception?.GetBaseException().Message}).");

            if (track != null && _hasSpeech && _prosodyClip == _speechClip)
            {
                _gestures.BeginLine(track, _speechText, CurrentEmotion(), SpeechTime());
                _gestureLine = true;
                _babbleGestures = false;
                if (logEvents) Debug.Log($"[NPCFace] {name} gestures: {_gestures.PlanSummary}");
            }
        }
        if (_gestureLine && !_hasSpeech)
        {
            _gestures.EndLine();
            _gestureLine = false;
        }

        _gestures.Expressiveness = expressiveness;
        _gestures.Update(Time.unscaledDeltaTime, Time.unscaledTime, IsSpeakingAudio ? SpeechTime() : -1f, thinking,
            CurrentEmotion());
        if (_gestures.ConsumeBlinkRequest()) RequestBlink(now);
    }

    /// <summary>
    /// The emotion that styles the gestures: the transient line emotion when it's clear, else the base mood (a mild
    /// cue, such as the faint surprise any question gets, shouldn't restyle the whole delivery).
    /// </summary>
    private FaceEmotion CurrentEmotion() =>
        _transientActive && _transientIntensity >= MinStylingIntensity ? _transientEmotion : baseMood;

    /// <summary>Playback time of the spoken clip (seconds), from its source's sample position.</summary>
    private float SpeechTime()
    {
        if (_speechSource == null || _speechClip == null) return 0f;
        return _speechClip.frequency > 0 ? _speechSource.timeSamples / (float)_speechClip.frequency : _speechSource.time;
    }

    private float Emphasis() => speechGestures && _gestures != null ? _gestures.Emphasis : 0f;

    private void BuildGestureShapes()
    {
        // ARKit brow units: AU1+2 raise, AU4 knit, AU1+4 worry (Synty's sadness capture pairs inner-up with frown)
        _raiseShapes = Shapes(("browInnerUp", 1f), ("browOuterUp", 0.75f));
        _knitShapes = Shapes(("browInnerDown", 1f), ("browFrown", 0.35f), ("browOuterDown", 0.2f));
        _worryShapes = Shapes(("browInnerUp", 1f), ("browFrown", 0.5f), ("browOuterDown", 0.3f));
        _squintShapes = Shapes(("eyeSquint", 0.6f), ("cheekSquint", 0.8f));
        _sneerShapes = Shapes(("noseSneer", 1f));
        _wideShapes = Shapes(("eyeWideUpper", 1f));
    }

    /// <summary>Resolves (shape, weight) pairs to channels: the unsided shape, else its Left and Right sides.</summary>
    private GestureShape[] Shapes(params (string shape, float weight)[] entries)
    {
        var list = new List<GestureShape>(entries.Length * 2);
        foreach ((string shape, float weight) in entries)
        {
            int both = Channel(shape);
            if (both >= 0)
            {
                list.Add(new GestureShape { Channel = both, Weight = weight, Side = 0 });
                continue;
            }
            int left = Channel(shape + "Left"), right = Channel(shape + "Right");
            if (left >= 0) list.Add(new GestureShape { Channel = left, Weight = weight, Side = -1 });
            if (right >= 0) list.Add(new GestureShape { Channel = right, Weight = weight, Side = 1 });
        }
        return list.ToArray();
    }

    private void AddGesture(GestureShape[] shapes, float amount)
    {
        if (shapes == null || amount <= 0f) return;
        for (int i = 0; i < shapes.Length; i++)
        {
            GestureShape g = shapes[i];
            float side = g.Side < 0 ? 1f + _asymmetry : g.Side > 0 ? 1f - _asymmetry : 1f;
            _add[g.Channel] += amount * g.Weight * side;
        }
    }

    private Transform HumanBone(HumanBodyBones bone)
    {
        return animator != null && animator.isHuman ? animator.GetBoneTransform(bone) : null;
    }

    // ================================================================
    // Compose + write
    // ================================================================

    private void Compose(float blink)
    {
        if (_face == null || !_face.enabled || !_face.gameObject.activeInHierarchy) return;

        int n = _channelCount;
        System.Array.Clear(_add, 0, n);

        for (int m = 0; m < MouthCount; m++)
        {
            int c = _mouthChannel[m];
            if (c >= 0) _add[c] += _mouthState[m];
        }

        // Upper lids follow the eyes down a little, and widen a touch when they look up
        if (eyeMotion && _gestures != null)
        {
            float down = Mathf.Clamp01(_gestures.GazePitch / 25f) * 0.35f;
            if (down > 0f && blink < 1f)
            {
                if (_blinkUpperL >= 0) _add[_blinkUpperL] += down * (1f - blink);
                if (_blinkUpperR >= 0) _add[_blinkUpperR] += down * (1f - blink);
            }
        }

        if (blink > 0f)
        {
            if (_blinkUpperL >= 0) _add[_blinkUpperL] += blink;
            if (_blinkUpperR >= 0) _add[_blinkUpperR] += blink;
            if (_blinkLowerL >= 0) _add[_blinkLowerL] += blink * 0.3f;
            if (_blinkLowerR >= 0) _add[_blinkLowerR] += blink * 0.3f;
        }

        // Lip safety: the expression's mouth gives way while the lips are shaping words, and only the words open it
        float lipScale = Mathf.Lerp(1f, speakingExpressionMouth, _mouthActive);
        float wideScale = 1f - blink;

        for (int c = 0; c < n; c++)
        {
            float expr = _exprState[c];
            float ambient = _ambient[c];
            if (_opensMouth[c]) expr *= 1f - _mouthActive;
            else if (_isMouthChannel[c]) expr *= lipScale;
            else if (_isEyeWide[c]) expr *= wideScale;
            // Idle lip drift stops while the lips shape words
            if (_isMouthChannel[c]) ambient *= 1f - _mouthActive;
            else if (_isEyeWide[c]) ambient *= wideScale;
            // A face track owns the lower face: the expression's mouth gives way to it entirely
            if (_trackMix > 0f && _isLowerFace[c])
            {
                expr *= 1f - _trackMix;
                ambient *= 1f - _trackMix;
                _add[c] += _trackValues[c] * _trackMix;
            }
            _add[c] += expr + ambient;
        }

        if (speechGestures && _gestures != null)
        {
            AddGesture(_raiseShapes, _gestures.BrowRaise);
            AddGesture(_knitShapes, _gestures.BrowKnit);
            AddGesture(_worryShapes, _gestures.BrowWorry);
            AddGesture(_squintShapes, _gestures.Squint);
            AddGesture(_sneerShapes, _gestures.Sneer);
            AddGesture(_wideShapes, (_gestures.EyeWide + Mathf.Clamp01(-_gestures.GazePitch / 15f) * 0.12f) * wideScale);
        }

        // mouthClose is a corrective for an open jaw: past what the jaw opened, it pushes the lips into each other
        int jawChannel = _mouthChannel[MJaw], closeChannel = _mouthChannel[MClose];
        if (closeChannel >= 0 && _add[closeChannel] > 0f)
        {
            float jawOpen = jawChannel >= 0 ? Mathf.Max(0f, _add[jawChannel]) : 0f;
            _add[closeChannel] = Mathf.Min(_add[closeChannel], jawOpen / MouthCloseCancelsJaw);
        }

        for (int c = 0; c < n; c++)
        {
            float offset = _add[c];
            // Untouched shapes keep their exact rest weight (never clamped or rewritten)
            float value = offset == 0f ? _rest[c] : Mathf.Clamp(_rest[c] + offset * 100f, 0f, 100f);
            if (Mathf.Abs(value - _written[c]) <= 0.01f) continue;

            _face.SetBlendShapeWeight(_shapeIndex[c], value);
            _written[c] = value;
        }
    }

    // ================================================================
    // Layer 4: jaw bone
    // ================================================================

    private void ApplyJawBone(float jaw)
    {
        if (_jawBone == null) return;
        bool hold = holdJawClosed && _hasJawClosed;
        if (!hold && jawBoneDegrees <= 0f)
        {
            RestoreJawBone();
            return;
        }

        Quaternion animated = _jawBone.localRotation;
        // A culled or paused Animator doesn't rewrite the bone: don't stack on top of our own last write
        if (_jawWritten && animated == _jawLastWritten) animated = _jawBase;

        _jawBase = animated;
        Quaternion from = hold ? _jawClosed : animated;
        _jawLastWritten = from * Quaternion.AngleAxis(Mathf.Clamp01(jaw) * jawBoneDegrees, Vector3.right);
        _jawBone.localRotation = _jawLastWritten;
        _jawWritten = true;
    }

    /// <summary>
    /// The jaw bone's closed local rotation: its rest rotation in the avatar's skeleton, or failing that the one
    /// the face mesh was bound with.
    /// </summary>
    private bool TryFindClosedJaw(SkinnedMeshRenderer smr, out Quaternion closed)
    {
        closed = Quaternion.identity;
        if (_jawBone == null) return false;

        Avatar avatar = animator != null ? animator.avatar : null;
        if (avatar != null)
        {
            foreach (SkeletonBone bone in avatar.humanDescription.skeleton)
            {
                if (bone.name != _jawBone.name) continue;
                closed = bone.rotation;
                return true;
            }
        }

        Transform[] bones = smr != null ? smr.bones : null;
        Matrix4x4[] bindposes = smr != null && smr.sharedMesh != null ? smr.sharedMesh.bindposes : null;
        if (bones == null || bindposes == null) return false;
        int jaw = System.Array.IndexOf(bones, _jawBone);
        int parent = System.Array.IndexOf(bones, _jawBone.parent);
        if (jaw < 0 || parent < 0 || jaw >= bindposes.Length || parent >= bindposes.Length) return false;

        closed = (bindposes[parent] * bindposes[jaw].inverse).rotation;
        return true;
    }

    private void RestoreJawBone()
    {
        if (!_jawWritten) return;
        _jawWritten = false;
        if (_jawBone != null && _jawBone.localRotation == _jawLastWritten)
            _jawBone.localRotation = _jawBase;
    }

    // ================================================================
    // Layer 4b: head and eyes
    // ================================================================

    /// <summary>
    /// Turns the neck and head by the gesture offsets (about the character's up, its right and its facing), then
    /// sets the eyes back to the world direction the animation and look-at IK gave them (the vestibulo-ocular reflex)
    /// and adds the gaze offset, so nods and posture shifts never drag the eyes off their target.
    /// </summary>
    private void ApplyHeadAndEyes()
    {
        bool moveHead = headMotion && _gestures != null && _head.Bone != null;
        bool moveEyes = eyeMotion && _gestures != null && _eyeL.Bone != null && _eyeR.Bone != null;
        if (!moveHead && !moveEyes)
        {
            RestoreHeadAndEyes();
            return;
        }

        _neck.BeginFrame();
        _head.BeginFrame();
        _eyeL.BeginFrame();
        _eyeR.BeginFrame();

        Vector3 up = transform.up;
        Vector3 facing = transform.forward;
        if (_head.Bone != null && _eyeL.Bone != null && _eyeR.Bone != null)
        {
            Vector3 toEyes = (_eyeL.Bone.position + _eyeR.Bone.position) * 0.5f - _head.Bone.position;
            Vector3 flat = Vector3.ProjectOnPlane(toEyes, up);
            if (flat.sqrMagnitude > 1e-6f) facing = flat.normalized;
        }
        Vector3 right = Vector3.Cross(up, facing);

        Quaternion eyeL = moveEyes ? _eyeL.Bone.rotation : Quaternion.identity;
        Quaternion eyeR = moveEyes ? _eyeR.Bone.rotation : Quaternion.identity;

        if (moveHead)
        {
            Quaternion delta = Quaternion.AngleAxis(_gestures.HeadYaw, up) *
                               Quaternion.AngleAxis(_gestures.HeadPitch, right) *
                               Quaternion.AngleAxis(_gestures.HeadRoll, facing);
            if (_neck.Bone != null)
            {
                _neck.Bone.rotation = Quaternion.Slerp(Quaternion.identity, delta, NeckShare) * _neck.Bone.rotation;
                _head.Bone.rotation = Quaternion.Slerp(Quaternion.identity, delta, 1f - NeckShare) * _head.Bone.rotation;
            }
            else
            {
                _head.Bone.rotation = delta * _head.Bone.rotation;
            }
        }

        if (moveEyes)
        {
            Quaternion gaze = Quaternion.AngleAxis(_gestures.GazeYaw, up) * Quaternion.AngleAxis(_gestures.GazePitch, right);
            _eyeL.Bone.rotation = gaze * eyeL;
            _eyeR.Bone.rotation = gaze * eyeR;
        }

        _neck.EndFrame();
        _head.EndFrame();
        _eyeL.EndFrame();
        _eyeR.EndFrame();
    }

    private void RestoreHeadAndEyes()
    {
        _eyeL.Restore();
        _eyeR.Restore();
        _head.Restore();
        _neck.Restore();
    }

    // ================================================================
    // Animator flags
    // ================================================================

    private void ReadAnimatorFlags(out bool talking, out bool thinking)
    {
        talking = false;
        thinking = false;
        if (animator == null || !animator.isActiveAndEnabled || !animator.isInitialized) return;

        RuntimeAnimatorController controller = animator.runtimeAnimatorController;
        if (controller == null) return;

        // Look the parameters up once per controller (they can read as empty during the first frames)
        if (!ReferenceEquals(controller, _checkedController))
        {
            if (animator.parameterCount == 0) return;

            _checkedController = controller;
            _hasTalkingParam = false;
            _hasThinkingParam = false;
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.type != AnimatorControllerParameterType.Bool) continue;
                if (p.nameHash == IsTalkingHash) _hasTalkingParam = true;
                else if (p.nameHash == IsThinkingHash) _hasThinkingParam = true;
            }
        }

        if (_hasTalkingParam) talking = animator.GetBool(IsTalkingHash);
        if (_hasThinkingParam) thinking = animator.GetBool(IsThinkingHash);
    }

    // ================================================================
    // Randomness
    // ================================================================

    private void SeedRandom()
    {
        // FNV-1a over the name, mixed with a per-run value so identical names still differ
        uint h = 2166136261u;
        string n = name;
        for (int i = 0; i < n.Length; i++)
        {
            h ^= n[i];
            h *= 16777619u;
        }
        h ^= (uint)Random.Range(1, int.MaxValue);
        _rngState = h != 0u ? h : 0x9E3779B9u;
        _noiseSeed = NextRandom() * 1000f;
        _ambientPhase = NextRandom() * 60f;
        _ambientSpeed = RandomRange(0.85f, 1.1f);
    }

    /// <summary>xorshift32, uniform in [0, 1).</summary>
    private float NextRandom()
    {
        uint x = _rngState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rngState = x;
        return (x & 0xFFFFFFu) / 16777216f;
    }

    private float RandomRange(float min, float max)
    {
        return min + (max - min) * NextRandom();
    }
}
