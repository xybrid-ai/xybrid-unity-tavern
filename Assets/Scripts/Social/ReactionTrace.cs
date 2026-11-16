using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Tavern.Social
{
    /// <summary>Per-listener line of a trace: what the model inferred, what the policy chose, what the animation systems did.</summary>
    public sealed class ListenerTrace
    {
        public string ListenerId;
        public string Interpretation;
        public string Requested;
        public string Outcome = "pending";
        public readonly List<string> Steps = new List<string>();
    }

    /// <summary>
    /// Everything observable about one utterance event, for the overlay, the [TableReactions] log and
    /// Logs/table-reactions.jsonl. Kept separate: model inference → policy request → execution outcome.
    /// </summary>
    public sealed class ReactionTrace
    {
        public string EventKey;
        public int SessionId;
        public int TurnId;
        public string Utterance;
        public string AddressedId;
        public List<string> Eligible = new List<string>();
        public string Mode;
        public string JudgeId;
        public string ModelLabel;
        public string JudgeStatus = "pending";
        public string Failure;
        public string RawOutput;
        public double QueueWaitMs;
        public double InferenceMs;
        public double TotalMs;
        /// <summary>Seconds from the utterance to the judgment being accepted or discarded.</summary>
        public double ArrivalSeconds;
        public string JudgmentSummary;
        public Dictionary<string, double> Probabilities = new Dictionary<string, double>();
        public readonly List<ListenerTrace> Listeners = new List<ListenerTrace>();
        public DateTime UtcTimestamp;
        /// <summary>Set once the trace has been logged, so a late result can't log it twice.</summary>
        public bool Flushed;

        public ListenerTrace Listener(string id)
        {
            foreach (ListenerTrace l in Listeners)
                if (l.ListenerId == id) return l;
            var created = new ListenerTrace { ListenerId = id };
            Listeners.Add(created);
            return created;
        }

        public void ApplyOutcome(JudgeOutcome outcome)
        {
            JudgeId = outcome.JudgeId;
            ModelLabel = outcome.ModelLabel;
            QueueWaitMs = outcome.QueueWaitMs;
            InferenceMs = outcome.InferenceMs;
            TotalMs = outcome.TotalMs;
            RawOutput = outcome.RawOutput;
            Failure = outcome.Failure;
            if (outcome.Judgment == null) return;

            JudgmentSummary = outcome.Judgment.Summary();
            Probabilities = new Dictionary<string, double>();
            foreach (var kv in outcome.Judgment.Probabilities) Probabilities[kv.Key] = kv.Value;
            foreach (var kv in outcome.Judgment.Listeners)
            {
                ListenerInterpretation i = kv.Value;
                string p = i.AttentionProbability.HasValue ? " p=" + F(i.AttentionProbability.Value) : "";
                Listener(kv.Key).Interpretation = "ref=" + i.Referent + " " + SocialLabels.ToLabel(i.Meaning) +
                                                  " attention=" + (i.AttentionWarranted ? "yes" : "no") + p;
            }
        }

        public string CompactLine()
        {
            var sb = new StringBuilder();
            sb.Append("S").Append(SessionId).Append(" T").Append(TurnId).Append(" \"").Append(Short(Utterance, 60)).Append("\"");
            sb.Append(" → [").Append(string.Join(",", Eligible)).Append("] ").Append(JudgeId ?? Mode);
            sb.Append(" ").Append(JudgeStatus);
            if (TotalMs > 0) sb.Append(" wait=").Append(Ms(QueueWaitMs)).Append(" infer=").Append(Ms(InferenceMs));
            if (JudgmentSummary != null) sb.Append(" | ").Append(JudgmentSummary);
            if (Failure != null) sb.Append(" | ").Append(Failure);
            foreach (ListenerTrace l in Listeners)
                sb.Append(" | ").Append(l.ListenerId).Append(": ").Append(l.Requested ?? "-").Append(" ⇒ ").Append(l.Outcome);
            return sb.ToString();
        }

        public JObject ToJson()
        {
            var listeners = new JArray();
            foreach (ListenerTrace l in Listeners)
                listeners.Add(new JObject
                {
                    ["id"] = l.ListenerId,
                    ["interpretation"] = l.Interpretation,
                    ["requested"] = l.Requested,
                    ["outcome"] = l.Outcome,
                    ["steps"] = new JArray(l.Steps),
                });
            var probabilities = new JObject();
            foreach (var kv in Probabilities) probabilities[kv.Key] = Math.Round(kv.Value, 4);

            return new JObject
            {
                ["utc"] = UtcTimestamp.ToString("o", CultureInfo.InvariantCulture),
                ["event"] = EventKey,
                ["session"] = SessionId,
                ["turn"] = TurnId,
                ["utterance"] = Utterance,
                ["addressee"] = AddressedId,
                ["eligible"] = new JArray(Eligible),
                ["mode"] = Mode,
                ["judge"] = JudgeId,
                ["model"] = ModelLabel,
                ["status"] = JudgeStatus,
                ["failure"] = Failure,
                ["raw"] = RawOutput != null && RawOutput.Length > 2000 ? RawOutput.Substring(0, 2000) : RawOutput,
                ["queue_wait_ms"] = Math.Round(QueueWaitMs, 1),
                ["inference_ms"] = Math.Round(InferenceMs, 1),
                ["total_ms"] = Math.Round(TotalMs, 1),
                ["arrival_s"] = Math.Round(ArrivalSeconds, 3),
                ["judgment"] = JudgmentSummary,
                ["probabilities"] = probabilities,
                ["listeners"] = listeners,
            };
        }

        public string ToJsonLine() => ToJson().ToString(Formatting.None);

        private static string Short(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        private static string Ms(double ms) => ms.ToString("0", CultureInfo.InvariantCulture) + "ms";
        private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
