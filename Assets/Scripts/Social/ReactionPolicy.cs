using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tavern.Social
{
    /// <summary>
    /// What the policy asked one listener to do for one event. <see cref="IsNone"/> plans are kept so the
    /// trace shows why nothing happened. Target ids are member ids or <see cref="SocialIds.Player"/>.
    /// </summary>
    public sealed class ReactionPlan
    {
        public string ListenerId;
        public bool IsNone;
        public string Reason;
        public string RuleKey;
        public string OptionKey;
        public double DelaySeconds;
        public GazeKind Gaze;
        public string GazeTargetId;
        public double GazeSeconds;
        public ReactionGesture Gesture;
        public string GestureTargetId;
        public string Face;
        public double FaceIntensity;
        public double FaceHoldSeconds;

        public static ReactionPlan None(string listener, string reason)
            => new ReactionPlan { ListenerId = listener, IsNone = true, Reason = reason };

        public string Describe()
        {
            if (IsNone) return "none (" + Reason + ")";
            var parts = new List<string> { "+" + DelaySeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s" };
            if (Gaze != GazeKind.None) parts.Add("look " + GazeTargetId + " " + GazeSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            if (Gesture != ReactionGesture.None) parts.Add(Gesture.ToString().ToLowerInvariant() + (GestureTargetId != null ? "→" + GestureTargetId : ""));
            if (Face != null) parts.Add(Face + " " + FaceIntensity.ToString("0.00", CultureInfo.InvariantCulture));
            return string.Join(", ", parts) + " [" + OptionKey + "]";
        }
    }

    /// <summary>
    /// Authored, inspectable mapping from a validated judgment to at most one brief reaction per listener.
    /// "No reaction" is the default. Enforces cooldowns, a skip chance, staggered starts and no immediate
    /// repeats of the same option. The model never chooses an animation; it only supplies labels.
    /// </summary>
    public sealed class ReactionPolicy
    {
        private readonly TableProfile _table;
        private readonly Random _rng;
        private readonly Dictionary<string, double> _lastReaction = new Dictionary<string, double>();
        private readonly Dictionary<string, string> _lastOption = new Dictionary<string, string>();

        /// <summary>Overrides the profile's skip chance (tests and evaluation use 0 for determinism).</summary>
        public double? SkipChanceOverride { get; set; }

        public ReactionPolicy(TableProfile table, Random rng)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _rng = rng ?? new Random();
        }

        public PolicyConfig Config => _table.Policy;

        /// <summary>self: about the listener; addressee: about the person being spoken to; peer: another member; none.</summary>
        public static string RelationOf(string listenerId, string referent, string addressedId, TableProfile table)
        {
            if (referent == listenerId) return "self";
            if (referent == SocialIds.None || referent == SocialIds.Unclear || table.Find(referent) == null) return "none";
            if (referent == addressedId) return "addressee";
            return "peer";
        }

        public void ResetHistory()
        {
            _lastReaction.Clear();
            _lastOption.Clear();
        }

        public List<ReactionPlan> Plan(UtteranceEvent e, EventJudgment judgment, double now)
        {
            var plans = new List<ReactionPlan>();
            var reacting = new List<ReactionPlan>();
            PolicyConfig config = _table.Policy;

            foreach (string listener in e.EligibleListenerIds)
            {
                if (!judgment.Listeners.TryGetValue(listener, out ListenerInterpretation interpretation))
                {
                    plans.Add(ReactionPlan.None(listener, "no interpretation"));
                    continue;
                }
                if (!interpretation.AttentionWarranted)
                {
                    plans.Add(ReactionPlan.None(listener, "attention=no"));
                    continue;
                }

                string relation = RelationOf(listener, interpretation.Referent, e.AddressedId, _table);
                string meaning = SocialLabels.ToLabel(interpretation.Meaning);
                PolicyRule rule = FindRule(listener, relation, meaning);
                if (rule == null)
                {
                    plans.Add(ReactionPlan.None(listener, "no rule for " + relation + "/" + meaning));
                    continue;
                }

                if (_lastReaction.TryGetValue(listener, out double last) && now - last < config.CooldownSeconds)
                {
                    plans.Add(ReactionPlan.None(listener, "cooldown"));
                    continue;
                }
                double skipChance = SkipChanceOverride ?? (relation == "self" ? config.SelfSkipChance : config.SkipChance);
                if (skipChance > 0 && _rng.NextDouble() < skipChance)
                {
                    plans.Add(ReactionPlan.None(listener, "skip chance"));
                    continue;
                }

                ReactionOption option = PickOption(listener, rule);
                if (option.IsNothing)
                {
                    _lastOption[listener] = option.Key;
                    plans.Add(ReactionPlan.None(listener, "authored nothing (" + rule.Key + ")"));
                    continue;
                }

                var plan = new ReactionPlan
                {
                    ListenerId = listener,
                    RuleKey = rule.Key,
                    OptionKey = option.Key,
                    Gaze = option.Gaze,
                    Gesture = option.Gesture,
                    Face = option.Face,
                    FaceIntensity = option.Intensity,
                    GazeSeconds = config.GazeSeconds.Sample(_rng),
                    FaceHoldSeconds = config.FaceHoldSeconds.Sample(_rng),
                };
                plan.GazeTargetId = ResolveTarget(plan.Gaze, listener, interpretation.Referent, e);
                if (plan.GazeTargetId == null) plan.Gaze = GazeKind.None;
                if (plan.Gesture == ReactionGesture.GlanceToReferent)
                {
                    plan.GestureTargetId = ResolveTarget(GazeKind.Referent, listener, interpretation.Referent, e);
                    if (plan.GestureTargetId == null) plan.Gesture = ReactionGesture.None;
                }
                if (plan.Gaze == GazeKind.None && plan.Gesture == ReactionGesture.None && plan.Face == null)
                {
                    plans.Add(ReactionPlan.None(listener, "no valid target (" + rule.Key + ")"));
                    continue;
                }

                plan.Reason = relation + "/" + meaning;
                _lastReaction[listener] = now;
                _lastOption[listener] = option.Key;
                plans.Add(plan);
                reacting.Add(plan);
            }

            // Stagger: listeners start one after another in a random order, never in unison
            Shuffle(reacting);
            double t = 0;
            for (int i = 0; i < reacting.Count; i++)
            {
                t = i == 0 ? config.FirstDelay.Sample(_rng) : t + Math.Max(config.MinStaggerSeconds, config.Stagger.Sample(_rng));
                reacting[i].DelaySeconds = t;
            }
            return plans;
        }

        /// <summary>
        /// Deterministic check used for scoring: attention warranted, a rule matches, and at least one of its
        /// options does something with a valid target. Ignores cooldown, skip chance and option weights.
        /// </summary>
        public bool CouldReact(UtteranceEvent e, EventJudgment judgment, string listener, out string ruleKey)
        {
            ruleKey = null;
            if (!judgment.Listeners.TryGetValue(listener, out ListenerInterpretation interpretation)) return false;
            if (!interpretation.AttentionWarranted) return false;
            string relation = RelationOf(listener, interpretation.Referent, e.AddressedId, _table);
            PolicyRule rule = FindRule(listener, relation, SocialLabels.ToLabel(interpretation.Meaning));
            if (rule == null) return false;
            ruleKey = rule.Key;
            foreach (ReactionOption o in rule.Options)
            {
                if (o.Face != null) return true;
                if (o.Gaze != GazeKind.None && ResolveTarget(o.Gaze, listener, interpretation.Referent, e) != null) return true;
                if (o.Gesture == ReactionGesture.Nod || o.Gesture == ReactionGesture.Shake ||
                    o.Gesture == ReactionGesture.GlanceAway || o.Gesture == ReactionGesture.Laugh) return true;
                if (o.Gesture == ReactionGesture.GlanceToReferent &&
                    ResolveTarget(GazeKind.Referent, listener, interpretation.Referent, e) != null) return true;
            }
            return false;
        }

        private PolicyRule FindRule(string listener, string relation, string meaning)
        {
            PolicyRule best = null;
            int bestScore = -1;
            foreach (PolicyRule rule in _table.Policy.Rules)
            {
                if (rule.Listener != "*" && rule.Listener != listener) continue;
                if (rule.Relation != "*" && rule.Relation != relation) continue;
                if (rule.Meaning != "*" && rule.Meaning != meaning) continue;
                int score = (rule.Listener != "*" ? 4 : 0) + (rule.Relation != "*" ? 2 : 0) + (rule.Meaning != "*" ? 1 : 0);
                if (score > bestScore)
                {
                    best = rule;
                    bestScore = score;
                }
            }
            return best;
        }

        private ReactionOption PickOption(string listener, PolicyRule rule)
        {
            _lastOption.TryGetValue(listener, out string previous);
            double total = 0;
            foreach (ReactionOption o in rule.Options)
                if (rule.Options.Count == 1 || o.Key != previous) total += o.Weight;

            double roll = _rng.NextDouble() * total;
            ReactionOption chosen = null;
            foreach (ReactionOption o in rule.Options)
            {
                if (rule.Options.Count > 1 && o.Key == previous) continue;
                chosen = o;
                roll -= o.Weight;
                if (roll < 0) break;
            }
            return chosen ?? rule.Options[0];
        }

        private string ResolveTarget(GazeKind gaze, string listener, string referent, UtteranceEvent e)
        {
            switch (gaze)
            {
                case GazeKind.Player:
                    return SocialIds.Player;
                case GazeKind.Addressee:
                    return e.AddressedId != listener ? e.AddressedId : null;
                case GazeKind.Referent:
                    return referent != listener && _table.Find(referent) != null ? referent : null;
                default:
                    return null;
            }
        }

        private void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }
    }
}
