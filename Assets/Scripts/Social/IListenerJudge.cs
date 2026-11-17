using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tavern.Social
{
    /// <summary>Everything a judge may see: the event and the table's members. Nothing from NPC dialogue histories.</summary>
    public sealed class JudgeRequest
    {
        public UtteranceEvent Event { get; }
        public TableProfile Table { get; }

        public JudgeRequest(UtteranceEvent utterance, TableProfile table)
        {
            Event = utterance ?? throw new ArgumentNullException(nameof(utterance));
            Table = table ?? throw new ArgumentNullException(nameof(table));
        }

        public string NameOf(string id)
        {
            if (id == SocialIds.Player) return "the player";
            TableMember m = Table.Find(id);
            return m != null ? m.DisplayName : id;
        }
    }

    /// <summary>A judge's answer for one event, with timings and its raw output for the debug view.</summary>
    public sealed class JudgeOutcome
    {
        public string JudgeId;
        public string ModelLabel;
        public bool Success;
        public EventJudgment Judgment;
        public string Failure;
        public string RawOutput;
        /// <summary>Time spent waiting for the model (shared lock, busy server). 0 when not applicable.</summary>
        public double QueueWaitMs;
        /// <summary>Time the model itself took.</summary>
        public double InferenceMs;
        /// <summary>Wall clock from the call to the result.</summary>
        public double TotalMs;
        public int InputTokens;

        public static JudgeOutcome Failed(string judgeId, string model, string failure, string raw, double totalMs)
            => new JudgeOutcome { JudgeId = judgeId, ModelLabel = model, Success = false, Failure = failure, RawOutput = raw, TotalMs = totalMs };
    }

    /// <summary>
    /// Interprets one utterance for a set of listeners. Implementations never decide physical hearing and
    /// never act: they return labels, which <see cref="JudgmentValidator"/> checks and the policy maps.
    /// </summary>
    public interface IListenerJudge
    {
        string JudgeId { get; }
        /// <summary>Human-readable model + runtime, e.g. "d1-2B Q8_0 via llama.cpp sidecar (not Xybrid)".</summary>
        string ModelLabel { get; }
        Task<JudgeOutcome> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Baseline: name/alias matching plus praise/criticism/concern word lists. No pronoun resolution, no
    /// negation handling, no context: the point is to show what simple rules get right and wrong.
    /// </summary>
    public sealed class RuleListenerJudge : IListenerJudge
    {
        public string JudgeId => "rules";
        public string ModelLabel => "keyword rules (no model)";

        private static readonly string[] SecondPerson = { "you", "your", "yours", "yourself" };

        // Generic word lists, not tuned per fixture. Stems match word prefixes ("disciplin" → disciplined).
        private static readonly string[] PraiseWords =
        {
            "disciplin", "incredibl", "amazing", "wise", "wisest", "best", "impressiv", "skilled", "skilful", "talent",
            "strong", "brave", "great", "brilliant", "excellent", "fastest", "graceful", "lucky", "admir", "gifted",
            "fine", "good", "remarkabl", "master of", "respect", "kind"
        };
        private static readonly string[] CriticismWords =
        {
            "coward", "afraid", "hides", "hiding", "weak", "lazy", "loud", "old-fashioned", "outdated", "too much",
            "annoy", "careless", "slow", "arrogant", "rude", "useless", "clumsy", "boring",
            "foolish", "stupid", "bad", "worst"
        };
        private static readonly string[] ConcernWords =
        {
            "exhausted", "troubled", "nervous", "worried", "alright", "all right", "hurt", "tired", "sick", "pale",
            "upset", "sad", "unwell", "injured"
        };

        public Task<JudgeOutcome> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            UtteranceEvent e = request.Event;
            string text = " " + Normalize(e.Text) + " ";

            // Referent: members named in the utterance; otherwise second person points at the addressee
            var named = new List<string>();
            foreach (TableMember m in request.Table.Members)
            {
                foreach (string alias in m.Aliases)
                {
                    if (text.Contains(" " + alias + " ") && !named.Contains(m.Id))
                        named.Add(m.Id);
                }
            }
            string referent;
            if (named.Count == 1) referent = named[0];
            else if (named.Count > 1) referent = SocialIds.Unclear;
            else if (ContainsAny(text, SecondPerson, wholeWord: true) && request.Table.Find(e.AddressedId) != null) referent = e.AddressedId;
            else referent = SocialIds.None;

            int praise = Count(text, PraiseWords), criticism = Count(text, CriticismWords), concern = Count(text, ConcernWords);
            string meaning = "neutral";
            int best = Math.Max(praise, Math.Max(criticism, concern));
            if (best > 0)
            {
                int ties = (praise == best ? 1 : 0) + (criticism == best ? 1 : 0) + (concern == best ? 1 : 0);
                meaning = ties > 1 ? "unclear" : praise == best ? "praise" : criticism == best ? "criticism" : "concern";
            }

            var raw = new RawJudgment { Referent = referent, Meaning = meaning };
            bool charged = meaning == "praise" || meaning == "criticism" || meaning == "concern";
            foreach (string listener in e.EligibleListenerIds)
            {
                bool aboutTable = referent != SocialIds.None && referent != SocialIds.Unclear;
                raw.Attention[listener] = charged && aboutTable;
            }

            sw.Stop();
            var outcome = new JudgeOutcome
            {
                JudgeId = JudgeId,
                ModelLabel = ModelLabel,
                InferenceMs = sw.Elapsed.TotalMilliseconds,
                TotalMs = sw.Elapsed.TotalMilliseconds,
                RawOutput = "referent=" + referent + " meaning=" + meaning,
            };
            if (JudgmentValidator.TryValidate(raw, request.Table.MemberIds, e.EligibleListenerIds, out EventJudgment judgment, out string error))
            {
                outcome.Success = true;
                outcome.Judgment = judgment;
            }
            else
            {
                outcome.Failure = error;
            }
            return Task.FromResult(outcome);
        }

        private static string Normalize(string s)
        {
            var chars = s.ToLowerInvariant().Replace("’", "'").Replace("'s ", " ").ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '\'') chars[i] = ' ';
            return string.Join(" ", new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static int Count(string text, string[] words)
        {
            int n = 0;
            foreach (string w in words)
                if (text.Contains(" " + w)) n++;
            return n;
        }

        private static bool ContainsAny(string text, string[] words, bool wholeWord)
        {
            foreach (string w in words)
                if (text.Contains(wholeWord ? " " + w + " " : w)) return true;
            return false;
        }
    }
}
