using System;
using System.Collections.Generic;

/// <summary>
/// Guesses the emotion an NPC line is spoken with, so the face can react while it plays
/// ("Ha! Splendid!" -> Delighted, "Beware the old mill." -> Worried, "Fools, the lot of them." -> Grumpy).
/// A deliberately small, predictable heuristic: a keyword table over lowercase words, a few phrases,
/// '!' to boost intensity, and a negation check ("not afraid" doesn't count as fear).
/// </summary>
public static class LineEmotion
{
    private const float MinIntensity = 0.3f;
    private const float MaxIntensity = 0.9f;
    private const float QuestionIntensity = 0.3f;
    private const float StrongThreshold = 0.8f;   // group intensity at which e.g. Grumpy becomes Angry

    // Emotions that read as variants of the same feeling are scored together, then split by strength
    private enum Group { Anger, Fear, Sorrow, Joy, Suspicion, Surprise, Smugness, Count }

    private struct Cue
    {
        public Group group;
        public FaceEmotion emotion;
        public float weight;
        public int tier;   // 0 mild, 1 normal, 2 strong: the group shows the emotion of its highest tier hit

        public Cue(Group group, FaceEmotion emotion, float weight, int tier)
        {
            this.group = group;
            this.emotion = emotion;
            this.weight = weight;
            this.tier = tier;
        }
    }

    private static readonly Dictionary<string, Cue> Words = BuildWords();

    private static readonly KeyValuePair<string, Cue>[] Phrases =
    {
        new KeyValuePair<string, Cue>("of course", new Cue(Group.Smugness, FaceEmotion.Smug, 1f, 1)),
        new KeyValuePair<string, Cue>("how dare", new Cue(Group.Anger, FaceEmotion.Angry, 1.5f, 2)),
        new KeyValuePair<string, Cue>("thank you", new Cue(Group.Joy, FaceEmotion.Content, 0.8f, 0)),
        new KeyValuePair<string, Cue>("by the gods", new Cue(Group.Surprise, FaceEmotion.Surprised, 1f, 1)),
        new KeyValuePair<string, Cue>("really?", new Cue(Group.Surprise, FaceEmotion.Surprised, 1f, 1)),
        new KeyValuePair<string, Cue>("really!", new Cue(Group.Surprise, FaceEmotion.Surprised, 1f, 1)),
        new KeyValuePair<string, Cue>("truly?", new Cue(Group.Surprise, FaceEmotion.Surprised, 1f, 1)),
        new KeyValuePair<string, Cue>("what!", new Cue(Group.Surprise, FaceEmotion.Surprised, 1.2f, 1)),
        new KeyValuePair<string, Cue>("what?!", new Cue(Group.Surprise, FaceEmotion.Surprised, 1.2f, 1)),
        new KeyValuePair<string, Cue>("oh!", new Cue(Group.Surprise, FaceEmotion.Surprised, 0.8f, 1)),
    };

    private static readonly HashSet<string> Negations = new HashSet<string>
    {
        "not", "never", "don't", "dont", "isn't", "isnt", "wasn't", "wasnt", "nothing", "without", "hardly", "no"
    };

    // Strongest member of each group, used once its intensity passes StrongThreshold
    private static FaceEmotion StrongEmotionOf(Group group, FaceEmotion current)
    {
        switch (group)
        {
            case Group.Joy: return FaceEmotion.Delighted;
            case Group.Anger: return FaceEmotion.Angry;
            case Group.Fear: return FaceEmotion.Scared;
            default: return current;
        }
    }

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// Analyses a spoken line. Returns false (Neutral, 0) when nothing matched; otherwise the emotion
    /// and an intensity in 0.3..0.9. A question with no other cue reads as mild surprise (0.3).
    /// </summary>
    public static bool TryAnalyze(string text, out FaceEmotion emotion, out float intensity)
    {
        emotion = FaceEmotion.Neutral;
        intensity = 0f;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string lower = text.ToLowerInvariant().Replace('’', '\'');

        int groupCount = (int)Group.Count;
        var scores = new float[groupCount];
        var bestTier = new int[groupCount];
        var bestEmotion = new FaceEmotion[groupCount];
        for (int g = 0; g < groupCount; g++) bestTier[g] = -1;

        // Phrases first (they include punctuation the word pass discards)
        foreach (var phrase in Phrases)
        {
            if (ContainsPhrase(lower, phrase.Key))
                AddHit(phrase.Value, scores, bestTier, bestEmotion);
        }

        // Word pass: letters and apostrophes form words
        string previousWord = null;
        int i = 0;
        while (i < lower.Length)
        {
            if (!IsWordChar(lower[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < lower.Length && IsWordChar(lower[i])) i++;
            string word = lower.Substring(start, i - start).Trim('\'');
            if (word.Length == 0) continue;

            if (Words.TryGetValue(word, out Cue cue) && !IsNegated(previousWord) && !IsTitle(word, text, start))
                AddHit(cue, scores, bestTier, bestEmotion);

            previousWord = word;
        }

        int exclamations = Math.Min(Count(text, '!'), 3);

        // Pick the highest-scoring group (ties go to the earlier, more expressive group)
        int best = -1;
        for (int g = 0; g < groupCount; g++)
        {
            if (scores[g] <= 0f) continue;
            if (best < 0 || scores[g] > scores[best]) best = g;
        }

        if (best < 0)
        {
            if (text.IndexOf('?') < 0) return false;
            emotion = FaceEmotion.Surprised;
            intensity = QuestionIntensity;
            return true;
        }

        float level = 0.35f + 0.15f * scores[best] + 0.1f * exclamations;
        intensity = Clamp(level, MinIntensity, MaxIntensity);

        emotion = bestEmotion[best];
        if (intensity >= StrongThreshold)
            emotion = StrongEmotionOf((Group)best, emotion);
        return true;
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static void AddHit(Cue cue, float[] scores, int[] bestTier, FaceEmotion[] bestEmotion)
    {
        int g = (int)cue.group;
        scores[g] += cue.weight;
        if (cue.tier > bestTier[g])
        {
            bestTier[g] = cue.tier;
            bestEmotion[g] = cue.emotion;
        }
    }

    private static bool IsWordChar(char c) => char.IsLetter(c) || c == '\'';

    // Phrase must start a word: "what!" matches "What!" but not "somewhat!"
    private static bool ContainsPhrase(string lower, string phrase)
    {
        int at = lower.IndexOf(phrase, StringComparison.Ordinal);
        while (at >= 0)
        {
            if (at == 0 || !char.IsLetter(lower[at - 1])) return true;
            at = lower.IndexOf(phrase, at + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static bool IsNegated(string previousWord) => previousWord != null && Negations.Contains(previousWord);

    // "Miss Greta" is a title, not longing
    private static bool IsTitle(string word, string original, int start) =>
        word == "miss" && start < original.Length && char.IsUpper(original[start]);

    private static int Count(string text, char c)
    {
        int n = 0;
        foreach (char ch in text)
            if (ch == c) n++;
        return n;
    }

    private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

    private static Dictionary<string, Cue> BuildWords()
    {
        var words = new Dictionary<string, Cue>();

        void Add(Group group, FaceEmotion emotion, float weight, int tier, params string[] keys)
        {
            foreach (string key in keys)
                words[key] = new Cue(group, emotion, weight, tier);
        }

        // Joy
        Add(Group.Joy, FaceEmotion.Content, 0.8f, 0,
            "welcome", "pleasure", "thanks", "cozy", "cosy", "kindly");
        Add(Group.Joy, FaceEmotion.Happy, 1f, 1,
            "laugh", "laughs", "laughed", "laughing", "ha", "hah", "heh", "glad", "lovely", "cheers",
            "merry", "joy", "excellent", "fantastic", "delightful", "grand");
        Add(Group.Joy, FaceEmotion.Delighted, 1.2f, 2,
            "haha", "hahaha", "wonderful", "splendid", "marvelous", "marvellous", "delighted", "thrilled");

        // Anger
        Add(Group.Anger, FaceEmotion.Grumpy, 1f, 1,
            "fool", "fools", "foolish", "idiot", "idiots", "bah", "hmph", "humph", "pah", "nonsense",
            "blast", "blasted", "curse", "cursed", "rubbish", "rascal", "rascals", "whelp");
        Add(Group.Anger, FaceEmotion.Angry, 1.2f, 2,
            "angry", "furious", "rage", "damn", "damned", "outrage", "outrageous");

        // Fear
        Add(Group.Fear, FaceEmotion.Worried, 1f, 1,
            "danger", "dangerous", "beware", "worry", "worried", "worries", "trouble", "fear", "fears",
            "uneasy", "perilous");
        Add(Group.Fear, FaceEmotion.Scared, 1.2f, 2,
            "afraid", "scared", "terrified", "frightened", "terror", "horror", "horrible");

        // Sorrow
        Add(Group.Sorrow, FaceEmotion.Sad, 1f, 1,
            "sorry", "alas", "sadly", "sad", "lost", "miss", "missed", "gone", "grief", "mourn", "mourning",
            "dead", "died", "weep", "wept", "tears", "lonely", "regret");

        // Suspicion
        Add(Group.Suspicion, FaceEmotion.Suspicious, 1f, 1,
            "strange", "odd", "secret", "secrets", "secretly", "careful", "hush", "whisper", "whispers",
            "whispered", "shadows", "suspicious", "mysterious");

        // Smugness
        Add(Group.Smugness, FaceEmotion.Smug, 1f, 1,
            "naturally", "obviously");

        // Surprise
        Add(Group.Surprise, FaceEmotion.Surprised, 1f, 1,
            "surprising", "surprised", "surprise", "wow", "goodness", "astonishing", "unbelievable");

        return words;
    }
}
