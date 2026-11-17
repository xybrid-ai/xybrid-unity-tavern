using System;
using System.Collections.Generic;

namespace Tavern.Social
{
    /// <summary>What a blind-test line is, for scoring: a remark about someone at the table, or small talk.</summary>
    public enum BlindLineKind { AboutSomeone, SmallTalk }

    public sealed class BlindLine
    {
        public readonly string Text;
        public readonly BlindLineKind Kind;

        public BlindLine(string text, BlindLineKind kind)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A blind-test line needs text.", nameof(text));
            Text = text.Trim();
            Kind = kind;
        }
    }

    /// <summary>One line of a blind run: which condition (hidden from the player) and what the player answered.</summary>
    public sealed class BlindTrial
    {
        public int Index;
        public BlindLine Line;
        public string Condition;
        /// <summary>True = "someone reacted to what I said", false = no, null = not answered.</summary>
        public bool? SaidYes;
    }

    /// <summary>
    /// Perceived contingency for one condition: how often the player said "they reacted to that" after remarks about
    /// someone (hits) and after small talk (false alarms). Content-blind behaviour scores near 0 however lively it
    /// is, because it reacts as often to small talk as to remarks about people.
    /// </summary>
    public sealed class ConditionScore
    {
        public string Condition;
        public int AboutTrials;
        public int Hits;
        public int SmallTalkTrials;
        public int FalseAlarms;

        public double HitRate => AboutTrials > 0 ? (double)Hits / AboutTrials : double.NaN;
        public double FalseAlarmRate => SmallTalkTrials > 0 ? (double)FalseAlarms / SmallTalkTrials : double.NaN;

        /// <summary>Hit rate minus false-alarm rate, from -1 to 1. NaN until both kinds have an answer.</summary>
        public double Contingency => HitRate - FalseAlarmRate;
    }

    public static class BlindContingency
    {
        /// <summary>
        /// Assigns every line once. Within each kind, the lines are shuffled and dealt to the conditions in turn, so
        /// each condition gets the same number of remarks and of small talk; then the whole order is shuffled.
        /// Each kind's line count must be a multiple of the condition count.
        /// </summary>
        public static List<BlindTrial> Plan(IList<BlindLine> lines, IList<string> conditions, Random rng)
        {
            if (lines == null || lines.Count == 0) throw new ArgumentException("No lines.", nameof(lines));
            if (conditions == null || conditions.Count == 0) throw new ArgumentException("No conditions.", nameof(conditions));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            var distinct = new HashSet<string>();
            foreach (string c in conditions)
                if (string.IsNullOrEmpty(c) || !distinct.Add(c))
                    throw new ArgumentException("Conditions must be non-empty and distinct.", nameof(conditions));

            var trials = new List<BlindTrial>();
            foreach (BlindLineKind kind in new[] { BlindLineKind.AboutSomeone, BlindLineKind.SmallTalk })
            {
                var ofKind = new List<BlindLine>();
                foreach (BlindLine l in lines)
                    if (l != null && l.Kind == kind) ofKind.Add(l);
                if (ofKind.Count % conditions.Count != 0)
                    throw new ArgumentException($"{ofKind.Count} {kind} lines can't be split evenly over {conditions.Count} conditions.");

                Shuffle(ofKind, rng);
                int offset = rng.Next(conditions.Count);
                for (int i = 0; i < ofKind.Count; i++)
                    trials.Add(new BlindTrial { Line = ofKind[i], Condition = conditions[(i + offset) % conditions.Count] });
            }

            Shuffle(trials, rng);
            for (int i = 0; i < trials.Count; i++) trials[i].Index = i + 1;
            return trials;
        }

        /// <summary>Scores answered trials per condition, in <paramref name="conditionOrder"/> (others appended).</summary>
        public static List<ConditionScore> Score(IEnumerable<BlindTrial> trials, IList<string> conditionOrder = null)
        {
            var byCondition = new Dictionary<string, ConditionScore>();
            var order = new List<string>();
            if (conditionOrder != null)
                foreach (string c in conditionOrder)
                    if (c != null && !byCondition.ContainsKey(c))
                    {
                        byCondition[c] = new ConditionScore { Condition = c };
                        order.Add(c);
                    }

            foreach (BlindTrial t in trials ?? Array.Empty<BlindTrial>())
            {
                if (t?.Line == null || t.Condition == null || t.SaidYes == null) continue;
                if (!byCondition.TryGetValue(t.Condition, out ConditionScore s))
                {
                    s = byCondition[t.Condition] = new ConditionScore { Condition = t.Condition };
                    order.Add(t.Condition);
                }
                if (t.Line.Kind == BlindLineKind.AboutSomeone)
                {
                    s.AboutTrials++;
                    if (t.SaidYes.Value) s.Hits++;
                }
                else
                {
                    s.SmallTalkTrials++;
                    if (t.SaidYes.Value) s.FalseAlarms++;
                }
            }

            var result = new List<ConditionScore>();
            foreach (string c in order) result.Add(byCondition[c]);
            return result;
        }

        private static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
