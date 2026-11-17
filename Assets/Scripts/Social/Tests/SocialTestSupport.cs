using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Tavern.Social.Tests
{
    /// <summary>Shared fixtures for the Tavern.Social tests (Unity EditMode and the dotnet harness).</summary>
    internal static class SocialTestSupport
    {
        public static readonly string[] FaceNames =
        {
            "Neutral", "Content", "Happy", "Delighted", "Smug", "Suspicious", "Grumpy", "Angry", "Worried", "Sad",
            "Surprised", "Scared", "Disgusted", "Thinking"
        };

        /// <summary>Unity runs tests from the project root; the dotnet harness copies files next to the binary.</summary>
        public static string ReadFile(string projectRelative)
        {
            string name = Path.GetFileName(projectRelative);
            string[] candidates =
            {
                Path.Combine(Directory.GetCurrentDirectory(), projectRelative),
                Path.Combine(AppContext.BaseDirectory, "Fixtures", name),
            };
            foreach (string path in candidates)
                if (File.Exists(path)) return File.ReadAllText(path);
            throw new FileNotFoundException(projectRelative);
        }

        private static TableProfile _table;

        public static TableProfile Table
            => _table ?? (_table = TableProfile.Parse(ReadFile("Assets/Resources/TableReactions/wu_table.json"), FaceNames));

        public static ScenarioSet Scenarios
            => ScenarioSet.Parse(ReadFile("Assets/Scripts/Social/Tests/Fixtures/table-reaction-scenarios.json"));

        public static UtteranceEvent Event(string text, int session = 1, int turn = 1, string addressee = "wu",
            double realtime = 0, params string[] listeners)
        {
            if (listeners == null || listeners.Length == 0) listeners = new[] { "shara", "varien" };
            return UtteranceEvent.Create(session, turn, SpeakerKind.Player, SocialIds.Player, addressee, text,
                new[] { new DialogueLine(addressee, "The tea is still warm.") }, realtime, DateTime.UtcNow, listeners);
        }

        public static EventJudgment Judgment(string referent, string meaning, params (string id, bool attend)[] attention)
        {
            var raw = new RawJudgment { Referent = referent, Meaning = meaning };
            var ids = new List<string>();
            foreach (var a in attention)
            {
                raw.Attention[a.id] = a.attend;
                ids.Add(a.id);
            }
            if (!JudgmentValidator.TryValidate(raw, Table.MemberIds, ids, out EventJudgment j, out string error))
                throw new InvalidOperationException(error);
            return j;
        }

        public static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
    }

    internal sealed class FakeTransport : ISystemOneTransport
    {
        public Func<string, string> Respond;
        public Exception Throw;
        public string LastBody;
        public int Calls;

        public Task<string> PostJsonAsync(string path, string json, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = json;
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw != null) return Task.FromException<string>(Throw);
            return Task.FromResult(Respond(json));
        }

        public Task<string> GetAsync(string path, CancellationToken cancellationToken) => Task.FromResult("{\"status\":\"ok\"}");
    }

    internal sealed class FakeGrammarRunner : IGrammarTextRunner
    {
        public string Output;
        public string LastSchema;
        public string LastUser;
        public string ModelLabel => "fake-gemma";

        public Task<GrammarRunResult> RunAsync(string system, string user, string jsonSchema, int maxTokens, CancellationToken cancellationToken)
        {
            LastSchema = jsonSchema;
            LastUser = user;
            return Task.FromResult(new GrammarRunResult { Success = true, Text = Output, InferenceMs = 1 });
        }
    }
}
