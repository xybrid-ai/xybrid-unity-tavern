#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tavern.Social;
using UnityEditor;
using UnityEngine;
using Xybrid;
using Xybrid.ModelAsset;
using Debug = UnityEngine.Debug;

namespace Tavern.Social.Editor
{
    /// <summary>
    /// Authoritative offline comparison for the table-reaction experiment: rules, the d1 decision model
    /// (llama.cpp sidecar, not Xybrid) and the Gemma4NPC structured-judgment baseline (Xybrid, loaded directly)
    /// over the pre-registered scenarios. Writes Logs/table-reactions-eval/eval-&lt;utc&gt;.{json,md}.
    ///   ./scripts/unity.sh method Tavern.Social.Editor.TableReactionEval.Run
    ///   Menu: Tavern &gt; Table Reactions &gt; Run Evaluation (Editor must not be in Play mode)
    /// Options: -tableEvalJudges rules,d1,gemma  -tableEvalRepeats 3  -tableEvalSidecar http://127.0.0.1:8187
    /// </summary>
    public static class TableReactionEval
    {
        private const string FixturePath = "Assets/Scripts/Social/Tests/Fixtures/table-reaction-scenarios.json";
        private const string ProfilePath = "Assets/Resources/TableReactions/wu_table.json";
        private const string GemmaAssetPath = "Assets/XybridModels/gemma4npc-e2b-q4.asset";

        [MenuItem("Tavern/Table Reactions/Run Evaluation")]
        public static void RunFromMenu()
        {
            try
            {
                string md = RunAll(Arg("-tableEvalJudges", "rules,d1,gemma"), int.Parse(Arg("-tableEvalRepeats", "3")),
                    Arg("-tableEvalSidecar", "http://127.0.0.1:8187"));
                Debug.Log("[TableReactionEval]\n" + md);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        /// <summary>Batch entry point; exits with 0 on success.</summary>
        public static void Run()
        {
            int code = 0;
            try
            {
                string md = RunAll(Arg("-tableEvalJudges", "rules,d1,gemma"), int.Parse(Arg("-tableEvalRepeats", "3")),
                    Arg("-tableEvalSidecar", "http://127.0.0.1:8187"));
                Debug.Log("[TableReactionEval]\n" + md);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static string RunAll(string judgeList, int repeats, string sidecarUrl)
        {
            string fixtureJson = File.ReadAllText(FixturePath);
            string sha = Sha256(FixturePath);
            ScenarioSet set = ScenarioSet.Parse(fixtureJson);
            TableProfile table = TableProfile.Parse(File.ReadAllText(ProfilePath), Enum.GetNames(typeof(FaceEmotion)));
            var wanted = new HashSet<string>(judgeList.Split(','));

            var judges = new List<IListenerJudge>();
            var notes = new List<string>();
            DirectModelGrammarRunner gemmaRunner = null;
            try
            {
                if (wanted.Contains("rules")) judges.Add(new RuleListenerJudge());
                if (wanted.Contains("d1"))
                {
                    var d1 = new D1SystemOneJudge(new HttpSystemOneTransport(sidecarUrl, TimeSpan.FromSeconds(30)));
                    if (d1.IsHealthyAsync(CancellationToken.None).GetAwaiter().GetResult()) judges.Add(d1);
                    else notes.Add("d1 skipped: sidecar not healthy at " + sidecarUrl + " (tools/d1-sidecar/start.sh)");
                }
                if (wanted.Contains("gemma"))
                {
                    gemmaRunner = DirectModelGrammarRunner.Load(GemmaAssetPath, out string loadNote);
                    notes.Add(loadNote);
                    if (gemmaRunner != null) judges.Add(new GemmaStructuredJudge(gemmaRunner));
                }

                var md = new StringBuilder();
                md.AppendLine("# Table reaction evaluation " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC");
                md.AppendLine();
                md.AppendLine("Fixture `" + FixturePath + "` sha256 `" + sha + "`. Hardware: " + SystemInfo.processorType +
                              ", " + SystemInfo.systemMemorySize + " MB RAM, " + SystemInfo.graphicsDeviceName + ".");
                foreach (string n in notes) md.AppendLine("- " + n);
                md.AppendLine();

                var reports = new JArray();
                foreach (string split in Arg("-tableEvalSplits", "test,dev").Split(','))
                {
                    var rows = new List<string>();
                    var detail = new StringBuilder();
                    foreach (IListenerJudge judge in judges)
                    {
                        Warm(judge, table);
                        int n = judge is RuleListenerJudge ? 1 : (split == "test" ? repeats : 1);
                        EvalReport report = ScenarioEvaluator.RunAsync(judge, set, table, split, n, sha, CancellationToken.None,
                            line => Debug.Log("[TableReactionEval] " + line)).GetAwaiter().GetResult();
                        rows.Add(report.SummaryMarkdownRow());
                        reports.Add(report.ToJson());
                        detail.AppendLine("### " + split + " — " + judge.JudgeId + ": " + judge.ModelLabel);
                        detail.AppendLine();
                        detail.AppendLine(report.CaseMarkdown());
                        detail.AppendLine("Repeats per case: " + n + ". Cases whose labels changed between repeats: " + report.UnstableCases + ".");
                        detail.AppendLine();
                    }
                    md.AppendLine("## Split: " + split);
                    md.AppendLine();
                    md.AppendLine(EvalReport.SummaryMarkdownHeader);
                    foreach (string r in rows) md.AppendLine(r);
                    md.AppendLine();
                    md.Append(detail);
                }

                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "table-reactions-eval"));
                Directory.CreateDirectory(dir);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                File.WriteAllText(Path.Combine(dir, "eval-" + stamp + ".json"),
                    new JObject { ["fixture"] = FixturePath, ["sha256"] = sha, ["notes"] = new JArray(notes), ["reports"] = reports }
                        .ToString(Formatting.Indented));
                File.WriteAllText(Path.Combine(dir, "eval-" + stamp + ".md"), md.ToString());
                return md.ToString();
            }
            finally
            {
                gemmaRunner?.Dispose();
            }
        }

        /// <summary>One untimed call on a sentence outside the fixtures, so loading isn't counted as latency.</summary>
        private static void Warm(IListenerJudge judge, TableProfile table)
        {
            UtteranceEvent e = UtteranceEvent.Create(1, 1, SpeakerKind.Player, SocialIds.Player, "wu", "The stew smells good tonight.",
                null, 0, DateTime.UtcNow, new[] { "shara", "varien" });
            judge.JudgeAsync(new JudgeRequest(e, table), CancellationToken.None).GetAwaiter().GetResult();
        }

        private static string Arg(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>Grammar runner over a Model loaded directly (no XybridModelService outside Play mode).</summary>
    internal sealed class DirectModelGrammarRunner : IGrammarTextRunner, IDisposable
    {
        private readonly Model _model;
        private readonly Dictionary<string, string> _grammars = new Dictionary<string, string>();
        private ConversationContext _context;
        private string _system;

        private DirectModelGrammarRunner(Model model)
        {
            _model = model;
        }

        public string ModelLabel => _model.ModelId + " via Xybrid " + XybridClient.Version;

        public static DirectModelGrammarRunner Load(string assetPath, out string note)
        {
            var asset = AssetDatabase.LoadAssetAtPath<XybridModelAsset>(assetPath);
            if (asset == null)
            {
                note = "gemma skipped: no model asset at " + assetPath;
                return null;
            }
            var sw = Stopwatch.StartNew();
            if (!XybridClient.IsInitialized) XybridClient.Initialize();
            Model model;
            using (var loader = ModelLoader.FromDirectory(asset.GetRuntimePath()))
                model = loader.Load();
            note = "gemma: loaded " + model.ModelId + " via Xybrid " + XybridClient.Version + " in " + sw.ElapsedMilliseconds + " ms";
            return new DirectModelGrammarRunner(model);
        }

        public Task<GrammarRunResult> RunAsync(string system, string user, string jsonSchema, int maxTokens, CancellationToken cancellationToken)
        {
            if (!_grammars.TryGetValue(jsonSchema, out string grammar))
            {
                grammar = XybridClient.JsonSchemaToGbnf(jsonSchema);
                _grammars[jsonSchema] = grammar;
            }
            if (_context == null || _system != system)
            {
                _context?.Dispose();
                _context = new ConversationContext("table-judge-eval");
                _context.SetSystem(system);
                _system = system;
            }

            return Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                using (GenerationConfig config = GenerationConfig.Greedy())
                using (Envelope envelope = Envelope.Text(user))
                {
                    config.SetMaxTokens(maxTokens);
                    config.SetGrammar(grammar);
                    using (InferenceResult r = _model.Run(envelope, _context, config, cancellationToken))
                    {
                        return new GrammarRunResult
                        {
                            Success = r.Success,
                            Text = r.Text,
                            Error = r.Success ? null : r.Error,
                            InferenceMs = sw.Elapsed.TotalMilliseconds,
                        };
                    }
                }
            });
        }

        public void Dispose()
        {
            _context?.Dispose();
            _model?.Dispose();
        }
    }
}
#endif
