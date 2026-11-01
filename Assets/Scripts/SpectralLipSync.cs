using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// In-engine lip-sync: estimates Rhubarb-style mouth shapes (A-H, X) from a speech clip's spectrum,
/// so NPC mouths form vowels and consonants without the Rhubarb CLI (which ships x86_64-only and
/// needs Rosetta on Apple silicon). Cheap enough for the main thread: one 512-point FFT per 10 ms hop.
/// This is a heuristic, not phoneme recognition: loudness separates speech from rest, the spectral
/// centroid of the voice band (ranked within the clip, so any voice works) separates open (AA), mid
/// (EH), rounded (AO) and puckered (OO) vowels,
/// and high-band energy marks teeth consonants (S, T, EE). Short silences inside speech become
/// closed lips (M, B, P).
/// </summary>
public static class SpectralLipSync
{
    private const float HopSeconds = 0.01f;
    private const int WindowSize = 512;                 // ~21 ms at 24 kHz
    private const float VoiceBandMin = 80f, VoiceBandMax = 3000f, HighBandMin = 3000f, HighBandMax = 8000f;
    private const float SpeechGate = 0.08f;             // Normalised loudness below this is silence
    private const float ClosureMaxSeconds = 0.07f;      // Silences this short inside speech read as M/B/P
    private const float MinCueSeconds = 0.04f;

    /// <summary>Analyses a readable clip (e.g. one made by TTSAudioPlayer.PCMToAudioClip). Main thread only.</summary>
    public static LipSyncCue[] Analyze(AudioClip clip)
    {
        if (clip == null || clip.samples <= 0 || clip.channels <= 0) return null;

        var data = new float[clip.samples * clip.channels];
        if (!clip.GetData(data, 0)) return null;

        float[] mono = data;
        if (clip.channels > 1)
        {
            mono = new float[clip.samples];
            for (int i = 0; i < clip.samples; i++)
            {
                float sum = 0f;
                for (int c = 0; c < clip.channels; c++) sum += data[i * clip.channels + c];
                mono[i] = sum / clip.channels;
            }
        }
        return Analyze(mono, clip.frequency);
    }

    /// <summary>Analyses mono samples. Thread-safe (no Unity API), so it can also run on a worker.</summary>
    public static LipSyncCue[] Analyze(float[] mono, int sampleRate)
    {
        if (mono == null || mono.Length == 0 || sampleRate <= 0) return null;

        int hop = Mathf.Max(1, Mathf.RoundToInt(HopSeconds * sampleRate));
        int frames = Mathf.CeilToInt(mono.Length / (float)hop);
        var loudness = new float[frames];
        var centroid = new float[frames];
        var highRatio = new float[frames];

        var re = new float[WindowSize];
        var im = new float[WindowSize];
        var window = new float[WindowSize];
        for (int i = 0; i < WindowSize; i++)
            window[i] = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * i / (WindowSize - 1));

        float binHz = sampleRate / (float)WindowSize;
        int voiceLo = Mathf.Max(1, Mathf.FloorToInt(VoiceBandMin / binHz));
        int voiceHi = Mathf.Min(WindowSize / 2, Mathf.CeilToInt(VoiceBandMax / binHz));
        int highLo = Mathf.Min(WindowSize / 2, Mathf.FloorToInt(HighBandMin / binHz));
        int highHi = Mathf.Min(WindowSize / 2, Mathf.CeilToInt(HighBandMax / binHz));

        for (int f = 0; f < frames; f++)
        {
            int start = f * hop + hop / 2 - WindowSize / 2;
            double energy = 0;
            for (int i = 0; i < WindowSize; i++)
            {
                int s = start + i;
                float v = s >= 0 && s < mono.Length ? mono[s] : 0f;
                energy += v * v;
                re[i] = v * window[i];
                im[i] = 0f;
            }
            loudness[f] = Mathf.Sqrt((float)(energy / WindowSize));

            Fft(re, im);

            double voicePower = 0, weighted = 0, highPower = 0;
            for (int k = voiceLo; k <= voiceHi; k++)
            {
                double p = re[k] * re[k] + im[k] * im[k];
                voicePower += p;
                weighted += p * k * binHz;
            }
            for (int k = highLo; k <= highHi; k++)
                highPower += re[k] * re[k] + im[k] * im[k];

            centroid[f] = voicePower > 1e-12 ? (float)(weighted / voicePower) : 0f;
            highRatio[f] = voicePower + highPower > 1e-12 ? (float)(highPower / (voicePower + highPower)) : 0f;
        }

        NormaliseLoudness(loudness);
        var thresholds = VowelThresholds(loudness, centroid, highRatio);

        var shapes = new char[frames];
        for (int f = 0; f < frames; f++)
            shapes[f] = Classify(loudness[f], centroid[f], highRatio[f], thresholds);

        MarkClosures(shapes, hop / (float)sampleRate);
        MedianSmooth(shapes);
        return ToCues(shapes, hop / (float)sampleRate);
    }

    // ─── Classification ──────────────────────────────────────────────────

    // Vowel-ish frames are ranked by where the voice energy sits, relative to this clip: absolute Hz
    // thresholds don't transfer between voices (a strong low fundamental drags every centroid down).
    // The cumulative shares roughly follow how often each mouth shape occurs in English speech.
    private struct Thresholds { public float pucker, rounded, open, spread; }

    private static readonly float[] VowelShares = { 0.10f, 0.28f, 0.55f, 0.80f };   // F | E | D/C | C | B

    private static Thresholds VowelThresholds(float[] loudness, float[] centroid, float[] highRatio)
    {
        var voiced = new List<float>(centroid.Length);
        for (int f = 0; f < centroid.Length; f++)
            if (loudness[f] >= SpeechGate && highRatio[f] <= HissRatio) voiced.Add(centroid[f]);

        if (voiced.Count < 8)   // Too little speech to rank: fall back to typical adult-voice values
            return new Thresholds { pucker = 550f, rounded = 800f, open = 1150f, spread = 1600f };

        voiced.Sort();
        float At(float share) => voiced[Mathf.Clamp(Mathf.RoundToInt(share * (voiced.Count - 1)), 0, voiced.Count - 1)];
        return new Thresholds { pucker = At(VowelShares[0]), rounded = At(VowelShares[1]), open = At(VowelShares[2]), spread = At(VowelShares[3]) };
    }

    private const float HissRatio = 0.45f;

    private static char Classify(float loudness, float centroidHz, float highRatio, Thresholds t)
    {
        if (loudness < SpeechGate) return 'X';

        // Hiss-dominated frames: S, Z, T, CH, and the teeth-together EE
        if (highRatio > HissRatio) return 'B';

        if (centroidHz > t.spread) return 'B';                          // EE / IH: lips spread, teeth close
        if (centroidHz > t.open) return 'C';                            // EH / AE: open
        if (centroidHz > t.rounded) return loudness > 0.4f ? 'D' : 'C';    // AA: wide open when loud
        if (centroidHz > t.pucker) return 'E';                          // AO / ER: slightly rounded
        return 'F';                                                     // OO / OW / W: puckered
    }

    // Normalise to the 95th percentile so quiet and loud voices both use the full range
    private static void NormaliseLoudness(float[] loudness)
    {
        var sorted = (float[])loudness.Clone();
        Array.Sort(sorted);
        float reference = Mathf.Max(sorted[Mathf.Clamp(Mathf.RoundToInt(sorted.Length * 0.95f), 0, sorted.Length - 1)], 1e-4f);
        for (int i = 0; i < loudness.Length; i++)
            loudness[i] = Mathf.Clamp01(loudness[i] / reference);
    }

    // Short gaps between voiced frames are lip closures (M, B, P) rather than rest
    private static void MarkClosures(char[] shapes, float frameSeconds)
    {
        int maxFrames = Mathf.Max(1, Mathf.RoundToInt(ClosureMaxSeconds / frameSeconds));
        int i = 0;
        while (i < shapes.Length)
        {
            if (shapes[i] != 'X') { i++; continue; }
            int end = i;
            while (end < shapes.Length && shapes[end] == 'X') end++;
            bool inside = i > 0 && end < shapes.Length;
            if (inside && end - i <= maxFrames)
                for (int k = i; k < end; k++) shapes[k] = 'A';
            i = end;
        }
    }

    // 3-frame median on shape labels removes single-frame flicker
    private static void MedianSmooth(char[] shapes)
    {
        if (shapes.Length < 3) return;
        var copy = (char[])shapes.Clone();
        for (int i = 1; i < shapes.Length - 1; i++)
        {
            char a = copy[i - 1], b = copy[i], c = copy[i + 1];
            if (a == c && b != a) shapes[i] = a;
        }
    }

    private static LipSyncCue[] ToCues(char[] shapes, float frameSeconds)
    {
        var cues = new List<LipSyncCue>();
        int i = 0;
        while (i < shapes.Length)
        {
            int end = i;
            while (end < shapes.Length && shapes[end] == shapes[i]) end++;
            float start = i * frameSeconds, stop = end * frameSeconds;

            // Fold blips shorter than MinCueSeconds into the previous cue
            if (stop - start < MinCueSeconds && cues.Count > 0)
            {
                var last = cues[cues.Count - 1];
                last.end = stop;
                cues[cues.Count - 1] = last;
            }
            else
            {
                cues.Add(new LipSyncCue { start = start, end = stop, shape = shapes[i] });
            }
            i = end;
        }
        return cues.ToArray();
    }

    // ─── FFT ─────────────────────────────────────────────────────────────

    // In-place iterative radix-2 FFT; length must be a power of two
    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            float angle = -2f * Mathf.PI / len;
            float wRe = Mathf.Cos(angle), wIm = Mathf.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                float curRe = 1f, curIm = 0f;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    float tRe = re[b] * curRe - im[b] * curIm;
                    float tIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - tRe; im[b] = im[a] - tIm;
                    re[a] += tRe; im[a] += tIm;
                    float nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
