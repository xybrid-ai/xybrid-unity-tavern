using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tavern.Dialogue;
using Tavern.Social;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// DEV ONLY. Blind test of the table-reaction experiment: can a player tell that Shara and Varien react to what
/// was said, without knowing which mode is on? F6 (on the <see cref="TableReactionDirector"/>) starts it.
///
/// It says 16 fixed lines to Master Wu for the player, 8 remarks about someone at the table and 8 small talk. Each
/// line runs under a condition picked at random and kept hidden (old content-blind watcher vs d1), balanced so each
/// condition gets 4 of each kind. After each line the player presses Y ("someone reacted to that") or N. Score per
/// condition = yes-rate after remarks minus yes-rate after small talk: 0 means the player can't tell reactions
/// from random glances, 100 means they always can. It also measures how big each listener's response was (head
/// turn, face change from the resting mood, gestures, where they looked) over the 6 s after each line.
///
/// Output: Logs/table-reactions-blind/&lt;utc&gt;/ (trials.jsonl, results.json, summary.md). The reveal also pools all
/// runs made with the same <see cref="ReactionStyle"/> and policy file.
/// </summary>
public class TableReactionBlindTest : MonoBehaviour
{
    /// <summary>
    /// Bump when the way reactions are performed changes (intensities, timings, the watcher's glances), so runs
    /// before and after aren't pooled together. The policy file is tagged separately by its hash.
    /// </summary>
    public const string ReactionStyle = "v2-strong";

    /// <summary>
    /// Remarks are from the scenario fixtures where d1 picked the right person and tone and the policy planned a
    /// visible reaction, so this measures whether reactions are noticeable, not whether d1 reads lines correctly.
    /// </summary>
    public static readonly BlindLine[] Lines =
    {
        new BlindLine("Varien seems like your most disciplined student.", BlindLineKind.AboutSomeone),
        new BlindLine("Shara's flying crescent kick was incredible.", BlindLineKind.AboutSomeone),
        new BlindLine("Shara has the fastest hands I've ever seen.", BlindLineKind.AboutSomeone),
        new BlindLine("That loud girl at your table is giving me a headache.", BlindLineKind.AboutSomeone),
        new BlindLine("Varien looks exhausted. Is he alright?", BlindLineKind.AboutSomeone),
        new BlindLine("Your quiet student looks nervous tonight.", BlindLineKind.AboutSomeone),
        new BlindLine("Your students are lucky to have you.", BlindLineKind.AboutSomeone),
        new BlindLine("Shara told me Varien is the best fighter she knows.", BlindLineKind.AboutSomeone),
        new BlindLine("What's good to drink here?", BlindLineKind.SmallTalk),
        new BlindLine("Is it always this busy on market day?", BlindLineKind.SmallTalk),
        new BlindLine("Looks like rain is coming over the hills.", BlindLineKind.SmallTalk),
        new BlindLine("Good evening. Mind if I sit with you?", BlindLineKind.SmallTalk),
        new BlindLine("How long have you been coming to this tavern?", BlindLineKind.SmallTalk),
        new BlindLine("Where can I find a room for the night?", BlindLineKind.SmallTalk),
        new BlindLine("The fire's warm tonight.", BlindLineKind.SmallTalk),
        new BlindLine("The stew smells good tonight.", BlindLineKind.SmallTalk),
    };

    private static readonly (string id, string label, TableReactionDirector.Mode mode)[] Conditions =
    {
        ("off", "Old behaviour (Off)", TableReactionDirector.Mode.Off),
        ("d1", "d1 decision model", TableReactionDirector.Mode.D1Sidecar),
    };

    private const float PreviewSeconds = 1.5f;   // The next line is shown before it's said, so it's read beforehand
    private const float AskAfterSeconds = 2.5f;  // Reactions start within about 1.5 s of the line
    private const float ProbeSeconds = 6f;       // Window for measuring how big the listeners' responses were
    private const float GapSeconds = 1f;

    private enum Phase { Intro, Preparing, Preview, Listening, Asking, Waiting, Reveal, Failed }

    private static TableReactionBlindTest _instance;
    public static bool Running => _instance != null;

    public static void Begin()
    {
        if (_instance != null) return;
        var go = new GameObject("TableReactionBlindTest") { hideFlags = HideFlags.DontSave };
        _instance = go.AddComponent<TableReactionBlindTest>();
    }

    /// <summary>Stops a run (answers so far are saved) or closes the results.</summary>
    public static void Cancel()
    {
        if (_instance != null) _instance.Stop("cancelled");
    }

    private sealed class ListenerProbe
    {
        public NPCIdentity Npc;
        public Transform Head;
        public NPCLookAt LookAt;
        public NPCFace Face;
        public NPCGestures Gestures;
        public Quaternion Head0;
        public float MaxHeadDegrees;
        public float MaxFaceShift;
        public Transform LastTarget;
        public float LastGestureTime;
        public readonly List<string> Looks = new List<string>();
        public readonly List<string> GesturesPlayed = new List<string>();

        public bool LookedAtPlayer => Looks.Skip(1).Any(l => l.StartsWith("player"));
    }

    private sealed class TrialRecord
    {
        public BlindTrial Trial;
        public TableReactionDirector.Mode Mode;
        public int Session;
        public int Turn = -1;
        public float AnswerSeconds = float.NaN;
        public double ReplyFirstTokenMs = -1;
        public double ReplyTotalMs = -1;
        public JObject Trace;
        public List<ListenerProbe> Probes = new List<ListenerProbe>();
        public string Error;

        /// <summary>The judge + policy asked for at least one visible reaction (null when no judge ran).</summary>
        public bool? ReactionPlanned
        {
            get
            {
                if (Trace?["listeners"] is not JArray listeners) return null;
                foreach (JToken l in listeners)
                {
                    string outcome = (string)l["outcome"] ?? "";
                    if (outcome.StartsWith("executed") || outcome.StartsWith("partial")) return true;
                }
                return false;
            }
        }
    }

    private Phase _phase = Phase.Intro;
    private string _message;
    private DialogueManagerV2 _dm;
    private TableReactionDirector _director;
    private NPCIdentity _wu;
    private readonly List<NPCIdentity> _listeners = new List<NPCIdentity>();
    private List<BlindTrial> _trials;
    private readonly List<TrialRecord> _records = new List<TrialRecord>();
    private TrialRecord _current;
    private bool? _answer;
    private int _seed;
    private string _runId;
    private string _outDir;
    private string _profileSha;
    private string _revealText;
    private bool _saved;
    private bool _scripted;   // Answered by automation: saved, but never pooled with human runs

    // Director/dialogue state to restore
    private bool _directorTouched;
    private TableReactionDirector.Mode _savedMode;
    private bool _savedOverlay;

    private int _lastSubmittedSession, _lastSubmittedTurn = -1;
    private readonly Dictionary<int, DialogueManagerV2.DialogueLineInfo> _replies = new Dictionary<int, DialogueManagerV2.DialogueLineInfo>();

    // ================================================================
    // Lifecycle
    // ================================================================

    private void OnEnable()
    {
        DialogueManagerV2.PlayerUtteranceSubmitted += OnPlayerLine;
        DialogueManagerV2.NpcLineCompleted += OnNpcLine;
    }

    private void OnDisable()
    {
        DialogueManagerV2.PlayerUtteranceSubmitted -= OnPlayerLine;
        DialogueManagerV2.NpcLineCompleted -= OnNpcLine;
    }

    private void OnPlayerLine(DialogueManagerV2.DialogueLineInfo info)
    {
        _lastSubmittedSession = info.Session;
        _lastSubmittedTurn = info.Turn;
    }

    private void OnNpcLine(DialogueManagerV2.DialogueLineInfo info)
    {
        if (info.Session == _lastSubmittedSession || info.Turn == 0) _replies[info.Turn] = info;
    }

    private void Start()
    {
        _dm = FindAnyObjectByType<DialogueManagerV2>();
        _director = FindAnyObjectByType<TableReactionDirector>();
        TableConversation table = FindAnyObjectByType<TableConversation>();
        if (table != null)
            foreach (NPCIdentity npc in table.Members)
            {
                if (npc == null) continue;
                if (npc.npcName == "Master Wu") _wu = npc;
                else _listeners.Add(npc);
            }

        if (_dm == null || _director == null || _wu == null || _listeners.Count == 0)
        {
            Fail("Needs the DialogueManagerV2, a TableReactionDirector and Master Wu's table in the scene.");
            return;
        }
        _message = Intro();
    }

    private static bool? _scriptedKey;

    /// <summary>Automation (smoke runs from a Unity MCP command): acts as if Y (true) or N (false) was pressed.</summary>
    public static void Press(bool yes)
    {
        _scriptedKey = yes;
        if (_instance != null) _instance._scripted = true;
    }

    /// <summary>Automation: "Intro", "Asking", "Reveal", ... and the current on-screen text.</summary>
    public static string State => _instance == null ? "not running" : _instance._phase + ": " + _instance._message;

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        bool yes = kb != null && kb.yKey.wasPressedThisFrame;
        bool no = kb != null && kb.nKey.wasPressedThisFrame;
        if (_scriptedKey.HasValue)
        {
            yes = _scriptedKey.Value;
            no = !_scriptedKey.Value;
            _scriptedKey = null;
        }

        switch (_phase)
        {
            case Phase.Intro:
                if (yes) StartCoroutine(Run());
                break;
            case Phase.Asking:
                if (yes || no) _answer = yes;
                break;
            case Phase.Reveal:
            case Phase.Failed:
                if (yes) Stop("closed");
                break;
        }
    }

    private void OnDestroy()
    {
        Restore();
        if (_instance == this) _instance = null;
    }

    private void Stop(string why)
    {
        if (_phase != Phase.Reveal && _phase != Phase.Failed && _records.Count > 0 && !_saved)
        {
            SaveResults(why);
            Debug.Log($"[TableReactionBlindTest] Stopped ({why}); {_records.Count(r => r.Trial.SaidYes != null)} answers saved to {_outDir}");
        }
        if (_dm != null && DialogueManagerV2.ActiveNPC == _wu && _phase != Phase.Intro) _dm.EndDialogue();
        Destroy(gameObject);
    }

    private void Fail(string message)
    {
        _phase = Phase.Failed;
        _message = "<b>Blind test can't run</b>\n" + message + "\n\n<i>Y or F6 to close</i>";
        Debug.LogWarning("[TableReactionBlindTest] " + message);
        Restore();
    }

    private void Restore()
    {
        if (_dm != null) _dm.SetAutomationInputLock(false);
        if (!_directorTouched || _director == null) return;
        _directorTouched = false;
        _director.SetMode(_savedMode);
        _director.ShowOverlay = _savedOverlay;
        _director.KeepSidecarChecked = false;
        _director.QuietConsole = false;
    }

    // ================================================================
    // The run
    // ================================================================

    private IEnumerator Run()
    {
        _phase = Phase.Preparing;
        _message = "Getting ready…";

        _savedMode = _director.CurrentMode;
        _savedOverlay = _director.ShowOverlay;
        _directorTouched = true;
        _director.QuietConsole = true;
        _director.ShowOverlay = false;
        _director.KeepSidecarChecked = true;
        _profileSha = Sha8(_director.ProfileText ?? "");

        _message = "Waiting for the models…";
        float deadline = Time.realtimeSinceStartup + 180f;
        while ((XybridModelService.Instance == null || !XybridModelService.Instance.IsReady) && Time.realtimeSinceStartup < deadline)
            yield return null;
        if (XybridModelService.Instance == null || !XybridModelService.Instance.IsReady)
        {
            Fail("The dialogue model didn't load.");
            yield break;
        }

        _message = "Checking the d1 sidecar…";
        deadline = Time.realtimeSinceStartup + 8f;
        while (!_director.SidecarReady && Time.realtimeSinceStartup < deadline) yield return null;
        if (!_director.SidecarReady)
        {
            Fail("The d1 sidecar isn't answering. Start it with tools/d1-sidecar/start.sh, then press F6 again.");
            yield break;
        }

        _message = "Starting the conversation with Master Wu…";
        _dm.SetUseAIDialogue(true);
        _dm.SetAutomationInputLock(true);
        if (DialogueManagerV2.ActiveNPC != null)
        {
            _dm.EndDialogue();
            yield return new WaitForSeconds(0.5f);
        }
        TableReactionLiveCheck.PlacePlayerInFrontOf(_wu, 1.4f);
        yield return new WaitForSeconds(0.5f);

        deadline = Time.realtimeSinceStartup + 20f;
        while (!_dm.CanStartDialogue && Time.realtimeSinceStartup < deadline) yield return null;
        _replies.Clear();
        _dm.StartDialogue(_wu);
        int session = DialogueManagerV2.CurrentSession;
        _lastSubmittedSession = session;
        yield return WaitForReply(0, 40f);
        yield return WaitForSpeech(_wu);
        if (DialogueManagerV2.CurrentSession != session || session == 0)
        {
            Fail("The conversation with Master Wu didn't open.");
            yield break;
        }

        _seed = Environment.TickCount;
        _trials = BlindContingency.Plan(Lines, Conditions.Select(c => c.id).ToList(), new System.Random(_seed));
        _runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        _outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "table-reactions-blind", _runId));
        Directory.CreateDirectory(_outDir);
        yield return new WaitForSeconds(GapSeconds);

        foreach (BlindTrial trial in _trials)
        {
            if (DialogueManagerV2.CurrentSession != session)
            {
                Stop("the conversation was closed");
                yield break;
            }
            yield return RunTrial(trial, session);
        }

        SaveResults("finished");
        _dm.EndDialogue();
        _dm.SetAutomationInputLock(false);
        Restore();
        _phase = Phase.Reveal;
        _message = _revealText + "\n\n<i>Y or F6 to close</i>";
        Debug.Log("[TableReactionBlindTest] Finished. Results: " + _outDir);
    }

    private IEnumerator RunTrial(BlindTrial trial, int session)
    {
        var (_, _, mode) = Conditions.First(c => c.id == trial.Condition);
        var record = new TrialRecord { Trial = trial, Mode = mode, Session = session };
        _current = record;
        _records.Add(record);
        _director.SetMode(mode);

        _phase = Phase.Preview;
        _message = Header(trial) + "Next, you say:\n<size=20><b>“" + trial.Line.Text + "”</b></size>";
        yield return new WaitForSeconds(PreviewSeconds);

        float until = Time.realtimeSinceStartup + 10f;
        while (_dm.IsBusy && Time.realtimeSinceStartup < until) yield return null;
        int before = _lastSubmittedTurn;
        if (!_dm.SubmitPlayerLine(trial.Line.Text))
        {
            record.Error = "submit refused";
            AppendTrial(record);
            yield break;
        }
        float t0 = Time.realtimeSinceStartup;
        if (_lastSubmittedTurn != before) record.Turn = _lastSubmittedTurn;
        StartCoroutine(Probe(record, t0));

        _phase = Phase.Listening;
        _message = Header(trial) + "You: <size=20><b>“" + trial.Line.Text + "”</b></size>\n<i>Watch Shara and Varien.</i>";
        while (Time.realtimeSinceStartup - t0 < AskAfterSeconds) yield return null;

        _answer = null;
        _phase = Phase.Asking;
        _message = Header(trial) + "You: “" + trial.Line.Text + "”\n" +
                   "<size=20><b>Did Shara or Varien respond to what you just said?</b></size>\n" +
                   "<b>Y</b> = yes, a look, nod or expression because of that line      <b>N</b> = no";
        while (_answer == null) yield return null;
        trial.SaidYes = _answer;
        record.AnswerSeconds = Time.realtimeSinceStartup - t0;

        _phase = Phase.Waiting;
        _message = Header(trial) + "Answer saved: " + (_answer.Value ? "yes" : "no") + ". Next line in a moment…";
        if (record.Turn > 0) yield return WaitForReply(record.Turn, 40f);
        yield return WaitForSpeech(_wu);
        while (Time.realtimeSinceStartup - t0 < ProbeSeconds + 0.2f) yield return null;

        if (record.Turn > 0 && _replies.TryGetValue(record.Turn, out var reply))
        {
            record.ReplyFirstTokenMs = reply.FirstTokenMs;
            record.ReplyTotalMs = reply.TotalMs;
        }
        record.Trace = record.Turn > 0 ? _director.FindTrace(session, record.Turn)?.ToJson() : null;
        AppendTrial(record);
        yield return new WaitForSeconds(GapSeconds);
    }

    private IEnumerator WaitForReply(int turn, float timeout)
    {
        float until = Time.realtimeSinceStartup + timeout;
        while (!_replies.ContainsKey(turn) && Time.realtimeSinceStartup < until) yield return null;
        yield return null;
    }

    private static IEnumerator WaitForSpeech(NPCIdentity npc)
    {
        NPCFace face = npc.GetComponentInChildren<NPCFace>();
        if (face == null) yield break;
        float until = Time.realtimeSinceStartup + 4f;
        while (!face.IsSpeakingAudio && Time.realtimeSinceStartup < until) yield return null;
        until = Time.realtimeSinceStartup + 25f;
        while (face.IsSpeakingAudio && Time.realtimeSinceStartup < until) yield return null;
    }

    private string Header(BlindTrial trial) => "<b>Blind test</b> · line " + trial.Index + " of " + _trials.Count + "\n";

    // ================================================================
    // Measuring the listeners
    // ================================================================

    private IEnumerator Probe(TrialRecord record, float t0)
    {
        foreach (NPCIdentity npc in _listeners)
        {
            if (npc == null) continue;
            var p = new ListenerProbe
            {
                Npc = npc,
                Head = NPCLookAt.HeadOf(npc),
                Face = npc.GetComponentInChildren<NPCFace>(),
            };
            npc.TryGetComponent(out p.LookAt);
            npc.TryGetComponent(out p.Gestures);
            if (p.Head != null) p.Head0 = Quaternion.Inverse(npc.transform.rotation) * p.Head.rotation;
            p.LastTarget = p.LookAt != null ? p.LookAt.CurrentTarget : null;
            p.Looks.Add(TargetName(p, p.LastTarget) + "@0.0s");
            p.LastGestureTime = p.Gestures != null ? p.Gestures.LastGestureTime : -999f;
            record.Probes.Add(p);
        }

        while (Time.realtimeSinceStartup - t0 < ProbeSeconds)
        {
            yield return new WaitForEndOfFrame();   // After animation, IK and NPCFace's bone writes
            string at = "@" + (Time.realtimeSinceStartup - t0).ToString("0.0") + "s";
            foreach (ListenerProbe p in record.Probes)
            {
                if (p.Npc == null) continue;
                if (p.Head != null)
                {
                    Quaternion now = Quaternion.Inverse(p.Npc.transform.rotation) * p.Head.rotation;
                    p.MaxHeadDegrees = Mathf.Max(p.MaxHeadDegrees, Quaternion.Angle(p.Head0, now));
                }
                if (p.Face != null) p.MaxFaceShift = Mathf.Max(p.MaxFaceShift, p.Face.ExpressionShiftFromBase());
                if (p.LookAt != null)
                {
                    Transform target = p.LookAt.CurrentTarget;
                    if (target != p.LastTarget)
                    {
                        p.LastTarget = target;
                        p.Looks.Add(TargetName(p, target) + at);
                    }
                }
                if (p.Gestures != null && p.Gestures.LastGestureTime > p.LastGestureTime)
                {
                    p.LastGestureTime = p.Gestures.LastGestureTime;
                    p.GesturesPlayed.Add(p.Gestures.LastTrigger + at);
                }
            }
        }
    }

    private string TargetName(ListenerProbe p, Transform target)
    {
        if (target == null) return "nothing";
        if (p.LookAt != null && target == p.LookAt.PlayerEyes) return "player";
        if (target == NPCLookAt.HeadOf(_wu)) return _wu.npcName;
        foreach (NPCIdentity npc in _listeners)
            if (npc != null && target == NPCLookAt.HeadOf(npc)) return npc.npcName;
        return target.name;
    }

    // ================================================================
    // Results
    // ================================================================

    private JObject TrialJson(TrialRecord r)
    {
        var listeners = new JObject();
        foreach (ListenerProbe p in r.Probes)
        {
            if (p.Npc == null) continue;
            listeners[p.Npc.npcName] = new JObject
            {
                ["head_turn_max_deg"] = Math.Round(p.MaxHeadDegrees, 1),
                ["face_shift_max_pct"] = Math.Round(p.MaxFaceShift * 100f, 1),
                ["looked_at_player"] = p.LookedAtPlayer,
                ["looks"] = new JArray(p.Looks),
                ["gestures"] = new JArray(p.GesturesPlayed),
            };
        }
        return new JObject
        {
            ["type"] = "trial",
            ["run"] = _runId,
            ["index"] = r.Trial.Index,
            ["condition"] = r.Trial.Condition,
            ["mode"] = r.Mode.ToString(),
            ["kind"] = r.Trial.Line.Kind == BlindLineKind.AboutSomeone ? "about_someone" : "small_talk",
            ["line"] = r.Trial.Line.Text,
            ["said_yes"] = r.Trial.SaidYes.HasValue ? (JToken)r.Trial.SaidYes.Value : JValue.CreateNull(),
            ["answer_s"] = float.IsNaN(r.AnswerSeconds) ? JValue.CreateNull() : (JToken)Math.Round(r.AnswerSeconds, 2),
            ["session"] = r.Session,
            ["turn"] = r.Turn,
            ["reply_first_token_ms"] = Math.Round(r.ReplyFirstTokenMs, 1),
            ["reply_total_ms"] = Math.Round(r.ReplyTotalMs, 1),
            ["reaction_planned"] = r.ReactionPlanned.HasValue ? (JToken)r.ReactionPlanned.Value : JValue.CreateNull(),
            ["listeners"] = listeners,
            ["reaction_trace"] = r.Trace,
            ["error"] = r.Error,
        };
    }

    private void AppendTrial(TrialRecord r)
    {
        try
        {
            File.AppendAllText(Path.Combine(_outDir, "trials.jsonl"), TrialJson(r).ToString(Formatting.None) + "\n");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TableReactionBlindTest] Could not write trials.jsonl: " + ex.Message);
        }
    }

    private void SaveResults(string status)
    {
        if (_saved || _outDir == null) return;
        _saved = true;
        List<string> order = Conditions.Select(c => c.id).ToList();
        List<ConditionScore> scores = BlindContingency.Score(_trials, order);

        var results = new JObject
        {
            ["format"] = "tavern.blind-reaction-test.v1",
            ["run"] = _runId,
            ["status"] = status,
            ["utc"] = DateTime.UtcNow.ToString("o"),
            ["reaction_style"] = ReactionStyle,
            ["policy_sha8"] = _profileSha,
            ["seed"] = _seed,
            ["scripted_answers"] = _scripted,
            ["unity"] = Application.unityVersion,
            ["trials"] = new JArray(_records.Select(r => new JObject
            {
                ["condition"] = r.Trial.Condition,
                ["kind"] = r.Trial.Line.Kind == BlindLineKind.AboutSomeone ? "about_someone" : "small_talk",
                ["line"] = r.Trial.Line.Text,
                ["said_yes"] = r.Trial.SaidYes.HasValue ? (JToken)r.Trial.SaidYes.Value : JValue.CreateNull(),
            })),
            ["scores"] = new JArray(scores.Select(ScoreJson)),
        };

        List<ConditionScore> pooled = PooledScores(out int runs, out int pooledTrials);
        results["pooled"] = new JObject
        {
            ["runs"] = runs,
            ["trials"] = pooledTrials,
            ["scores"] = new JArray(pooled.Select(ScoreJson)),
        };

        string summary = Summary(status, scores, pooled, runs, pooledTrials);
        try
        {
            File.WriteAllText(Path.Combine(_outDir, "results.json"), results.ToString(Formatting.Indented));
            File.WriteAllText(Path.Combine(_outDir, "summary.md"), summary);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TableReactionBlindTest] Could not write results: " + ex.Message);
        }
        _revealText = RevealText(scores, pooled, runs, pooledTrials);
    }

    private static JObject ScoreJson(ConditionScore s) => new JObject
    {
        ["condition"] = s.Condition,
        ["about_trials"] = s.AboutTrials,
        ["hits"] = s.Hits,
        ["small_talk_trials"] = s.SmallTalkTrials,
        ["false_alarms"] = s.FalseAlarms,
        ["contingency"] = double.IsNaN(s.Contingency) ? JValue.CreateNull() : (JToken)Math.Round(s.Contingency, 3),
    };

    /// <summary>All finished runs in the output folder with the same reaction style and policy, this one included.</summary>
    private List<ConditionScore> PooledScores(out int runs, out int trialCount)
    {
        var trials = new List<BlindTrial>();
        runs = 0;
        string root = Path.GetDirectoryName(_outDir);
        foreach (string dir in Directory.GetDirectories(root))
        {
            string file = Path.Combine(dir, "results.json");
            JObject o;
            if (Path.GetFullPath(dir) == Path.GetFullPath(_outDir))
            {
                if (_scripted) continue;
                trials.AddRange(_trials);
                runs++;
                continue;
            }
            if (!File.Exists(file)) continue;
            try { o = JObject.Parse(File.ReadAllText(file)); }
            catch (Exception) { continue; }
            if ((string)o["reaction_style"] != ReactionStyle || (string)o["policy_sha8"] != _profileSha) continue;
            if ((bool?)o["scripted_answers"] == true) continue;
            runs++;
            foreach (JToken t in (o["trials"] as JArray) ?? new JArray())
            {
                JToken yes = t["said_yes"];
                trials.Add(new BlindTrial
                {
                    Condition = (string)t["condition"],
                    Line = new BlindLine((string)t["line"] ?? "?",
                        (string)t["kind"] == "about_someone" ? BlindLineKind.AboutSomeone : BlindLineKind.SmallTalk),
                    SaidYes = yes == null || yes.Type == JTokenType.Null ? (bool?)null : (bool)yes,
                });
            }
        }
        trialCount = trials.Count(t => t.SaidYes != null);
        return BlindContingency.Score(trials, Conditions.Select(c => c.id).ToList());
    }

    private static string Label(string id) => Conditions.FirstOrDefault(c => c.id == id).label ?? id;

    private static string Points(double contingency) =>
        double.IsNaN(contingency) ? "n/a" : (contingency >= 0 ? "+" : "") + Math.Round(contingency * 100).ToString("0");

    /// <summary>Per condition and line kind: average of the biggest listener response per line.</summary>
    private IEnumerable<string> SizeLines()
    {
        foreach (var c in Conditions)
            foreach (BlindLineKind kind in new[] { BlindLineKind.AboutSomeone, BlindLineKind.SmallTalk })
            {
                List<TrialRecord> rs = _records.Where(r => r.Trial.Condition == c.id && r.Trial.Line.Kind == kind && r.Probes.Count > 0).ToList();
                if (rs.Count == 0) continue;
                double head = rs.Average(r => r.Probes.Max(p => p.MaxHeadDegrees));
                double face = rs.Average(r => r.Probes.Max(p => p.MaxFaceShift)) * 100;
                int looked = rs.Count(r => r.Probes.Any(p => p.LookedAtPlayer));
                int gestured = rs.Count(r => r.Probes.Any(p => p.GesturesPlayed.Count > 0));
                int planned = rs.Count(r => r.ReactionPlanned == true);
                string kindText = kind == BlindLineKind.AboutSomeone ? "remarks" : "small talk";
                string line = $"{c.label}, {kindText}: head turn {head:0}°, face change {face:0}%, looked at you {looked}/{rs.Count}, gesture {gestured}/{rs.Count}";
                if (c.mode != TableReactionDirector.Mode.Off) line += $", reaction planned {planned}/{rs.Count}";
                yield return line;
            }
    }

    private string RevealText(List<ConditionScore> scores, List<ConditionScore> pooled, int runs, int pooledTrials)
    {
        var sb = new StringBuilder();
        sb.Append("<b>Blind test results</b> · ").Append(_records.Count(r => r.Trial.SaidYes != null)).Append(" answers\n");
        foreach (ConditionScore s in scores)
        {
            sb.Append("\n<b>").Append(Label(s.Condition)).Append("</b>: you said yes after ")
              .Append(s.Hits).Append(" of ").Append(s.AboutTrials).Append(" remarks and ")
              .Append(s.FalseAlarms).Append(" of ").Append(s.SmallTalkTrials).Append(" small-talk lines  ⇒  <b>score ")
              .Append(Points(s.Contingency)).Append("</b>");
        }
        sb.Append("\n<i>Score = yes after remarks minus yes after small talk. 0 = reactions look random; +100 = always tied to what you said.</i>");
        sb.Append("\n\n<size=13>");
        foreach (string line in SizeLines()) sb.Append(line).Append('\n');
        sb.Append("</size>");
        if (runs > 1)
        {
            sb.Append("\nAll ").Append(runs).Append(" runs with these reactions (").Append(pooledTrials).Append(" answers): ");
            sb.Append(string.Join(" · ", pooled.Select(s => Label(s.Condition) + " " + Points(s.Contingency))));
        }
        sb.Append("\nSaved to Logs/table-reactions-blind/").Append(_runId);
        return sb.ToString();
    }

    private string Summary(string status, List<ConditionScore> scores, List<ConditionScore> pooled, int runs, int pooledTrials)
    {
        var sb = new StringBuilder();
        sb.Append("# Blind reaction test ").Append(_runId).Append("\n\n");
        if (_scripted) sb.Append("**Scripted answers (automation smoke run): not a human rating, never pooled.**\n\n");
        sb.Append("Status: ").Append(status).Append(". Reaction style `").Append(ReactionStyle).Append("`, policy sha `")
          .Append(_profileSha).Append("`, seed ").Append(_seed).Append(".\n\n");
        sb.Append("| condition | yes after remarks | yes after small talk | score |\n| --- | --- | --- | --- |\n");
        foreach (ConditionScore s in scores)
            sb.Append("| ").Append(Label(s.Condition)).Append(" | ").Append(s.Hits).Append('/').Append(s.AboutTrials)
              .Append(" | ").Append(s.FalseAlarms).Append('/').Append(s.SmallTalkTrials).Append(" | ").Append(Points(s.Contingency)).Append(" |\n");
        sb.Append("\nScore = yes-rate after remarks about someone minus yes-rate after small talk, in points (-100 to +100).\n\n");
        sb.Append("## Size of the listeners' responses (6 s after each line, biggest listener per line, averaged)\n\n");
        foreach (string line in SizeLines()) sb.Append("- ").Append(line).Append('\n');
        if (runs > 1)
        {
            sb.Append("\n## Pooled: ").Append(runs).Append(" runs, ").Append(pooledTrials).Append(" answers, same style and policy\n\n");
            foreach (ConditionScore s in pooled)
                sb.Append("- ").Append(Label(s.Condition)).Append(": ").Append(s.Hits).Append('/').Append(s.AboutTrials)
                  .Append(" remarks, ").Append(s.FalseAlarms).Append('/').Append(s.SmallTalkTrials).Append(" small talk, score ")
                  .Append(Points(s.Contingency)).Append('\n');
        }
        sb.Append("\n## Lines\n\n| # | condition | kind | line | answer | planned |\n| --- | --- | --- | --- | --- | --- |\n");
        foreach (TrialRecord r in _records)
            sb.Append("| ").Append(r.Trial.Index).Append(" | ").Append(r.Trial.Condition).Append(" | ")
              .Append(r.Trial.Line.Kind == BlindLineKind.AboutSomeone ? "remark" : "small talk").Append(" | ")
              .Append(r.Trial.Line.Text).Append(" | ")
              .Append(r.Trial.SaidYes == null ? "-" : r.Trial.SaidYes.Value ? "yes" : "no").Append(" | ")
              .Append(r.ReactionPlanned == null ? "-" : r.ReactionPlanned.Value ? "yes" : "no").Append(" |\n");
        return sb.ToString();
    }

    private static string Sha8(string text)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").Substring(0, 8).ToLowerInvariant();
    }

    // ================================================================
    // Screen
    // ================================================================

    private static string Intro() =>
        "<b>Blind reaction test</b> · 16 lines, about 4 minutes\n" +
        "I'll say each line to Master Wu for you. Watch his students, <b>Shara</b> and <b>Varien</b>.\n" +
        "After each line: <b>Y</b> if one of them responded to what you said (a look, nod or expression), <b>N</b> if not.\n" +
        "They also glance around at random, so answer whether it seemed caused by your line. The mode stays hidden until the end.\n\n" +
        "<b>Y</b> to start · <b>F6</b> to cancel   <i>(Tip: maximize the Game view so the Console is out of sight.)</i>";

    private GUIStyle _style;

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(_message)) return;
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 15,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(14, 14, 10, 10),
            };
            _style.normal.textColor = new Color(0.91f, 0.86f, 0.77f);
        }
        float width = Mathf.Min(820f, Screen.width - 40f);
        var content = new GUIContent(_message);
        float height = Mathf.Min(_style.CalcHeight(content, width), Screen.height - 40f);
        GUI.Box(new Rect((Screen.width - width) * 0.5f, 16f, width, height), content, _style);
    }
}
