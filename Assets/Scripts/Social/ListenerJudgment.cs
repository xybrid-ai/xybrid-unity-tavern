using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Tavern.Social
{
    /// <summary>How the utterance treats its referent. A small closed vocabulary.</summary>
    public enum SocialMeaning
    {
        Praise,
        Criticism,
        Concern,
        Neutral,
        Unclear
    }

    public static class SocialLabels
    {
        public static readonly string[] MeaningLabels = { "praise", "criticism", "concern", "neutral", "unclear" };

        public static string ToLabel(SocialMeaning meaning) => MeaningLabels[(int)meaning];

        /// <summary>Exact, lower-case match only. Anything else is rejected, not guessed.</summary>
        public static bool TryParseMeaning(string label, out SocialMeaning meaning)
        {
            for (int i = 0; i < MeaningLabels.Length; i++)
            {
                if (MeaningLabels[i] == label)
                {
                    meaning = (SocialMeaning)i;
                    return true;
                }
            }
            meaning = SocialMeaning.Unclear;
            return false;
        }
    }

    /// <summary>
    /// What a judge returned before validation: labels as strings, attention as yes/no per listener,
    /// and optional model-native probabilities kept for diagnostics only.
    /// </summary>
    public sealed class RawJudgment
    {
        public string Referent;
        public string Meaning;
        public readonly Dictionary<string, bool?> Attention = new Dictionary<string, bool?>();
        /// <summary>Diagnostics, e.g. "referent" → p(choice), "attention_shara" → p(yes). Never calibrated.</summary>
        public readonly Dictionary<string, double> Probabilities = new Dictionary<string, double>();
    }

    /// <summary>Validated interpretation for one listener.</summary>
    public sealed class ListenerInterpretation
    {
        public string ListenerId { get; }
        /// <summary>Allowed member id, <see cref="SocialIds.None"/> or <see cref="SocialIds.Unclear"/>.</summary>
        public string Referent { get; }
        public SocialMeaning Meaning { get; }
        /// <summary>Whether the judge thinks this listener has a reason to react at all.</summary>
        public bool AttentionWarranted { get; }
        /// <summary>d1's p(yes) for the attention question; null for judges without one.</summary>
        public double? AttentionProbability { get; }

        public ListenerInterpretation(string listenerId, string referent, SocialMeaning meaning, bool attention, double? attentionProbability)
        {
            ListenerId = listenerId;
            Referent = referent;
            Meaning = meaning;
            AttentionWarranted = attention;
            AttentionProbability = attentionProbability;
        }
    }

    /// <summary>A validated judgment for every eligible listener of one event.</summary>
    public sealed class EventJudgment
    {
        public string Referent { get; }
        public SocialMeaning Meaning { get; }
        public IReadOnlyDictionary<string, ListenerInterpretation> Listeners { get; }
        public IReadOnlyDictionary<string, double> Probabilities { get; }

        public EventJudgment(string referent, SocialMeaning meaning,
            IDictionary<string, ListenerInterpretation> listeners, IDictionary<string, double> probabilities)
        {
            Referent = referent;
            Meaning = meaning;
            Listeners = new ReadOnlyDictionary<string, ListenerInterpretation>(new Dictionary<string, ListenerInterpretation>(listeners));
            Probabilities = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(probabilities ?? new Dictionary<string, double>()));
        }

        public string Summary()
        {
            var parts = new List<string>();
            foreach (var kv in Listeners)
                parts.Add(kv.Key + "=" + (kv.Value.AttentionWarranted ? "attend" : "ignore"));
            return "ref=" + Referent + " meaning=" + SocialLabels.ToLabel(Meaning) + " " + string.Join(" ", parts);
        }
    }

    /// <summary>
    /// The only path from judge output to <see cref="EventJudgment"/>. Rejects unknown ids, labels outside
    /// the vocabulary, missing or extra listeners and non-finite numbers. Rejection means no reaction.
    /// </summary>
    public static class JudgmentValidator
    {
        public static bool TryValidate(RawJudgment raw, IReadOnlyCollection<string> allowedMemberIds,
            IReadOnlyList<string> eligibleListenerIds, out EventJudgment judgment, out string error)
        {
            judgment = null;
            error = null;
            if (raw == null) { error = "no judgment"; return false; }
            if (allowedMemberIds == null || eligibleListenerIds == null) { error = "missing context"; return false; }

            string referent = raw.Referent;
            bool referentOk = referent == SocialIds.None || referent == SocialIds.Unclear ||
                              (referent != null && !SocialIds.IsReserved(referent) && Contains(allowedMemberIds, referent));
            if (!referentOk) { error = "unknown referent '" + referent + "'"; return false; }

            if (!SocialLabels.TryParseMeaning(raw.Meaning, out SocialMeaning meaning))
            {
                error = "invalid meaning '" + raw.Meaning + "'";
                return false;
            }

            foreach (string key in raw.Attention.Keys)
            {
                if (!Contains(eligibleListenerIds, key)) { error = "listener '" + key + "' is outside the supplied set"; return false; }
            }

            foreach (var kv in raw.Probabilities)
            {
                if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value) || kv.Value < 0.0 || kv.Value > 1.0)
                {
                    error = "invalid probability for '" + kv.Key + "'";
                    return false;
                }
            }

            var listeners = new Dictionary<string, ListenerInterpretation>();
            foreach (string id in eligibleListenerIds)
            {
                if (!raw.Attention.TryGetValue(id, out bool? attention) || !attention.HasValue)
                {
                    error = "missing attention for '" + id + "'";
                    return false;
                }
                double? p = raw.Probabilities.TryGetValue("attention_" + id, out double prob) ? prob : (double?)null;
                listeners[id] = new ListenerInterpretation(id, referent, meaning, attention.Value, p);
            }

            judgment = new EventJudgment(referent, meaning, listeners, raw.Probabilities);
            return true;
        }

        private static bool Contains(IEnumerable<string> items, string value)
        {
            foreach (string item in items)
                if (string.Equals(item, value, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
