// Dev harness for the table-reaction experiment: runs the rules and d1 judges over the fixed scenarios.
//   dotnet run -- --judge d1 --split dev
//   dotnet run -- --judge all --split test --repeats 3 --out ../../Logs/table-reactions-eval/harness.json
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tavern.Social;

internal static class Program
{
    private static readonly string[] FaceNames =
    {
        "Neutral", "Content", "Happy", "Delighted", "Smug", "Suspicious", "Grumpy", "Angry", "Worried", "Sad",
        "Surprised", "Scared", "Disgusted", "Thinking"
    };

    private static async Task<int> Main(string[] args)
    {
        string root = FindProjectRoot();
        string judgeArg = Arg(args, "--judge", "all");
        string split = Arg(args, "--split", "dev");
        int repeats = int.Parse(Arg(args, "--repeats", "1"));
        string url = Arg(args, "--url", "http://127.0.0.1:8187");
        string output = Arg(args, "--out", null);
        bool verbose = Array.IndexOf(args, "--verbose") >= 0;

        string fixturePath = Path.Combine(root, "Assets/Scripts/Social/Tests/Fixtures/table-reaction-scenarios.json");
        string fixtureJson = File.ReadAllText(fixturePath);
        string sha = Sha256(fixturePath);
        ScenarioSet set = ScenarioSet.Parse(fixtureJson);
        TableProfile table = TableProfile.Parse(
            File.ReadAllText(Path.Combine(root, "Assets/Resources/TableReactions/wu_table.json")), FaceNames);

        var judges = new List<IListenerJudge>();
        if (judgeArg == "all" || judgeArg == "rules") judges.Add(new RuleListenerJudge());
        if (judgeArg == "all" || judgeArg == "d1")
        {
            var d1 = new D1SystemOneJudge(new HttpSystemOneTransport(url, TimeSpan.FromSeconds(30)));
            if (!await d1.IsHealthyAsync(CancellationToken.None))
            {
                Console.Error.WriteLine("d1 sidecar not healthy at " + url + " (tools/d1-sidecar/start.sh)");
                return 2;
            }
            judges.Add(d1);
        }

        Console.WriteLine("fixture " + sha.Substring(0, 12) + " split=" + split + " repeats=" + repeats);
        var reports = new JArray();
        var rows = new List<string>();
        foreach (IListenerJudge judge in judges)
        {
            // Warm-up on a sentence outside the fixtures, so the first case doesn't pay for loading
            await judge.JudgeAsync(new JudgeRequest(UtteranceEvent.Create(1, 1, SpeakerKind.Player, SocialIds.Player, "wu",
                "The stew smells good tonight.", null, 0, DateTime.UtcNow, new[] { "shara", "varien" }), table), CancellationToken.None);

            EvalReport report = await ScenarioEvaluator.RunAsync(judge, set, table, split, repeats, sha, CancellationToken.None,
                verbose ? Console.WriteLine : (Action<string>)null);
            Console.WriteLine();
            Console.WriteLine("## " + judge.JudgeId + " — " + judge.ModelLabel);
            Console.WriteLine(report.CaseMarkdown());
            rows.Add(report.SummaryMarkdownRow());
            reports.Add(report.ToJson());
        }

        Console.WriteLine(EvalReport.SummaryMarkdownHeader);
        foreach (string row in rows) Console.WriteLine(row);

        if (output != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output, new JObject { ["fixture"] = fixturePath, ["sha256"] = sha, ["reports"] = reports }.ToString(Formatting.Indented));
            Console.WriteLine("wrote " + output);
        }
        return 0;
    }

    private static string Arg(string[] args, string name, string fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static string FindProjectRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "Assets/Scripts/Social")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("Run from inside the Xybrid Tavern project.");
    }

    private static string Sha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
