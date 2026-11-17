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
        string xybridCli = Arg(args, "--xybrid", null);
        string modelDir = Arg(args, "--model-dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Developer/xybrid-models/d1-2b-q4km"));

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

        if (judgeArg == "d1xybrid")
        {
            if (xybridCli == null) { Console.Error.WriteLine("--xybrid <path to xybrid v0.18+ CLI> is required"); return 2; }
            judges.Add(new XybridDecisionJudge(new CliDecisionRunner(xybridCli, modelDir)));
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

/// <summary>
/// Runs each request through <c>xybrid run --decision-request</c>. Every call loads the model again, so wall
/// time includes loading; the in-process cost has to be estimated from the difference between requests with
/// different question counts.
/// </summary>
internal sealed class CliDecisionRunner : IDecisionRunner
{
    private readonly string _cli;
    private readonly string _modelDir;

    public CliDecisionRunner(string cli, string modelDir)
    {
        _cli = cli;
        _modelDir = modelDir;
    }

    public string ModelLabel => "d1-2B Q4_K_M via xybrid CLI (lfm3-d1/text/v1, CPU 4 threads)";

    public async Task<DecisionRunResult> RunAsync(string requestJson, CancellationToken cancellationToken)
    {
        string file = Path.Combine(Path.GetTempPath(), "tavern-d1-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, requestJson);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(_cli)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in new[] { "run", "--directory", _modelDir, "--decision-request", file }) psi.ArgumentList.Add(a);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                Task<string> stdout = p.StandardOutput.ReadToEndAsync();
                Task<string> stderr = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string output = await stdout.ConfigureAwait(false);
                DecisionRunResult result = p.ExitCode == 0
                    ? XybridDecisionJudge.ParseCliRecord(output)
                    : new DecisionRunResult { Error = "exit " + p.ExitCode + ": " + (await stderr.ConfigureAwait(false)).Trim(), Raw = output };
                result.InferenceMs = sw.Elapsed.TotalMilliseconds;
                return result;
            }
        }
        finally
        {
            File.Delete(file);
        }
    }
}
