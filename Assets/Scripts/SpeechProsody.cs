using System;
using System.Collections.Generic;

/// <summary>
/// Prosody of one whole speech clip, made by <see cref="SpeechProsody.Analyze"/>: a loudness envelope, a pitch
/// track, syllable nuclei, accents, pauses and phrase-final pitch contours, so a face can raise its brows, nod
/// and blink with the emphasis of what it says. Times are seconds from the clip start. Treat it as read-only; the
/// sampling helpers don't allocate, so they are safe to call every frame from any thread.
/// </summary>
public sealed class ProsodyTrack
{
    public enum AccentKind { Rise, Fall, Loud }
    public enum Contour { Level, Rising, Falling }

    /// <summary>One syllable, found as a voiced loudness peak (its vowel) between two loudness dips.</summary>
    public struct Syllable
    {
        public float Time;          // Nucleus: the vowel's loudness peak
        public float Start, End;    // Dips (or speech edges) either side
        public float Loudness;      // 0..1, peak of the loudness envelope over the vowel
        public float Pitch;         // Semitones vs the clip median over the vowel; NaN when unvoiced
        public float PitchMove;     // Semitones gained across the vowel (+ rising, - falling); 0 when unknown
        public float Prominence;    // 0..1: louder, higher, longer or moving more than syllables within ±1 s
    }

    /// <summary>An emphasised syllable. Rise: pitch peak or rise (brows up). Fall: pitch drop (nod down). Loud: loudness beat.</summary>
    public struct Accent
    {
        public float Time;          // Nucleus of the accented syllable
        public float Strength;      // 0..1 (the syllable's prominence)
        public AccentKind Kind;
        public int Syllable;        // Index into Syllables
    }

    /// <summary>Silence of at least 120 ms between two stretches of speech.</summary>
    public struct Pause
    {
        public float Start, End;
    }

    /// <summary>The end of a stretch of speech followed by a pause or the end of the clip.</summary>
    public struct PhraseEnd
    {
        public float Start;         // Phrase start (first audible frame after the previous pause)
        public float Time;          // Phrase end (last audible frame)
        public Contour Contour;     // Pitch movement over the phrase's final ~300 ms of voicing
        public float Semitones;     // Size of that movement (+ rising)
    }

    public readonly float HopSeconds;       // Frame step of Loudness and PitchSemitones
    public readonly float Duration;         // Clip length
    public readonly float SpeechStart;      // First syllable start (0 when no speech was found)
    public readonly float SpeechEnd;        // Last syllable end
    public readonly float MedianPitchHz;    // The clip's median F0 (0 when nothing is voiced)
    public readonly float PitchSpread;      // 10th-90th percentile range of PitchSemitones: how lively the voice is
    public readonly float[] Loudness;       // 0..1 per frame; identical to NPCFace's envelope (frame i is time i * HopSeconds)
    public readonly float[] PitchSemitones; // Per frame vs MedianPitchHz, NaN when unvoiced (frame i is centred on (i + 0.5) * HopSeconds)
    public readonly Syllable[] Syllables;
    public readonly Accent[] Accents;
    public readonly Pause[] Pauses;
    public readonly PhraseEnd[] PhraseEnds;
    public readonly bool EndsAsQuestion;    // The last phrase ends rising

    internal ProsodyTrack(float hopSeconds, float duration, float speechStart, float speechEnd, float medianPitchHz,
        float pitchSpread, float[] loudness, float[] pitchSemitones, Syllable[] syllables, Accent[] accents,
        Pause[] pauses, PhraseEnd[] phraseEnds)
    {
        HopSeconds = hopSeconds;
        Duration = duration;
        SpeechStart = speechStart;
        SpeechEnd = speechEnd;
        MedianPitchHz = medianPitchHz;
        PitchSpread = pitchSpread;
        Loudness = loudness;
        PitchSemitones = pitchSemitones;
        Syllables = syllables;
        Accents = accents;
        Pauses = pauses;
        PhraseEnds = phraseEnds;
        EndsAsQuestion = phraseEnds.Length > 0 && phraseEnds[phraseEnds.Length - 1].Contour == Contour.Rising;
    }

    /// <summary>Loudness envelope at time t (0..1), sampled exactly as NPCFace samples its own envelope.</summary>
    public float LoudnessAt(float t)
    {
        int n = Loudness.Length;
        if (n == 0) return 0f;
        float x = Math.Max(0f, t) / HopSeconds;
        if (x >= n - 1) return Loudness[n - 1];
        int i = (int)x;
        return Loudness[i] + (Loudness[i + 1] - Loudness[i]) * (x - i);
    }

    /// <summary>Pitch at time t in semitones vs the clip median; NaN when unvoiced.</summary>
    public float PitchAt(float t)
    {
        int n = PitchSemitones.Length;
        if (n == 0) return float.NaN;
        float x = t / HopSeconds - 0.5f;
        if (x <= 0f) return PitchSemitones[0];
        if (x >= n - 1) return PitchSemitones[n - 1];
        int i = (int)x;
        float a = PitchSemitones[i], b = PitchSemitones[i + 1], k = x - i;
        if (float.IsNaN(a)) return k >= 0.5f ? b : float.NaN;
        if (float.IsNaN(b)) return k < 0.5f ? a : float.NaN;
        return a + (b - a) * k;
    }

    /// <summary>Index of the first accent at or after time t, or -1.</summary>
    public int NextAccent(float t)
    {
        int lo = 0, hi = Accents.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Accents[mid].Time < t) lo = mid + 1; else hi = mid;
        }
        return lo < Accents.Length ? lo : -1;
    }

    /// <summary>True when t falls inside one of the Pauses.</summary>
    public bool InPause(float t)
    {
        for (int i = 0; i < Pauses.Length; i++)
            if (t >= Pauses[i].Start && t < Pauses[i].End) return true;
        return false;
    }
}

/// <summary>
/// Speech prosody analyser for whole TTS clips (no timings needed): where the syllables are, which ones are
/// stressed, where the speaker pauses, and whether phrases end rising or falling. Pure C# with no Unity API and no
/// shared state, so it can run on a worker thread when a clip starts (~1.4 ms per second of 24 kHz audio in .NET).
///
/// Pipeline (10 ms frames):
/// 1. Loudness: NPCFace's envelope, bit for bit (RMS, 25 ms attack / 80 ms release, 95th-percentile normalised, gated).
/// 2. Pitch: the clip is low-passed (1.8 kHz) and decimated to ~6 kHz. YIN's cumulative-mean-normalised difference
///    gives up to five period candidates per frame (50-470 Hz); a Viterbi path picks one or "unvoiced" per frame,
///    penalising frame-to-frame jumps, subharmonics and voicing flicker. A second pass pulls each frame toward the
///    first pass's ±300 ms running median, fixing octave errors on voiced runs that follow a gap. Voiced runs under
///    30 ms are dropped and the rest are 5-frame median-smoothed. Semitones are relative to the clip's median F0.
/// 3. Syllables: peaks of the 300-1800 Hz "vowel band" level (nasals and the voice bar fall below it, so sonorant
///    stretches still dip) within 20 dB of loud speech, separated from the previous peak by a 2 dB dip and 80 ms
///    (peak-valley nuclei after de Jong &amp; Wempe 2009). Peaks must be voiced, or else loud (within 12 dB) and
///    vowel-like (at least -5 dB of their power in the band) so creaky, growled and whispered vowels count.
/// 4. Prominence: each syllable's level, pitch peak, vowel length (halved before a phrase break) and pitch movement
///    against the median of its neighbours within ±1 s. Accents are the most prominent syllables (at least 0.35, the
///    best one always), at least 0.6 s apart, at most one per started 2 s of speech.
/// 5. Pauses: full-band silence (30 dB under loud speech) of 120 ms or more; those of 200 ms or more end phrases.
///    Each phrase's ending is classed from its final 300 ms of voicing (see PhraseEnding).
/// </summary>
public static class SpeechProsody
{
    public const float HopSeconds = 0.01f;

    // Loudness envelope: identical to NPCFace.ComputeEnvelope so either can drive the face
    private const float AttackSeconds = 0.025f, ReleaseSeconds = 0.08f, EnvelopePercentile = 0.95f, EnvelopeGate = 0.12f;

    // Pitch: YIN candidates on a low-passed, decimated copy, then a Viterbi path through them
    private const float AnalysisRate = 6000f;               // Decimate to at least this rate (24 kHz -> 6 kHz)
    private const float LowPassHz = 1800f, LowPassTransitionHz = 1000f;
    private const float MinF0 = 50f, MaxF0 = 470f;          // Lag search range: 70-450 Hz voices, plus deep voices' phrase ends
    private const float YinWindowSeconds = 0.025f;
    private const float YinThreshold = 0.15f;               // YIN's absolute threshold: the first dip under it is the period
    private const float CandidateCeiling = 0.6f;            // Dips above this aren't periods
    private const int MaxCandidates = 5;
    private const float OctaveCost = 0.1f;                  // Per octave below the frame's shortest candidate (against subharmonics)
    private const float JumpCost = 1f;                      // Per octave of frame-to-frame change (against octave errors)
    private const float VoicingCost = 0.15f;                // Per voiced/unvoiced switch (against flicker)
    private const float UnvoicedCost = 0.3f;                // Calling an audible frame unvoiced; fades to 0 as it gets quiet
    private const int ReferenceFrames = 30;                 // Pass 2 reference: running median over ±300 ms of pass 1
    private const float OctaveSlack = 0.6f, ReferenceCost = 1f;  // Free within ±0.6 octave of it, then 1 per octave
    private const float MinVoicedSeconds = 0.03f;
    private const int MedianTaps = 5;

    // Levels, relative to the clip's loud speech (95th percentile of the frames within 50 dB of its peak)
    private const float SilenceDb = 30f;                    // Full-band level this far down is silence
    private const float PitchGateDb = 35f;                  // Low-band level this far down skips pitch analysis
    private const float QuietDb = 20f;                      // Below this, frames lean unvoiced
    private const float IntensityWindowSeconds = 0.04f;
    private const float VowelBandHz = 300f;                 // Syllable level ignores energy below this (nasals, voice bar)

    // Syllable nuclei
    private const float NucleusFloorDb = 20f;
    private const float UnvoicedNucleusDb = 12f;            // Creaky or whispered vowels count when this loud...
    private const float VowelShareDb = -5f;                 // ...and this much of their power is in the vowel band (noise: -9 dB)
    private const float MinDipDb = 2f;
    private const float MinNucleusGap = 0.08f;
    private const float CoreDb = 6f;                        // Vowel core: frames within this of the nucleus peak

    // Prominence and accents
    private const float BaselineSeconds = 1f;
    private const float LoudnessUnitDb = 3f, PitchUnitSemitones = 2f, LengthUnitOctaves = 0.5f, MoveUnitSemitones = 3f;
    private const float LoudnessWeight = 0.35f, PitchWeight = 0.35f, LengthWeight = 0.15f, MoveWeight = 0.15f;
    private const float AccentThreshold = 0.35f, MinAccentGap = 0.6f, SpeechSecondsPerAccent = 2f;
    private const float RiseSemitones = 1.5f, FallSemitones = 1.5f;

    // Pauses and phrase endings
    private const float MinPauseSeconds = 0.12f;
    private const float PhraseBreakSeconds = 0.2f;          // Shorter pauses are stop closures and hesitations, not phrase ends
    private const float ContourSeconds = 0.3f, ContourLookback = 0.6f, ContourSemitones = 2f;
    private const int MinFinalVoicedFrames = 5;             // A rising end needs this much voicing in the last syllable,
    private const int MaxFinalUnvoicedFrames = 3;           // lasting to within this of its vowel's end
    private const float EndNearExtreme = 0.5f;              // Semitones: a rising end peaks within this of the window's high
    private const int EndFrames = 8;                        // "The end" of a phrase contour: its last 80 ms of voicing


    /// <summary>
    /// Analyses mono samples at any rate from 8 kHz up (NaN/Inf samples count as 0). Returns null for empty input or
    /// a lower rate. Thread-safe; allocates about 0.65x the input array (17.7 s at 24 kHz: ~1.1 MB).
    /// </summary>
    public static ProsodyTrack Analyze(float[] mono, int sampleRate)
    {
        if (mono == null || mono.Length == 0 || sampleRate < 8000) return null;
        mono = Sanitised(mono);

        int hop = Math.Max(1, (int)Math.Round(HopSeconds * sampleRate));
        int frames = (mono.Length + hop - 1) / hop;
        float frameSeconds = hop / (float)sampleRate;
        float duration = mono.Length / (float)sampleRate;

        var loudness = new float[frames];
        var levelDb = new float[frames];
        Envelope(mono, hop, frames, loudness, levelDb);

        var semitones = new float[frames];
        for (int f = 0; f < frames; f++) semitones[f] = float.NaN;

        float levelRef = LoudReference(levelDb);
        if (levelRef < -100f)   // Digital silence
            return Empty(frameSeconds, duration, 0f, 0f, loudness, semitones);

        // Low-band copy for pitch and syllable level
        int decimation = Math.Max(1, (int)(sampleRate / AnalysisRate));
        float rate = sampleRate / (float)decimation;
        int tauMin = Math.Max(2, (int)(rate / MaxF0));
        int tauMax = (int)Math.Ceiling(rate / MinF0);
        int yinWindow = (int)Math.Round(YinWindowSeconds * rate);
        int levelWindow = (int)Math.Round(IntensityWindowSeconds * rate) | 1;
        int pad = yinWindow + tauMax + levelWindow + hop / decimation + 4;
        float[] low = Decimate(mono, sampleRate, decimation, pad);

        var centres = new int[frames];
        for (int f = 0; f < frames; f++)
            centres[f] = pad + (int)Math.Round((f * hop + hop * 0.5) / decimation);

        var bandDb = BandLevel(low, centres, levelWindow);
        float bandRef = LoudReference(bandDb);
        var vowelDb = BandLevel(HighPassed(low, rate, VowelBandHz), centres, levelWindow);
        float vowelRef = LoudReference(vowelDb);

        var silent = new bool[frames];
        for (int f = 0; f < frames; f++) silent[f] = levelDb[f] < levelRef - SilenceDb;

        // ── Pitch ──
        var f0 = TrackPitch(low, rate, centres, bandDb, bandRef, tauMin, tauMax, yinWindow);
        CleanPitch(f0, Math.Max(1, (int)Math.Round(MinVoicedSeconds / frameSeconds)));
        float medianHz = MedianVoiced(f0);
        float spread = 0f;
        if (medianHz > 0f)
        {
            for (int f = 0; f < frames; f++)
                if (f0[f] > 0f) semitones[f] = 12f * (float)Math.Log(f0[f] / medianHz, 2.0);
            spread = Spread(semitones);
        }

        // ── Syllables ──
        var nuclei = FindNuclei(vowelDb, vowelRef, f0, silent, frameSeconds, mono, centres, pad, decimation, levelWindow * decimation);
        if (nuclei.Count == 0)
            return Empty(frameSeconds, duration, medianHz, spread, loudness, semitones);

        int count = nuclei.Count;
        var starts = new int[count];
        var ends = new int[count];
        var phraseFinal = new bool[count];
        var silenceStart = new int[count];
        var silenceLength = new int[count];
        SyllableBounds(nuclei, vowelDb, silent, starts, ends, phraseFinal, silenceStart, silenceLength);

        var syllables = new ProsodyTrack.Syllable[count];
        var features = new SyllableFeatures[count];
        for (int k = 0; k < count; k++)
            features[k] = Measure(nuclei[k], starts[k], ends[k], vowelDb, semitones, loudness);

        // ── Pauses and phrases ──
        var pauses = new List<ProsodyTrack.Pause>();
        var phrases = new List<ProsodyTrack.PhraseEnd>();
        int minPause = Math.Max(1, (int)Math.Round(MinPauseSeconds / frameSeconds));
        int minBreak = Math.Max(minPause, (int)Math.Round(PhraseBreakSeconds / frameSeconds));
        int phraseStart = starts[0];
        for (int k = 0; k < count; k++)
        {
            bool last = k == count - 1;
            // Only an unbroken silence counts: a closure, a fricative and another closure aren't a pause
            int gap = last ? 0 : silenceLength[k];
            if (!last && phraseFinal[k] && gap >= minPause)
            {
                pauses.Add(new ProsodyTrack.Pause
                {
                    Start = silenceStart[k] * frameSeconds,
                    End = (silenceStart[k] + silenceLength[k]) * frameSeconds
                });
            }
            phraseFinal[k] = last || (phraseFinal[k] && gap >= minBreak);
            if (!phraseFinal[k]) continue;

            phrases.Add(PhraseEnding(phraseStart, starts[k], features[k].CoreEnd, ends[k], semitones, frameSeconds));
            if (!last) phraseStart = starts[k + 1];
        }

        // ── Prominence and accents ──
        Prominence(features, nuclei, phraseFinal, frameSeconds);
        for (int k = 0; k < count; k++)
        {
            syllables[k] = new ProsodyTrack.Syllable
            {
                Time = (nuclei[k] + 0.5f) * frameSeconds,
                Start = starts[k] * frameSeconds,
                End = ends[k] * frameSeconds,
                Loudness = features[k].Loudness,
                Pitch = features[k].Pitch,
                PitchMove = features[k].Move,
                Prominence = features[k].Prominence,
            };
        }

        float speechSeconds = 0f;
        foreach (var p in phrases) speechSeconds += p.Time - p.Start;
        var accents = PickAccents(syllables, features, speechSeconds);

        return new ProsodyTrack(frameSeconds, duration, starts[0] * frameSeconds, ends[count - 1] * frameSeconds,
            medianHz, spread, loudness, semitones, syllables, accents, pauses.ToArray(), phrases.ToArray());
    }

    private static ProsodyTrack Empty(float frameSeconds, float duration, float medianHz, float spread, float[] loudness, float[] semitones)
    {
        return new ProsodyTrack(frameSeconds, duration, 0f, 0f, medianHz, spread, loudness, semitones,
            new ProsodyTrack.Syllable[0], new ProsodyTrack.Accent[0], new ProsodyTrack.Pause[0], new ProsodyTrack.PhraseEnd[0]);
    }

    // ─── Levels ──────────────────────────────────────────────────────────

    private static float[] Sanitised(float[] mono)
    {
        for (int i = 0; i < mono.Length; i++)
        {
            if (!float.IsNaN(mono[i]) && !float.IsInfinity(mono[i])) continue;
            var copy = (float[])mono.Clone();
            for (int j = i; j < copy.Length; j++)
                if (float.IsNaN(copy[j]) || float.IsInfinity(copy[j])) copy[j] = 0f;
            return copy;
        }
        return mono;
    }

    // RMS in hops, opened fast and closed slower, normalised to the 95th percentile and gated: the exact
    // arithmetic of NPCFace.ComputeEnvelope. Also returns each hop's raw RMS in dB for silence detection.
    private static void Envelope(float[] mono, int hop, int frames, float[] envelope, float[] levelDb)
    {
        float attack = 1f - (float)Math.Exp(-HopSeconds / AttackSeconds);
        float release = 1f - (float)Math.Exp(-HopSeconds / ReleaseSeconds);
        float level = 0f;
        for (int f = 0; f < frames; f++)
        {
            int start = f * hop, end = Math.Min(mono.Length, start + hop);
            double sum = 0;
            for (int i = start; i < end; i++) sum += mono[i] * mono[i];
            float rms = (float)Math.Sqrt((float)(sum / (end - start)));
            level += (rms - level) * (rms > level ? attack : release);
            envelope[f] = level;
            levelDb[f] = 20f * (float)Math.Log10(rms + 1e-9f);
        }

        var sorted = (float[])envelope.Clone();
        Array.Sort(sorted);
        float reference = Math.Max(sorted[Clamp((int)Math.Round(frames * EnvelopePercentile), 0, frames - 1)], 1e-4f);
        for (int f = 0; f < frames; f++)
            envelope[f] = Clamp01((envelope[f] / reference - EnvelopeGate) / (1f - EnvelopeGate));
    }

    // Level of loud speech: the 95th percentile of frames within 50 dB of the clip's peak, so long silences
    // around a short word don't drag it down
    private static float LoudReference(float[] db)
    {
        float max = float.MinValue;
        for (int i = 0; i < db.Length; i++) max = Math.Max(max, db[i]);
        var active = new List<float>(db.Length);
        for (int i = 0; i < db.Length; i++)
            if (db[i] >= max - 50f) active.Add(db[i]);
        active.Sort();
        return active[Clamp((int)Math.Round(0.95f * (active.Count - 1)), 0, active.Count - 1)];
    }

    // Windowed-sinc low-pass and decimation in one pass (only the kept samples are computed), with the clip's DC
    // removed. The result is zero-padded by `pad` samples at both ends so frame windows never need bounds checks.
    private static float[] Decimate(float[] x, int sampleRate, int factor, int pad)
    {
        double mean = 0;
        for (int i = 0; i < x.Length; i++) mean += x[i];
        float dc = (float)(mean / x.Length);

        float rate = sampleRate / (float)factor;
        float cutoff = Math.Min(LowPassHz, 0.3f * rate);
        float transition = Math.Min(LowPassTransitionHz, 0.15f * rate);
        int taps = (int)Math.Ceiling(5.5f * sampleRate / transition) | 1;
        int half = taps / 2;
        var h = new float[taps];
        double norm = 0;
        double fc = cutoff / sampleRate;
        for (int i = 0; i < taps; i++)
        {
            int n = i - half;
            double sinc = n == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * n) / (Math.PI * n);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            h[i] = (float)(sinc * w);
            norm += h[i];
        }
        for (int i = 0; i < taps; i++) h[i] = (float)(h[i] / norm);

        int length = (x.Length + factor - 1) / factor;
        var y = new float[length + 2 * pad];
        for (int k = 0; k < length; k++)
        {
            int first = k * factor - half;
            float acc = 0f;
            if (first >= 0 && first + taps <= x.Length)
            {
                for (int j = 0; j < taps; j++) acc += h[j] * x[first + j];
                acc -= dc;
            }
            else
            {
                for (int j = 0; j < taps; j++)
                {
                    int s = first + j;
                    if (s >= 0 && s < x.Length) acc += h[j] * (x[s] - dc);
                }
            }
            y[pad + k] = acc;
        }
        return y;
    }

    // Hann-weighted mean power of the low band around each frame centre, in dB
    private static float[] BandLevel(float[] low, int[] centres, int window)
    {
        var w = new float[window];
        double wSum = 0;
        for (int i = 0; i < window; i++)
        {
            w[i] = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * (i + 1) / (window + 1));
            wSum += w[i];
        }
        var db = new float[centres.Length];
        for (int f = 0; f < centres.Length; f++)
        {
            int start = centres[f] - window / 2;
            double e = 0;
            for (int i = 0; i < window; i++) { float v = low[start + i]; e += w[i] * v * v; }
            db[f] = 10f * (float)Math.Log10(e / wSum + 1e-12);
        }
        return db;
    }

    // Zero-phase 2nd-order Butterworth high-pass (run forwards, then backwards)
    private static float[] HighPassed(float[] x, float rate, float cutoff)
    {
        double w0 = 2 * Math.PI * cutoff / rate, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * 0.70710678);
        double a0 = 1 + alpha;
        float b0 = (float)((1 + cos) / 2 / a0), b1 = (float)(-(1 + cos) / a0), b2 = b0;
        float a1 = (float)(-2 * cos / a0), a2 = (float)((1 - alpha) / a0);
        var y = new float[x.Length];
        float x1 = 0f, x2 = 0f, y1 = 0f, y2 = 0f;
        for (int i = 0; i < x.Length; i++)
        {
            float v = b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v;
            y[i] = v;
        }
        x1 = x2 = y1 = y2 = 0f;
        for (int i = x.Length - 1; i >= 0; i--)
        {
            float u = y[i];
            float v = b0 * u + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = u; y2 = y1; y1 = v;
            y[i] = v;
        }
        return y;
    }

    // ─── Pitch ───────────────────────────────────────────────────────────

    // YIN candidates per frame, then the cheapest path through candidates and "unvoiced". Returns Hz (0 = unvoiced).
    private static float[] TrackPitch(float[] low, float rate, int[] centres, float[] bandDb, float bandRef,
        int tauMin, int tauMax, int window)
    {
        int frames = centres.Length;
        const int K = MaxCandidates;
        var lagLog = new float[frames * K];     // log2 of each candidate's lag
        var dips = new float[frames * K];       // its CMND value (0 = perfectly periodic)
        var counts = new int[frames];
        var unvoiced = new float[frames];

        var diff = new float[tauMax + 2];
        var cmnd = new float[tauMax + 2];
        var minLags = new float[tauMax + 2];
        var minDips = new float[tauMax + 2];

        for (int f = 0; f < frames; f++)
        {
            float relative = bandDb[f] - bandRef;
            if (relative < -PitchGateDb) continue;     // Too quiet to have a pitch: unvoiced at no cost
            unvoiced[f] = UnvoicedCost * Clamp01((relative + PitchGateDb) / (PitchGateDb - QuietDb));

            // Difference function centred on the frame for every lag
            int c = centres[f];
            for (int tau = 1; tau <= tauMax + 1; tau++)
            {
                int a = c - (window + tau) / 2, b = a + tau;
                float sum = 0f;
                for (int j = 0; j < window; j++)
                {
                    float d = low[a + j] - low[b + j];
                    sum += d * d;
                }
                diff[tau] = sum;
            }

            // Cumulative-mean normalisation
            cmnd[0] = 1f;
            float running = 0f;
            for (int tau = 1; tau <= tauMax + 1; tau++)
            {
                running += diff[tau];
                cmnd[tau] = running > 1e-12f ? diff[tau] * tau / running : 1f;
            }

            // Local minima under the ceiling, refined by parabolic interpolation
            int minima = 0;
            for (int tau = tauMin; tau <= tauMax; tau++)
            {
                float v = cmnd[tau];
                if (v >= CandidateCeiling || v > cmnd[tau - 1] || v >= cmnd[tau + 1]) continue;
                float left = cmnd[tau - 1], right = cmnd[tau + 1];
                float denom = left - 2f * v + right;
                float offset = denom > 1e-9f ? Clamp(0.5f * (left - right) / denom, -0.5f, 0.5f) : 0f;
                minLags[minima] = tau + offset;
                minDips[minima] = Math.Max(0f, v - 0.25f * (left - right) * offset);
                minima++;
            }
            if (minima == 0) continue;

            // Keep YIN's pick (the shortest lag under the threshold) plus the deepest others
            int baseIndex = f * K;
            int kept = 0;
            int yinPick = -1;
            for (int m = 0; m < minima; m++)
                if (minDips[m] < YinThreshold) { yinPick = m; break; }
            if (yinPick >= 0)
            {
                lagLog[baseIndex] = (float)Math.Log(minLags[yinPick], 2.0);
                dips[baseIndex] = minDips[yinPick];
                kept = 1;
                minDips[yinPick] = float.MaxValue;
            }
            while (kept < K)
            {
                int best = -1;
                for (int m = 0; m < minima; m++)
                    if (minDips[m] < float.MaxValue && (best < 0 || minDips[m] < minDips[best])) best = m;
                if (best < 0) break;
                lagLog[baseIndex + kept] = (float)Math.Log(minLags[best], 2.0);
                dips[baseIndex + kept] = minDips[best];
                minDips[best] = float.MaxValue;
                kept++;
            }
            counts[f] = kept;
        }

        // Pass 1 finds the cheapest path; pass 2 re-runs it pulled toward pass 1's running median, which fixes
        // octave errors on voiced runs that follow a gap (where the path has no pitch to stay continuous with)
        var f0 = new float[frames];
        Decode(lagLog, dips, counts, unvoiced, null, rate, f0);
        var reference = RunningMedianLog(f0, ReferenceFrames);
        if (reference != null) Decode(lagLog, dips, counts, unvoiced, reference, rate, f0);
        return f0;
    }

    // Viterbi over states 0..K-1 (the frame's candidates) and K (unvoiced). With a reference (log2 Hz per frame),
    // candidates more than OctaveSlack away from it pay ReferenceCost per octave beyond. Writes Hz (0 = unvoiced).
    private static void Decode(float[] lagLog, float[] dips, int[] counts, float[] unvoiced, float[] reference, float rate, float[] f0)
    {
        int frames = counts.Length;
        const int K = MaxCandidates;
        int states = K + 1;
        float logRate = (float)Math.Log(rate, 2.0);
        var back = new byte[frames * states];
        var cost = new float[states];
        var next = new float[states];
        for (int f = 0; f < frames; f++)
        {
            int n = counts[f];
            float shortest = float.MaxValue;
            for (int s = 0; s < n; s++) shortest = Math.Min(shortest, lagLog[f * K + s]);

            for (int s = 0; s < states; s++)
            {
                bool voiced = s < K;
                if (voiced && s >= n) { next[s] = float.MaxValue; continue; }
                float local = unvoiced[f];
                if (voiced)
                {
                    local = dips[f * K + s] + OctaveCost * (lagLog[f * K + s] - shortest);
                    if (reference != null && !float.IsNaN(reference[f]))
                        local += ReferenceCost * Math.Max(0f, Math.Abs(logRate - lagLog[f * K + s] - reference[f]) - OctaveSlack);
                }
                if (f == 0) { next[s] = local; back[s] = (byte)K; continue; }

                float best = float.MaxValue;
                int arg = K;
                int prevCount = counts[f - 1];
                for (int p = 0; p < states; p++)
                {
                    if (cost[p] == float.MaxValue) continue;
                    bool prevVoiced = p < K;
                    if (prevVoiced && p >= prevCount) continue;
                    float transition;
                    if (voiced && prevVoiced) transition = JumpCost * Math.Abs(lagLog[f * K + s] - lagLog[(f - 1) * K + p]);
                    else if (voiced != prevVoiced) transition = VoicingCost;
                    else transition = 0f;
                    float total = cost[p] + transition;
                    if (total < best) { best = total; arg = p; }
                }
                next[s] = best + local;
                back[f * states + s] = (byte)arg;
            }
            var swap = cost; cost = next; next = swap;
        }

        int state = K;
        float lowest = float.MaxValue;
        for (int s = 0; s < states; s++)
            if (cost[s] < lowest) { lowest = cost[s]; state = s; }
        for (int f = frames - 1; f >= 0; f--)
        {
            f0[f] = state < K ? rate / (float)Math.Pow(2.0, lagLog[f * K + state]) : 0f;
            state = back[f * states + state];
        }
    }

    // Median log2 F0 of the voiced frames within ±radius of each frame (NaN where there are none); null if nothing is voiced
    private static float[] RunningMedianLog(float[] f0, int radius)
    {
        int frames = f0.Length;
        var logs = new float[frames];
        bool any = false;
        for (int f = 0; f < frames; f++)
        {
            logs[f] = f0[f] > 0f ? (float)Math.Log(f0[f], 2.0) : float.NaN;
            any |= f0[f] > 0f;
        }
        if (!any) return null;

        var result = new float[frames];
        var window = new float[2 * radius + 1];
        for (int f = 0; f < frames; f++)
        {
            int n = 0;
            for (int i = Math.Max(0, f - radius); i <= Math.Min(frames - 1, f + radius); i++)
                if (!float.IsNaN(logs[i])) window[n++] = logs[i];
            if (n == 0) { result[f] = float.NaN; continue; }
            Array.Sort(window, 0, n);
            result[f] = window[n / 2];
        }
        return result;
    }

    // Drops voiced runs shorter than minRun frames and median-smooths the rest (within each run)
    private static void CleanPitch(float[] f0, int minRun)
    {
        int frames = f0.Length;
        var copy = new float[frames];
        var window = new float[MedianTaps];
        int f = 0;
        while (f < frames)
        {
            if (f0[f] <= 0f) { f++; continue; }
            int end = f;
            while (end < frames && f0[end] > 0f) end++;
            if (end - f < minRun)
            {
                for (int i = f; i < end; i++) f0[i] = 0f;
            }
            else
            {
                Array.Copy(f0, f, copy, f, end - f);
                for (int i = f; i < end; i++)
                {
                    int lo = Math.Max(f, i - MedianTaps / 2), hi = Math.Min(end - 1, i + MedianTaps / 2);
                    int n = 0;
                    for (int j = lo; j <= hi; j++) window[n++] = copy[j];
                    Array.Sort(window, 0, n);
                    f0[i] = n % 2 == 1 ? window[n / 2] : 0.5f * (window[n / 2 - 1] + window[n / 2]);
                }
            }
            f = end;
        }
    }

    private static float MedianVoiced(float[] f0)
    {
        var voiced = new List<float>(f0.Length);
        for (int i = 0; i < f0.Length; i++) if (f0[i] > 0f) voiced.Add(f0[i]);
        if (voiced.Count == 0) return 0f;
        voiced.Sort();
        return voiced[voiced.Count / 2];
    }

    private static float Spread(float[] semitones)
    {
        var voiced = new List<float>(semitones.Length);
        for (int i = 0; i < semitones.Length; i++) if (!float.IsNaN(semitones[i])) voiced.Add(semitones[i]);
        if (voiced.Count < 2) return 0f;
        voiced.Sort();
        return voiced[(int)(0.9f * (voiced.Count - 1))] - voiced[(int)(0.1f * (voiced.Count - 1))];
    }

    // ─── Syllables ───────────────────────────────────────────────────────

    // Peaks of the vowel-band level, merged unless a 2 dB dip and 80 ms separate them. Unvoiced peaks (creak,
    // whisper, growl) count only when loud and vowel-like: fricatives and noise put most of their power elsewhere.
    private static List<int> FindNuclei(float[] bandDb, float bandRef, float[] f0, bool[] silent, float frameSeconds,
        float[] mono, int[] centres, int pad, int decimation, int window)
    {
        var nuclei = new List<int>();
        float floor = bandRef - NucleusFloorDb;
        int minGap = Math.Max(1, (int)Math.Round(MinNucleusGap / frameSeconds));
        for (int f = 1; f < bandDb.Length - 1; f++)
        {
            float v = bandDb[f];
            if (v < floor || silent[f] || v < bandDb[f - 1] || v <= bandDb[f + 1]) continue;
            if (!VoicedNear(f0, f, 3) && (v < bandRef - UnvoicedNucleusDb ||
                v - FullBandDb(mono, (centres[f] - pad) * decimation, window) < VowelShareDb)) continue;

            if (nuclei.Count > 0)
            {
                int last = nuclei.Count - 1, p = nuclei[last];
                float dip = float.MaxValue;
                bool silenceBetween = false;
                for (int i = p + 1; i < f; i++)
                {
                    dip = Math.Min(dip, bandDb[i]);
                    silenceBetween |= silent[i];
                }
                if (!silenceBetween && (Math.Min(bandDb[p], v) - dip < MinDipDb || f - p < minGap))
                {
                    if (v > bandDb[p]) nuclei[last] = f;
                    continue;
                }
            }
            nuclei.Add(f);
        }
        return nuclei;
    }

    // Hann-weighted mean power (DC removed) of the full-band signal around a sample, in dB: the same measure as
    // BandLevel, so their difference is the share of power in the band
    private static float FullBandDb(float[] mono, int centre, int window)
    {
        int start = centre - window / 2;
        double sum = 0, power = 0, weights = 0;
        for (int i = 0; i < window; i++)
        {
            int s = start + i;
            if (s < 0 || s >= mono.Length) continue;
            sum += mono[s];
            weights += 1;
        }
        if (weights == 0) return -120f;
        float mean = (float)(sum / weights);
        weights = 0;
        for (int i = 0; i < window; i++)
        {
            int s = start + i;
            float w = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * (i + 1) / (window + 1));
            weights += w;
            if (s < 0 || s >= mono.Length) continue;
            float v = mono[s] - mean;
            power += w * v * v;
        }
        return 10f * (float)Math.Log10(power / weights + 1e-12);
    }

    private static bool VoicedNear(float[] f0, int f, int radius)
    {
        for (int i = Math.Max(0, f - radius); i <= Math.Min(f0.Length - 1, f + radius); i++)
            if (f0[i] > 0f) return true;
        return false;
    }

    // Syllables meet at the deepest dip between nuclei, or stop at the edges of a silence between them.
    // End indices are exclusive. silenceStart/silenceLength hold the longest unbroken silent run after syllable k.
    private static void SyllableBounds(List<int> nuclei, float[] bandDb, bool[] silent, int[] starts, int[] ends,
        bool[] phraseFinal, int[] silenceStart, int[] silenceLength)
    {
        int count = nuclei.Count, frames = bandDb.Length;
        int s = nuclei[0];
        while (s > 0 && !silent[s - 1]) s--;
        starts[0] = s;

        for (int k = 0; k < count - 1; k++)
        {
            int a = nuclei[k], b = nuclei[k + 1];
            int firstSilent = -1, lastSilent = -1, dip = a + 1, run = 0;
            for (int i = a + 1; i < b; i++)
            {
                if (silent[i])
                {
                    if (firstSilent < 0) firstSilent = i;
                    lastSilent = i;
                    run++;
                    if (run > silenceLength[k])
                    {
                        silenceLength[k] = run;
                        silenceStart[k] = i - run + 1;
                    }
                }
                else
                {
                    run = 0;
                }
                if (bandDb[i] < bandDb[dip]) dip = i;
            }
            if (firstSilent >= 0)
            {
                ends[k] = firstSilent;
                starts[k + 1] = lastSilent + 1;
                phraseFinal[k] = true;
            }
            else
            {
                ends[k] = dip;
                starts[k + 1] = dip;
            }
        }

        int e = nuclei[count - 1] + 1;
        while (e < frames && !silent[e]) e++;
        ends[count - 1] = e;
        phraseFinal[count - 1] = true;
    }

    private struct SyllableFeatures
    {
        public float LevelDb, Pitch, PitchPeak, Move, VowelFrames, Loudness, Prominence;
        public float PitchShare;    // How much of the prominence came from pitch (vs loudness)
        public int CoreEnd;         // Last frame of the vowel core
    }

    private static SyllableFeatures Measure(int nucleus, int start, int end, float[] bandDb, float[] semitones, float[] loudness)
    {
        float peak = bandDb[nucleus];
        int coreStart = nucleus, coreEnd = nucleus;
        while (coreStart > start && bandDb[coreStart - 1] >= peak - CoreDb) coreStart--;
        while (coreEnd < end - 1 && bandDb[coreEnd + 1] >= peak - CoreDb) coreEnd++;

        var m = new SyllableFeatures { LevelDb = peak, Pitch = float.NaN, PitchPeak = float.NaN, VowelFrames = coreEnd - coreStart + 1, CoreEnd = coreEnd };
        float sum = 0f;
        int voiced = 0;
        for (int f = coreStart; f <= coreEnd; f++)
        {
            m.Loudness = Math.Max(m.Loudness, loudness[f]);
            float st = semitones[f];
            if (float.IsNaN(st)) continue;
            sum += st;
            voiced++;
            m.PitchPeak = float.IsNaN(m.PitchPeak) ? st : Math.Max(m.PitchPeak, st);
        }
        if (voiced > 0) m.Pitch = sum / voiced;

        // Movement across the vowel and its voiced edges: mean of the last 3 voiced frames minus the first 3
        int lo = Math.Max(start, coreStart - 3), hi = Math.Min(end - 1, coreEnd + 3);
        float head = 0f, tail = 0f;
        int nHead = 0, nTail = 0;
        for (int f = lo; f <= hi && nHead < 3; f++)
            if (!float.IsNaN(semitones[f])) { head += semitones[f]; nHead++; }
        for (int f = hi; f >= lo && nTail < 3; f--)
            if (!float.IsNaN(semitones[f])) { tail += semitones[f]; nTail++; }
        if (nHead == 3 && nTail == 3) m.Move = tail / 3f - head / 3f;
        return m;
    }

    // Each syllable against the median of its neighbours within ±1 s (the whole clip when it has few syllables)
    private static void Prominence(SyllableFeatures[] f, List<int> nuclei, bool[] phraseFinal, float frameSeconds)
    {
        int count = f.Length;
        int radius = (int)Math.Round(BaselineSeconds / frameSeconds);
        var levels = new List<float>(count);
        var pitches = new List<float>(count);
        var lengths = new List<float>(count);
        for (int k = 0; k < count; k++)
        {
            levels.Clear(); pitches.Clear(); lengths.Clear();
            for (int j = 0; j < count; j++)
            {
                if (j == k || Math.Abs(nuclei[j] - nuclei[k]) > radius) continue;
                Gather(f[j], levels, pitches, lengths);
            }
            if (levels.Count < 2)
            {
                levels.Clear(); pitches.Clear(); lengths.Clear();
                for (int j = 0; j < count; j++) if (j != k) Gather(f[j], levels, pitches, lengths);
            }

            float loud = 0f, pitch = 0f, length = 0f, move = 0f;
            if (levels.Count > 0)
            {
                loud = Math.Max(0f, (f[k].LevelDb - Median(levels)) / LoudnessUnitDb);
                length = Math.Max(0f, (float)Math.Log(f[k].VowelFrames / Median(lengths), 2.0) / LengthUnitOctaves);
                if (phraseFinal[k]) length *= 0.5f;     // Phrase-final lengthening isn't emphasis
                if (pitches.Count > 0 && !float.IsNaN(f[k].PitchPeak))
                    pitch = Math.Max(0f, (f[k].PitchPeak - Median(pitches)) / PitchUnitSemitones);
            }
            else
            {
                loud = 1f;      // A lone syllable carries the whole line
            }
            move = Math.Abs(f[k].Move) / MoveUnitSemitones;

            float fromPitch = PitchWeight * Math.Min(pitch, 3f) + MoveWeight * Math.Min(move, 3f);
            float fromLevel = LoudnessWeight * Math.Min(loud, 3f) + LengthWeight * Math.Min(length, 3f);
            float raw = fromPitch + fromLevel;
            f[k].Prominence = 1f - (float)Math.Exp(-raw);
            f[k].PitchShare = raw > 1e-6f ? fromPitch / raw : 0f;
        }
    }

    private static void Gather(SyllableFeatures s, List<float> levels, List<float> pitches, List<float> lengths)
    {
        levels.Add(s.LevelDb);
        lengths.Add(s.VowelFrames);
        if (!float.IsNaN(s.PitchPeak)) pitches.Add(s.PitchPeak);
    }

    // ─── Accents ─────────────────────────────────────────────────────────

    // The most prominent syllables, at least MinAccentGap apart, at most one per started SpeechSecondsPerAccent of
    // speech; after the first, only those at least AccentThreshold. A line with speech always gets its best syllable.
    private static ProsodyTrack.Accent[] PickAccents(ProsodyTrack.Syllable[] syllables, SyllableFeatures[] features, float speechSeconds)
    {
        int count = syllables.Length;
        var order = new int[count];
        for (int k = 0; k < count; k++) order[k] = k;
        Array.Sort(order, (a, b) => syllables[b].Prominence.CompareTo(syllables[a].Prominence));

        int budget = Math.Max(1, (int)Math.Ceiling(speechSeconds / SpeechSecondsPerAccent - 0.05f));
        var picked = new List<int>(budget);
        foreach (int k in order)
        {
            if (picked.Count >= budget) break;
            if (picked.Count > 0 && syllables[k].Prominence < AccentThreshold) break;
            bool clear = true;
            foreach (int p in picked)
                if (Math.Abs(syllables[p].Time - syllables[k].Time) < MinAccentGap) { clear = false; break; }
            if (clear) picked.Add(k);
        }
        picked.Sort();

        var accents = new ProsodyTrack.Accent[picked.Count];
        for (int i = 0; i < picked.Count; i++)
        {
            int k = picked[i];
            var s = syllables[k];
            ProsodyTrack.AccentKind kind = ProsodyTrack.AccentKind.Loud;
            if (features[k].PitchShare >= 0.5f)
                kind = s.PitchMove <= -FallSemitones ? ProsodyTrack.AccentKind.Fall : ProsodyTrack.AccentKind.Rise;
            else if (s.PitchMove >= RiseSemitones * 2f)
                kind = ProsodyTrack.AccentKind.Rise;
            accents[i] = new ProsodyTrack.Accent { Time = s.Time, Strength = s.Prominence, Kind = kind, Syllable = k };
        }
        return accents;
    }

    // ─── Phrase endings ──────────────────────────────────────────────────

    // The phrase's last ContourSeconds of voiced frames (looking back at most ContourLookback). Rising: the peak of the
    // final 80 ms is the window's high and sits ContourSemitones above its low (10th percentile), so V-shaped final
    // rises and rises with a small drop at voice offset count, while an accent peak that falls away doesn't. Falling
    // mirrors it, or the window's Theil-Sen slope drops ContourSemitones. A rise also needs the final vowel voiced to
    // its end and its last voiced stretch not to fall overall: a last word that turns creaky or whispered, or starts
    // high and falls away (typical of statements and exclamations), isn't a question even when it starts higher than
    // the word before.
    private static ProsodyTrack.PhraseEnd PhraseEnding(int start, int lastSyllable, int lastCoreEnd, int end, float[] semitones, float frameSeconds)
    {
        var phrase = new ProsodyTrack.PhraseEnd { Start = start * frameSeconds, Time = end * frameSeconds, Contour = ProsodyTrack.Contour.Level };
        int want = (int)Math.Round(ContourSeconds / frameSeconds);
        int limit = Math.Max(start, end - (int)Math.Round(ContourLookback / frameSeconds));
        var points = new List<int>(want);
        for (int f = end - 1; f >= limit && points.Count < want; f--)
            if (!float.IsNaN(semitones[f])) points.Add(f);
        if (points.Count < MinFinalVoicedFrames) return phrase;

        // points[0] is the latest frame
        var values = new List<float>(points.Count);
        foreach (int f in points) values.Add(semitones[f]);
        float endPeak = float.MinValue, endTrough = float.MaxValue;
        for (int i = 0; i < Math.Min(EndFrames, points.Count); i++)
        {
            endPeak = Math.Max(endPeak, semitones[points[i]]);
            endTrough = Math.Min(endTrough, semitones[points[i]]);
        }
        values.Sort();
        float low = values[(int)(0.1f * (values.Count - 1))], high = values[(int)Math.Ceiling(0.9f * (values.Count - 1))];

        int voicedInLast = 0;
        foreach (int f in points) if (f >= lastSyllable) voicedInLast++;
        bool voicedToEnd = voicedInLast >= MinFinalVoicedFrames && points[0] >= lastCoreEnd - MaxFinalUnvoicedFrames;

        // The final voiced stretch must not fall overall (a high start that falls away is a statement's last accent)
        int runStart = points[0];
        while (runStart > start && !float.IsNaN(semitones[runStart - 1])) runStart--;
        bool runRises = points[0] - runStart + 1 >= MinFinalVoicedFrames &&
            semitones[points[0]] + semitones[points[0] - 1] + semitones[points[0] - 2] >=
            semitones[runStart] + semitones[runStart + 1] + semitones[runStart + 2];

        if (voicedToEnd && runRises && endPeak - low >= ContourSemitones && endPeak >= high - EndNearExtreme)
        {
            phrase.Contour = ProsodyTrack.Contour.Rising;
            phrase.Semitones = endPeak - low;
        }
        else if (high - endTrough >= ContourSemitones && endTrough <= low + EndNearExtreme)
        {
            phrase.Contour = ProsodyTrack.Contour.Falling;
            phrase.Semitones = endTrough - high;
        }
        else
        {
            var slopes = new List<float>(points.Count * (points.Count - 1) / 2);
            for (int i = 0; i < points.Count; i++)
                for (int j = i + 1; j < points.Count; j++)
                    slopes.Add((semitones[points[i]] - semitones[points[j]]) / ((points[i] - points[j]) * frameSeconds));
            phrase.Semitones = Median(slopes) * (points[0] - points[points.Count - 1]) * frameSeconds;
            if (phrase.Semitones <= -ContourSemitones) phrase.Contour = ProsodyTrack.Contour.Falling;
        }
        return phrase;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static float Median(List<float> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : 0.5f * (values[n / 2 - 1] + values[n / 2]);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
