using System.Collections.Generic;

/// <summary>
/// One phoneme of a synthesized line, as reported by the TTS model: the Misaki symbol and its span in
/// seconds from the first sample of the returned audio. Tavern-local copy, so lip sync code doesn't
/// depend on the SDK version.
/// </summary>
public struct PhonemeSpan
{
    public string phoneme;
    public float start;
    public float end;
}

/// <summary>
/// Exact lip-sync: turns the per-phoneme timings Kokoro reports (timed model + SDK with phoneme timings)
/// into Rhubarb-style mouth cues (A-H, X) for <see cref="NPCFace.SetLipSyncCues"/>. No audio analysis, so
/// closures land on every M/B/P and the lower lip tucks on F/V. Symbols are Misaki's (US/GB), after its
/// ɾ->T and ʔ->t swaps; diphthongs are single capitals (A=eɪ, I=aɪ, O=oʊ, W=aʊ, Y=ɔɪ, Q=əʊ).
/// Thread-safe (no Unity API).
/// </summary>
public static class PhonemeLipSync
{
    private const float ShortGapSeconds = 0.08f;    // Word gaps this short keep the previous shape
    private const float LengthToleranceSeconds = 0.1f;

    private static readonly Dictionary<char, char> Shapes = BuildShapes(
        ("mbp", 'A'),                                   // closed
        ("tdkɡgsznŋTjiɪᵻʃʒʧʤθðçx", 'B'),               // slightly open, teeth
        ("ɛæʌəᵊAhe", 'C'),                              // open
        ("ɑIaɐɒ", 'D'),                                 // wide open
        ("ɔɜɚɹrY", 'E'),                                // rounded
        ("uʊOWwQo", 'F'),                               // puckered
        ("fv", 'G'),                                    // teeth on lip
        ("l", 'H'));                                    // tongue up

    /// <summary>
    /// Cues for a clip of <paramref name="clipLength"/> seconds, or null when the timings don't fit the clip
    /// (caller falls back to audio analysis). <paramref name="leadSeconds"/> shows each shape slightly
    /// before its sound, as real mouths do.
    /// </summary>
    public static LipSyncCue[] FromPhonemes(PhonemeSpan[] spans, float clipLength, float leadSeconds = 0f)
    {
        if (spans == null || spans.Length == 0 || clipLength <= 0f) return null;

        var cues = new List<LipSyncCue>(spans.Length);
        float stressStart = -1f;
        float lastEnd = 0f;
        foreach (PhonemeSpan span in spans)
        {
            if (string.IsNullOrEmpty(span.phoneme) || !(span.end > span.start)) continue;
            lastEnd = span.end;
            char c = span.phoneme[0];

            // Stress marks come before their vowel: their time belongs to it
            if (c == 'ˈ' || c == 'ˌ')
            {
                if (stressStart < 0f) stressStart = span.start;
                continue;
            }
            // Length mark and nasal tilde follow their phoneme: hold it
            if (c == 'ː' || c == '̃')
            {
                if (cues.Count > 0) Extend(cues, span.end);
                continue;
            }

            float start = stressStart >= 0f ? stressStart : span.start;
            stressStart = -1f;

            if (c == ' ' && cues.Count > 0 && span.end - span.start <= ShortGapSeconds)
            {
                Extend(cues, span.end);
                continue;
            }
            Append(cues, start, span.end, ShapeFor(c));
        }

        if (cues.Count == 0 || lastEnd > clipLength + LengthToleranceSeconds) return null;

        var result = new List<LipSyncCue>(cues.Count);
        foreach (LipSyncCue cue in cues)
        {
            float start = cue.start - leadSeconds;
            float end = cue.end - leadSeconds;
            if (start < 0f) start = 0f;
            if (end > clipLength) end = clipLength;
            if (end > start) result.Add(new LipSyncCue { start = start, end = end, shape = cue.shape });
        }
        return result.Count > 0 ? result.ToArray() : null;
    }

    /// <summary>Mouth shape for one Misaki symbol: X for spaces and punctuation, B for anything unknown.</summary>
    public static char ShapeFor(char symbol)
    {
        if (Shapes.TryGetValue(symbol, out char shape)) return shape;
        if (char.IsWhiteSpace(symbol) || char.IsPunctuation(symbol) || char.IsSymbol(symbol)) return 'X';
        return 'B';
    }

    private static void Append(List<LipSyncCue> cues, float start, float end, char shape)
    {
        if (cues.Count > 0)
        {
            LipSyncCue last = cues[cues.Count - 1];
            if (last.shape == shape && last.end >= start - 0.001f)
            {
                Extend(cues, end);
                return;
            }
            // Overlap from rounding: the newer phoneme wins
            if (last.end > start)
            {
                last.end = start;
                if (last.end > last.start) cues[cues.Count - 1] = last;
                else cues.RemoveAt(cues.Count - 1);
            }
        }
        cues.Add(new LipSyncCue { start = start, end = end, shape = shape });
    }

    private static void Extend(List<LipSyncCue> cues, float end)
    {
        LipSyncCue last = cues[cues.Count - 1];
        if (end > last.end) last.end = end;
        cues[cues.Count - 1] = last;
    }

    private static Dictionary<char, char> BuildShapes(params (string symbols, char shape)[] rows)
    {
        var map = new Dictionary<char, char>();
        foreach (var (symbols, shape) in rows)
            foreach (char c in symbols)
                map[c] = shape;
        return map;
    }
}
