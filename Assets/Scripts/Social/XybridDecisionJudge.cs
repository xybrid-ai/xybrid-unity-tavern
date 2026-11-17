using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Tavern.Social
{
    /// <summary>One decision answer from Xybrid: the question id and its distribution, in protocol order.</summary>
    public sealed class DecisionAnswerData
    {
        public string QuestionId;
        public readonly List<KeyValuePair<string, double>> Distribution = new List<KeyValuePair<string, double>>();
    }

    public sealed class DecisionRunResult
    {
        public bool Success;
        public string Error;
        public List<DecisionAnswerData> Answers = new List<DecisionAnswerData>();
        public long InputTokens;
        public int Passes;
        public double QueueWaitMs;
        public double InferenceMs;
        public string Raw;
    }

    /// <summary>Runs one Liquid decision request (the <c>{state, questions}</c> JSON) through Xybrid.</summary>
    public interface IDecisionRunner
    {
        string ModelLabel { get; }
        Task<DecisionRunResult> RunAsync(string requestJson, CancellationToken cancellationToken);
    }

    /// <summary>
    /// d1-2B through Xybrid's own decision runtime (<c>lfm3-d1/text/v1</c>, v0.18.0+): same request and the
    /// same frozen question wording as <see cref="D1SystemOneJudge"/>, answers converted to the same
    /// validated path. Xybrid's profile is CPU-only (4 threads, one whole-prompt pass per question) and
    /// pins the Q4_K_M weights, so labels and timings are not those of the Q8_0 Metal sidecar.
    /// </summary>
    public sealed class XybridDecisionJudge : IListenerJudge
    {
        private readonly IDecisionRunner _runner;

        public string JudgeId => "d1-xybrid";
        public string ModelLabel => _runner.ModelLabel;

        /// <summary>Same uncalibrated operating point as the sidecar judge.</summary>
        public double AttentionThreshold { get; set; } = 0.5;

        public XybridDecisionJudge(IDecisionRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public async Task<JudgeOutcome> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            JObject body = D1SystemOneJudge.BuildRequest(request);
            DecisionRunResult run;
            try
            {
                run = await _runner.RunAsync(body.ToString(Formatting.None), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return JudgeOutcome.Failed(JudgeId, ModelLabel, "xybrid: " + ex.Message, null, sw.Elapsed.TotalMilliseconds);
            }
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = new JudgeOutcome
            {
                JudgeId = JudgeId,
                ModelLabel = ModelLabel,
                RawOutput = run?.Raw,
                QueueWaitMs = run?.QueueWaitMs ?? 0,
                InferenceMs = run?.InferenceMs ?? 0,
                TotalMs = sw.Elapsed.TotalMilliseconds,
                InputTokens = (int)Math.Min(int.MaxValue, run?.InputTokens ?? 0),
            };
            if (run == null || !run.Success)
            {
                outcome.Failure = "decision run failed: " + (run?.Error ?? "no result");
                return outcome;
            }

            string converted = ToSystemOneJson(body, run.Answers, run.InputTokens, out string convertError);
            if (converted == null)
            {
                outcome.Failure = "invalid decisions: " + convertError;
                return outcome;
            }
            if (!D1SystemOneJudge.TryParse(converted, request, AttentionThreshold, out RawJudgment raw, out _, out string parseError))
            {
                outcome.Failure = "invalid decisions: " + parseError;
                return outcome;
            }
            if (JudgmentValidator.TryValidate(raw, request.Table.MemberIds, request.Event.EligibleListenerIds,
                    out EventJudgment judgment, out string error))
            {
                outcome.Success = true;
                outcome.Judgment = judgment;
            }
            else
            {
                outcome.Failure = error;
            }
            return outcome;
        }

        /// <summary>
        /// Rebuilds the llama.cpp <c>/v1/systemone</c> answer shape from Xybrid's distributions, using only the
        /// question id and (key, probability) pairs: a choice is the first key with the highest probability
        /// (Xybrid's own rule), a yes/no is p("true"). Null on a question the request didn't ask, a missing
        /// question, or a bad probability.
        /// </summary>
        public static string ToSystemOneJson(JObject requestBody, IList<DecisionAnswerData> answers, long inputTokens, out string error)
        {
            error = null;
            if (!(requestBody?["questions"] is JObject questions)) { error = "request has no questions"; return null; }
            var seen = new HashSet<string>();
            var outAnswers = new JObject();

            foreach (DecisionAnswerData a in answers ?? new List<DecisionAnswerData>())
            {
                if (a == null || string.IsNullOrEmpty(a.QuestionId) || !(questions[a.QuestionId] is JObject q))
                {
                    error = "answer for a question that wasn't asked";
                    return null;
                }
                if (!seen.Add(a.QuestionId)) { error = "duplicate answer for " + a.QuestionId; return null; }
                foreach (var kv in a.Distribution)
                {
                    if (double.IsNaN(kv.Value) || kv.Value < 0 || kv.Value > 1) { error = "probability out of range in " + a.QuestionId; return null; }
                }

                string type = (string)q["type"];
                if (type == "choice")
                {
                    if (a.Distribution.Count == 0) { error = a.QuestionId + " has an empty distribution"; return null; }
                    var probabilities = new JObject();
                    string best = null;
                    double bestP = -1;
                    foreach (var kv in a.Distribution)
                    {
                        probabilities[kv.Key] = kv.Value;
                        if (kv.Value > bestP) { best = kv.Key; bestP = kv.Value; }
                    }
                    outAnswers[a.QuestionId] = new JObject
                    {
                        ["type"] = "choice", ["choice"] = best, ["confidence"] = bestP, ["probabilities"] = probabilities,
                    };
                }
                else if (type == "noul")
                {
                    double? pYes = null;
                    foreach (var kv in a.Distribution)
                        if (kv.Key == "true") pYes = kv.Value;
                    if (pYes == null) { error = a.QuestionId + " has no \"true\" entry"; return null; }
                    outAnswers[a.QuestionId] = new JObject { ["type"] = "noul", ["noul"] = pYes.Value };
                }
                else
                {
                    error = "unsupported question type '" + type + "'";
                    return null;
                }
            }

            foreach (var q in questions.Properties())
                if (!seen.Contains(q.Name)) { error = "no answer for " + q.Name; return null; }

            return new JObject
            {
                ["answers"] = outAnswers,
                ["usage"] = new JObject { ["input_tokens"] = inputTokens, ["output_tokens"] = 0 },
            }.ToString(Formatting.None);
        }

        /// <summary>Reads the CLI's <c>xybrid.decision.v1</c> record (<c>xybrid run --decision-request</c>).</summary>
        public static DecisionRunResult ParseCliRecord(string json)
        {
            var result = new DecisionRunResult { Raw = json };
            JObject root;
            try
            {
                root = JObject.Parse(json ?? "");
            }
            catch (JsonException ex)
            {
                result.Error = "malformed JSON (" + ex.Message + ")";
                return result;
            }
            if ((string)root["format"] != "xybrid.decision.v1" || !(root["decisions"]?["answers"] is JArray list))
            {
                result.Error = "not a xybrid.decision.v1 record";
                return result;
            }
            foreach (JToken a in list)
            {
                var data = new DecisionAnswerData { QuestionId = (string)a["question_id"] };
                foreach (JToken d in (a["distribution"] as JArray) ?? new JArray())
                {
                    JToken p = d["probability"];
                    double prob = p != null && (p.Type == JTokenType.Float || p.Type == JTokenType.Integer)
                        ? (double)p
                        : double.Parse((string)p ?? "NaN", CultureInfo.InvariantCulture);
                    data.Distribution.Add(new KeyValuePair<string, double>((string)d["key"], prob));
                }
                result.Answers.Add(data);
            }
            result.InputTokens = (long?)root["tokens_in"] ?? 0;
            result.Passes = (int?)root["decisions"]?["usage"]?["passes"] ?? 0;
            result.Success = true;
            return result;
        }
    }
}
