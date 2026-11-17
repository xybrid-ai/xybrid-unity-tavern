using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Tavern.Social
{
    public sealed class ScenarioCase
    {
        public string Id;
        public string Split;
        public string Category;
        public string Addressee;
        public List<string> Listeners = new List<string>();
        public List<DialogueLine> History = new List<DialogueLine>();
        public string Utterance;
        public List<string> ExpectReferent = new List<string>();
        public List<string> ExpectMeaning = new List<string>();
        /// <summary>listener → "yes" | "no" | "any".</summary>
        public Dictionary<string, string> ExpectReact = new Dictionary<string, string>();
    }

    /// <summary>Fixed utterances + context snapshots with pre-registered acceptable outcomes.</summary>
    public sealed class ScenarioSet
    {
        public int Version;
        public List<ScenarioCase> Cases = new List<ScenarioCase>();

        public static ScenarioSet Parse(string json)
        {
            JObject root = JObject.Parse(json);
            var set = new ScenarioSet { Version = (int?)root["version"] ?? 0 };
            var defaults = root["defaultHistory"] as JObject ?? new JObject();

            foreach (JToken c in (JArray)root["cases"])
            {
                var sc = new ScenarioCase
                {
                    Id = (string)c["id"],
                    Split = (string)c["split"] ?? "test",
                    Category = (string)c["category"] ?? "",
                    Addressee = (string)c["addressee"],
                    Utterance = (string)c["utterance"],
                };
                foreach (JToken l in (JArray)c["listeners"]) sc.Listeners.Add((string)l);
                JArray history = c["history"] as JArray ?? defaults[sc.Addressee] as JArray ?? new JArray();
                foreach (JToken h in history) sc.History.Add(new DialogueLine((string)h["speaker"], (string)h["text"]));
                JToken expect = c["expect"];
                foreach (JToken r in (JArray)expect["referent"]) sc.ExpectReferent.Add((string)r);
                foreach (JToken m in (JArray)expect["meaning"]) sc.ExpectMeaning.Add((string)m);
                foreach (var p in ((JObject)expect["react"]).Properties()) sc.ExpectReact[p.Name] = (string)p.Value;
                set.Cases.Add(sc);
            }
            return set;
        }

        public IEnumerable<ScenarioCase> Select(string split)
            => split == "all" ? Cases : Cases.Where(c => c.Split == split);
    }

    /// <summary>One judge's answer for one case (one repeat), scored against the pre-registered expectations.</summary>
    public sealed class CaseRun
    {
        public string CaseId;
        public string Category;
        public int Repeat;
        public bool Success;
        public string Failure;
        public string Referent;
        public string Meaning;
        public Dictionary<string, bool> Attention = new Dictionary<string, bool>();
        public Dictionary<string, bool> WouldReact = new Dictionary<string, bool>();
        public Dictionary<string, string> PlanText = new Dictionary<string, string>();
        public Dictionary<string, double> Probabilities = new Dictionary<string, double>();
        public string Raw;
        public double TotalMs;
        public double InferenceMs;
        public double QueueWaitMs;
        public int InputTokens;
        public bool ReferentOk;
        public bool MeaningOk;
        public int ReactChecked;
        public int ReactAgreed;
        public int ExpectedNoSlots;
        public int FalseReactions;
        public int ExpectedYesSlots;
        public int MissedReactions;
    }

    public sealed class EvalReport
    {
        public string JudgeId;
        public string ModelLabel;
        public string Split;
        public string FixtureSha256;
        public DateTime StartedUtc;
        public List<CaseRun> Runs = new List<CaseRun>();

        public IEnumerable<CaseRun> Scored => Runs.Where(r => r.Repeat == 0);

        public int Cases => Scored.Count();
        public int Failures => Scored.Count(r => !r.Success);
        public double ReferentAccuracy => Ratio(Scored.Count(r => r.ReferentOk), Cases);
        public double MeaningAccuracy => Ratio(Scored.Count(r => r.MeaningOk), Cases);
        public double JointAccuracy => Ratio(Scored.Count(r => r.ReferentOk && r.MeaningOk), Cases);
        public double AttentionAgreement => Ratio(Scored.Sum(r => r.ReactAgreed), Scored.Sum(r => r.ReactChecked));
        public int ExpectedNoSlots => Scored.Sum(r => r.ExpectedNoSlots);
        public int FalseReactions => Scored.Sum(r => r.FalseReactions);
        public int ExpectedYesSlots => Scored.Sum(r => r.ExpectedYesSlots);
        public int MissedReactions => Scored.Sum(r => r.MissedReactions);

        /// <summary>Repeats that disagreed with repeat 0 on any label.</summary>
        public int UnstableCases
        {
            get
            {
                int n = 0;
                foreach (var group in Runs.GroupBy(r => r.CaseId))
                {
                    CaseRun first = group.First(r => r.Repeat == 0);
                    if (group.Any(r => r.Referent != first.Referent || r.Meaning != first.Meaning ||
                                       !SameAttention(r.Attention, first.Attention))) n++;
                }
                return n;
            }
        }

        public double LatencyPercentile(double p) => Percentile(Runs.Where(r => r.Success || r.TotalMs > 0).Select(r => r.TotalMs), p);

        public JObject ToJson()
        {
            var runs = new JArray();
            foreach (CaseRun r in Runs)
            {
                runs.Add(JObject.FromObject(r));
            }
            return new JObject
            {
                ["judge"] = JudgeId,
                ["model"] = ModelLabel,
                ["split"] = Split,
                ["fixture_sha256"] = FixtureSha256,
                ["started_utc"] = StartedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["summary"] = new JObject
                {
                    ["cases"] = Cases,
                    ["failures"] = Failures,
                    ["referent_accuracy"] = Math.Round(ReferentAccuracy, 3),
                    ["meaning_accuracy"] = Math.Round(MeaningAccuracy, 3),
                    ["joint_accuracy"] = Math.Round(JointAccuracy, 3),
                    ["attention_agreement"] = Math.Round(AttentionAgreement, 3),
                    ["false_reactions"] = FalseReactions,
                    ["expected_no_slots"] = ExpectedNoSlots,
                    ["missed_reactions"] = MissedReactions,
                    ["expected_yes_slots"] = ExpectedYesSlots,
                    ["unstable_cases"] = UnstableCases,
                    ["latency_ms_p50"] = Math.Round(LatencyPercentile(0.5), 1),
                    ["latency_ms_p95"] = Math.Round(LatencyPercentile(0.95), 1),
                    ["latency_ms_max"] = Math.Round(LatencyPercentile(1.0), 1),
                },
                ["runs"] = runs,
            };
        }

        public string SummaryMarkdownRow()
            => "| " + JudgeId + " | " + Cases + " | " + Failures + " | " + Pct(ReferentAccuracy) + " | " + Pct(MeaningAccuracy) +
               " | " + Pct(JointAccuracy) + " | " + Pct(AttentionAgreement) + " | " + FalseReactions + "/" + ExpectedNoSlots +
               " | " + MissedReactions + "/" + ExpectedYesSlots + " | " + Ms(LatencyPercentile(0.5)) + " / " + Ms(LatencyPercentile(0.95)) + " |";

        public const string SummaryMarkdownHeader =
            "| judge | cases | invalid | referent | meaning | both | attention agree | false reactions | missed reactions | latency p50 / p95 |\n" +
            "|---|---|---|---|---|---|---|---|---|---|";

        public string CaseMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| case | referent | meaning | attention | policy could react | ok |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (CaseRun r in Scored)
            {
                if (!r.Success)
                {
                    sb.AppendLine("| " + r.CaseId + " | — | — | — | — | invalid: " + Escape(r.Failure) + " |");
                    continue;
                }
                string attention = string.Join(" ", r.Attention.Select(kv => kv.Key + ":" + (kv.Value ? "y" : "n")));
                string react = string.Join(" ", r.WouldReact.Select(kv => kv.Key + ":" + (kv.Value ? "y" : "n")));
                string ok = (r.ReferentOk ? "R" : "r̶") + (r.MeaningOk ? "M" : "m̶") + " " + r.ReactAgreed + "/" + r.ReactChecked +
                            (r.FalseReactions > 0 ? " FALSE-REACT" : "") + (r.MissedReactions > 0 ? " missed" : "");
                sb.AppendLine("| " + r.CaseId + " | " + r.Referent + " | " + r.Meaning + " | " + attention + " | " + react + " | " + ok + " |");
            }
            return sb.ToString();
        }

        private static bool SameAttention(Dictionary<string, bool> a, Dictionary<string, bool> b)
            => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out bool v) && v == kv.Value);

        private static double Ratio(int a, int b) => b == 0 ? 0 : (double)a / b;
        private static string Pct(double v) => Math.Round(v * 100).ToString(CultureInfo.InvariantCulture) + "%";
        private static string Ms(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture) + " ms";
        private static string Escape(string s) => (s ?? "").Replace("|", "/");

        public static double Percentile(IEnumerable<double> values, double p)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;
            int index = (int)Math.Ceiling(p * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
        }
    }

    /// <summary>Runs a judge over fixed scenarios and scores labels, attention and the policy's would-react output.</summary>
    public static class ScenarioEvaluator
    {
        public static UtteranceEvent ToEvent(ScenarioCase c, int turn)
            => UtteranceEvent.Create(1, turn, SpeakerKind.Player, SocialIds.Player, c.Addressee, c.Utterance, c.History,
                0, DateTime.UtcNow, c.Listeners);

        public static async Task<EvalReport> RunAsync(IListenerJudge judge, ScenarioSet set, TableProfile table, string split,
            int repeats, string fixtureSha256, CancellationToken cancellationToken, Action<string> log = null)
        {
            var report = new EvalReport
            {
                JudgeId = judge.JudgeId,
                ModelLabel = judge.ModelLabel,
                Split = split,
                FixtureSha256 = fixtureSha256,
                StartedUtc = DateTime.UtcNow,
            };
            var policy = new ReactionPolicy(table, new Random(7)) { SkipChanceOverride = 0 };

            int turn = 0;
            foreach (ScenarioCase c in set.Select(split))
            {
                for (int repeat = 0; repeat < Math.Max(1, repeats); repeat++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    UtteranceEvent e = ToEvent(c, ++turn);
                    JudgeOutcome outcome = await judge.JudgeAsync(new JudgeRequest(e, table), cancellationToken).ConfigureAwait(false);
                    CaseRun run = Score(c, repeat, outcome, e, policy);
                    report.Runs.Add(run);
                    if (repeat == 0 && log != null)
                        log(judge.JudgeId + " " + c.Id + ": " + (run.Success ? run.Referent + "/" + run.Meaning : "INVALID " + run.Failure) +
                            " " + Math.Round(run.TotalMs) + "ms");
                }
            }
            return report;
        }

        public static CaseRun Score(ScenarioCase c, int repeat, JudgeOutcome outcome, UtteranceEvent e, ReactionPolicy policy)
        {
            var run = new CaseRun
            {
                CaseId = c.Id,
                Category = c.Category,
                Repeat = repeat,
                Success = outcome.Success,
                Failure = outcome.Failure,
                Raw = outcome.RawOutput,
                TotalMs = outcome.TotalMs,
                InferenceMs = outcome.InferenceMs,
                QueueWaitMs = outcome.QueueWaitMs,
                InputTokens = outcome.InputTokens,
            };

            List<ReactionPlan> plans = null;
            if (outcome.Success)
            {
                EventJudgment j = outcome.Judgment;
                run.Referent = j.Referent;
                run.Meaning = SocialLabels.ToLabel(j.Meaning);
                foreach (var kv in j.Probabilities) run.Probabilities[kv.Key] = Math.Round(kv.Value, 4);
                foreach (var kv in j.Listeners) run.Attention[kv.Key] = kv.Value.AttentionWarranted;
                run.ReferentOk = c.ExpectReferent.Contains(run.Referent);
                run.MeaningOk = c.ExpectMeaning.Contains(run.Meaning);
                policy.ResetHistory();
                plans = policy.Plan(e, j, 0);
                foreach (ReactionPlan p in plans)
                {
                    run.WouldReact[p.ListenerId] = policy.CouldReact(e, j, p.ListenerId, out string rule);
                    run.PlanText[p.ListenerId] = p.Describe() + (rule != null ? " rule=" + rule : "");
                }
            }

            foreach (var kv in c.ExpectReact)
            {
                if (kv.Value == "any") continue;
                bool expected = kv.Value == "yes";
                run.ReactChecked++;
                bool attention = run.Attention.TryGetValue(kv.Key, out bool a) && a;
                if (outcome.Success && attention == expected) run.ReactAgreed++;

                bool reacts = run.WouldReact.TryGetValue(kv.Key, out bool w) && w;
                if (expected)
                {
                    run.ExpectedYesSlots++;
                    if (!reacts) run.MissedReactions++;
                }
                else
                {
                    run.ExpectedNoSlots++;
                    if (reacts) run.FalseReactions++;
                }
            }
            return run;
        }
    }
}
