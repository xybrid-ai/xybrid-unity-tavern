using System;
using System.Collections.Generic;
using UnityEngine;

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
/// Everything but the AudioClip overload is thread-safe (no Unity API).
/// </summary>
public static class PhonemeLipSync
{
    private const float ShortGapSeconds = 0.08f;    // Word gaps this short keep the previous shape
    private const float LengthToleranceSeconds = 0.1f;
    private const float OnsetWindowSeconds = 0.01f;
    private const float OnsetLevel = 0.1f;          // Of the loudest window's RMS
    private const float MaxOnsetSnapSeconds = 0.5f; // Further than this, trust the timings

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
    /// before its sound, as real mouths do. <paramref name="speechOnset"/> (seconds, from
    /// <see cref="FindSpeechOnset(AudioClip)"/>; negative to skip) pulls the first spoken shape back to where
    /// the audio actually starts: Kokoro times its first phoneme late when speaking slower than 1.0.
    /// </summary>
    public static LipSyncCue[] FromPhonemes(PhonemeSpan[] spans, float clipLength, float leadSeconds = 0f,
                                            float speechOnset = -1f)
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
        // A stress mark with no vowel after it: the audio trim cut into the final vowel, whose own span was
        // dropped. The mark's time is that vowel's onset, so keep the mouth open through it.
        if (stressStart >= 0f) Append(cues, stressStart, lastEnd, 'C');

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
        if (speechOnset >= 0f) SnapFirstShapeToOnset(result, speechOnset);
        return result.Count > 0 ? result.ToArray() : null;
    }

    /// <summary>
    /// Seconds to the first audible sound: the first 10 ms window louder than a tenth of the loudest one.
    /// Negative if the clip is silent or unreadable. Main thread only.
    /// </summary>
    public static float FindSpeechOnset(AudioClip clip)
    {
        if (clip == null || clip.samples <= 0 || clip.channels <= 0) return -1f;
        var data = new float[clip.samples * clip.channels];
        if (!clip.GetData(data, 0)) return -1f;
        if (clip.channels == 1) return FindSpeechOnset(data, clip.frequency);

        var mono = new float[clip.samples];
        for (int i = 0; i < mono.Length; i++) mono[i] = data[i * clip.channels];
        return FindSpeechOnset(mono, clip.frequency);
    }

    /// <summary>Like <see cref="FindSpeechOnset(AudioClip)"/>, on mono samples. Thread-safe.</summary>
    public static float FindSpeechOnset(float[] mono, int sampleRate)
    {
        if (mono == null || sampleRate <= 0) return -1f;
        int hop = Math.Max(1, (int)(OnsetWindowSeconds * sampleRate));
        int windows = mono.Length / hop;
        if (windows == 0) return -1f;

        var rms = new float[windows];
        float loudest = 0f;
        for (int w = 0; w < windows; w++)
        {
            double sum = 0;
            for (int i = w * hop, end = i + hop; i < end; i++) sum += mono[i] * mono[i];
            rms[w] = (float)Math.Sqrt(sum / hop);
            if (rms[w] > loudest) loudest = rms[w];
        }
        if (loudest <= 0f) return -1f;

        for (int w = 0; w < windows; w++)
            if (rms[w] >= loudest * OnsetLevel) return w * hop / (float)sampleRate;
        return -1f;
    }

    private static void SnapFirstShapeToOnset(List<LipSyncCue> cues, float onset)
    {
        int first = cues.FindIndex(c => c.shape != 'X');
        if (first < 0) return;
        LipSyncCue speaking = cues[first];
        if (speaking.start <= onset || speaking.start - onset > MaxOnsetSnapSeconds) return;

        speaking.start = onset;
        cues[first] = speaking;
        // Rest cues before it now overlap: shorten or drop them
        for (int i = first - 1; i >= 0; i--)
        {
            LipSyncCue rest = cues[i];
            if (rest.start >= onset) cues.RemoveAt(i);
            else if (rest.end > onset)
            {
                rest.end = onset;
                cues[i] = rest;
            }
        }
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
