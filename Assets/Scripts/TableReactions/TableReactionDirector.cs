using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Tavern.Dialogue;
using Tavern.Social;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// EXPERIMENT: when the player talks to one member of a <see cref="TableConversation"/>, the other members who
/// can hear it may react nonverbally to what the line means (who it's about, praise/criticism/concern).
///
/// Unity decides who can hear (table membership + distance). A judge interprets meaning with closed labels:
/// rules, the d1-2B decision model in a local llama.cpp sidecar (dev only, not Xybrid), or the dialogue model
/// as a grammar-constrained baseline. The authored policy in Resources/TableReactions picks at most one brief
/// reaction per listener; <see cref="ListenerReactionPerformer"/> plays it through NPCLookAt/NPCGestures/NPCFace.
/// Judgments for closed sessions, superseded turns or that arrive too late are discarded (<see cref="ReactionGate"/>).
///
/// F3 cycles the mode, F4 toggles the debug overlay, F6 starts or stops the blind reaction test
/// (<see cref="TableReactionBlindTest"/>). Traces go to the Console ([TableReactions]) and to
/// Logs/table-reactions.jsonl. Put it next to the TableConversation.
/// </summary>
[DisallowMultipleComponent]
public class TableReactionDirector : MonoBehaviour
{
    public enum Mode { Off, Rules, D1Sidecar, GemmaBaseline }

    [Header("Table")]
    [Tooltip("The table whose members react. Defaults to the TableConversation on this GameObject.")]
    [SerializeField] private TableConversation table;
    [Tooltip("Members, persona facts and the reaction policy. Defaults to Resources/TableReactions/wu_table.")]
    [SerializeField] private TextAsset profileJson;

    [Header("Experiment")]
    [Tooltip("Off = existing generic watcher only. Rules = keyword baseline. D1 Sidecar = d1-2B decision model " +
             "(tools/d1-sidecar/start.sh). Gemma Baseline = dialogue model, grammar-constrained, after the reply.")]
    [SerializeField] private Mode mode = Mode.Off;
    [Tooltip("Members farther than this from the player (head to eyes, metres) don't hear the line.")]
    [Min(0.5f)] [SerializeField] private float hearingRadius = 3.5f;
    [Tooltip("A judgment older than this (seconds after the line) is discarded instead of acted on.")]
    [Min(0.5f)] [SerializeField] private float maxJudgmentAgeSeconds = 8f;
    [Tooltip("Use the keyword rules when the selected judge fails (labelled 'fallback' in traces).")]
    [SerializeField] private bool rulesOnJudgeFailure = false;
    [Tooltip("0 = different every run.")]
    [SerializeField] private int randomSeed = 0;

    [Header("d1 sidecar (dev only)")]
    [SerializeField] private string sidecarUrl = "http://127.0.0.1:8187";
    [Min(0.2f)] [SerializeField] private float sidecarTimeoutSeconds = 2.5f;

    [Header("Debug")]
    [SerializeField] private bool showOverlay = true;
    [SerializeField] private bool writeTraceFile = true;

    private TableProfile _profile;
    private ReactionPolicy _policy;
    private ReactionGate _gate;
    private readonly ConversationTranscript _transcript = new ConversationTranscript(8);
    private readonly Dictionary<string, NPCIdentity> _members = new Dictionary<string, NPCIdentity>();
    private readonly List<ReactionTrace> _recent = new List<ReactionTrace>();

    private RuleListenerJudge _rules;
    private D1SystemOneJudge _d1;
    private GemmaStructuredJudge _gemma;
    private XybridGrammarRunner _gemmaRunner;
    private bool _d1Healthy;
    private bool _healthCheckRunning;
    private float _nextHealthCheck;

    private int _openSession;
    private string _addresseeId;
    private (ReactionTicket ticket, ReactionTrace trace) _awaitingReply;   // Gemma waits for the reply
    private ReactionTrace _pendingTrace;
    private string _tracePath;
    private string _loadError;

    public Mode CurrentMode => mode;
    public IReadOnlyList<ReactionTrace> RecentTraces => _recent;

    /// <summary>Raw text of the profile in use (members + policy), for tagging test runs.</summary>
    public string ProfileText { get; private set; }

    public bool ShowOverlay
    {
        get => showOverlay;
        set => showOverlay = value;
    }

    /// <summary>Blind testing: no Console lines that would reveal the mode (traces still go to the JSONL file).</summary>
    public bool QuietConsole { get; set; }

    /// <summary>Keep probing the d1 sidecar even while another mode is selected (the blind test switches per line).</summary>
    public bool KeepSidecarChecked { get; set; }

    public bool SidecarReady => _d1Healthy;

    /// <summary>The trace of a player line, while it is among the last few.</summary>
    public ReactionTrace FindTrace(int session, int turn)
    {
        for (int i = _recent.Count - 1; i >= 0; i--)
            if (_recent[i].SessionId == session && _recent[i].TurnId == turn) return _recent[i];
        return null;
    }

    // ================================================================
    // Lifecycle
    // ================================================================

    private void Awake()
    {
        if (table == null) table = GetComponent<TableConversation>();
        _gate = new ReactionGate(() => Time.realtimeSinceStartup, maxJudgmentAgeSeconds);
        _rules = new RuleListenerJudge();
        _d1 = new D1SystemOneJudge(new HttpSystemOneTransport(sidecarUrl, TimeSpan.FromSeconds(sidecarTimeoutSeconds)));
        _tracePath = Path.Combine(Application.isEditor ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"))
            : Application.persistentDataPath, "table-reactions.jsonl");
        LoadProfile();
    }

    private void OnEnable()
    {
        DialogueManagerV2.DialogueStarted += OnDialogueStarted;
        DialogueManagerV2.DialogueEnded += OnDialogueEnded;
        DialogueManagerV2.PlayerUtteranceSubmitted += OnPlayerUtterance;
        DialogueManagerV2.NpcLineCompleted += OnNpcLine;
    }

    private void OnDisable()
    {
        DialogueManagerV2.DialogueStarted -= OnDialogueStarted;
        DialogueManagerV2.DialogueEnded -= OnDialogueEnded;
        DialogueManagerV2.PlayerUtteranceSubmitted -= OnPlayerUtterance;
        DialogueManagerV2.NpcLineCompleted -= OnNpcLine;
        CloseSession("component disabled");
    }

    private void OnDestroy()
    {
        _gemmaRunner?.Dispose();
    }

    private void LoadProfile()
    {
        try
        {
            TextAsset asset = profileJson != null ? profileJson : Resources.Load<TextAsset>("TableReactions/wu_table");
            if (asset == null) throw new FileNotFoundException("Resources/TableReactions/wu_table.json");
            _profile = TableProfile.Parse(asset.text, Enum.GetNames(typeof(FaceEmotion)));
            ProfileText = asset.text;
            _policy = new ReactionPolicy(_profile, randomSeed != 0 ? new System.Random(randomSeed) : new System.Random());
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            Debug.LogError($"[TableReactions] Profile failed to load, reactions disabled: {ex.Message}", this);
            return;
        }

        if (table == null) return;
        foreach (NPCIdentity npc in table.Members)
        {
            if (npc == null) continue;
            TableMember member = _profile.FindByNpcName(npc.npcName);
            if (member != null) _members[member.Id] = npc;
            else Debug.LogWarning($"[TableReactions] {npc.npcName} sits at {table.name} but has no profile entry; it won't react.", this);
        }
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.f6Key.wasPressedThisFrame)
        {
            if (TableReactionBlindTest.Running) TableReactionBlindTest.Cancel();
            else TableReactionBlindTest.Begin();
        }
        // Mode and overlay stay hidden and fixed during a blind run
        if (!TableReactionBlindTest.Running)
        {
            if (kb != null && kb.f3Key.wasPressedThisFrame) SetMode((Mode)(((int)mode + 1) % 4));
            if (kb != null && kb.f4Key.wasPressedThisFrame) showOverlay = !showOverlay;
        }

        if ((mode == Mode.D1Sidecar || KeepSidecarChecked) && !_healthCheckRunning && Time.realtimeSinceStartup >= _nextHealthCheck)
            _ = CheckSidecarAsync();
    }

    public void SetMode(Mode newMode)
    {
        if (newMode == mode) return;
        mode = newMode;
        if (!QuietConsole) Debug.Log($"[TableReactions] Mode: {mode} ({JudgeLabel()})", this);
        _nextHealthCheck = 0f;
    }

    private async Task CheckSidecarAsync()
    {
        _healthCheckRunning = true;
        try
        {
            bool healthy = await _d1.IsHealthyAsync(CancellationToken.None);
            if (healthy != _d1Healthy && !QuietConsole)
                Debug.Log($"[TableReactions] d1 sidecar at {sidecarUrl} is {(healthy ? "ready" : "unavailable")}.", this);
            _d1Healthy = healthy;
        }
        finally
        {
            _healthCheckRunning = false;
            _nextHealthCheck = Time.realtimeSinceStartup + (_d1Healthy ? 10f : 3f);
        }
    }

    // ================================================================
    // Dialogue events
    // ================================================================

    private string IdOf(NPCIdentity npc)
    {
        if (npc == null || _profile == null) return null;
        foreach (var kv in _members)
            if (kv.Value == npc) return kv.Key;
        return null;
    }

    private void OnDialogueStarted(NPCIdentity npc)
    {
        string id = IdOf(npc);
        if (id == null)
        {
            CloseSession("dialogue moved elsewhere");
            return;
        }
        _openSession = DialogueManagerV2.CurrentSession;
        _addresseeId = id;
        _transcript.Clear();
        _gate.MaxAgeSeconds = maxJudgmentAgeSeconds;
        _gate.OpenSession(_openSession);
    }

    private void OnDialogueEnded(NPCIdentity npc)
    {
        if (IdOf(npc) != null) CloseSession("dialogue closed");
    }

    private void CloseSession(string why)
    {
        if (_openSession == 0) return;
        _gate.CloseSession(_openSession);
        DiscardPending("discarded: " + why);
        _awaitingReply = default;
        _openSession = 0;
        _addresseeId = null;
        _transcript.Clear();
    }

    private void OnNpcLine(DialogueManagerV2.DialogueLineInfo info)
    {
        if (info.Session != _openSession || _openSession == 0) return;
        string speaker = IdOf(info.Npc);
        if (info.Success && speaker != null) _transcript.Add(speaker, info.Text);

        if (info.Turn > 0)
        {
            WriteJsonLine(new JObject
            {
                ["type"] = "reply",
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["mode"] = mode.ToString(),
                ["session"] = info.Session,
                ["turn"] = info.Turn,
                ["success"] = info.Success,
                ["first_token_ms"] = Math.Round(info.FirstTokenMs, 1),
                ["total_ms"] = Math.Round(info.TotalMs, 1),
            });
        }

        // The Gemma baseline shares the dialogue model, so it only starts once this turn's reply is done
        if (_awaitingReply.ticket != null && _awaitingReply.ticket.Event.TurnId == info.Turn)
        {
            var (ticket, trace) = _awaitingReply;
            _awaitingReply = default;
            if (_gemma != null) RunJudge(_gemma, ticket, trace);
        }
    }

    private void OnPlayerUtterance(DialogueManagerV2.DialogueLineInfo info)
    {
        if (_profile == null || info.Session != _openSession || _openSession == 0) return;
        string addressee = IdOf(info.Npc);
        if (addressee == null) return;

        List<DialogueLine> recent = _transcript.Snapshot(UtteranceEvent.MaxRecentLines);
        _transcript.Add(SocialIds.Player, info.Text);
        if (mode == Mode.Off) return;

        List<string> eligible = HearingEligibility.Eligible(BuildCandidates(), SocialIds.Player, addressee, hearingRadius);
        UtteranceEvent e;
        try
        {
            e = UtteranceEvent.Create(info.Session, info.Turn, SpeakerKind.Player, SocialIds.Player, addressee, info.Text,
                recent, info.Realtime, DateTime.UtcNow, eligible);
        }
        catch (ArgumentException ex)
        {
            Debug.LogWarning($"[TableReactions] Utterance not published: {ex.Message}", this);
            return;
        }

        var trace = new ReactionTrace
        {
            EventKey = e.Key,
            SessionId = e.SessionId,
            TurnId = e.TurnId,
            Utterance = e.Text,
            AddressedId = addressee,
            Eligible = new List<string>(e.EligibleListenerIds),
            Mode = mode.ToString(),
            ModelLabel = JudgeLabel(),
            UtcTimestamp = e.UtcTimestamp,
        };
        Remember(trace);

        DiscardPending("discarded: superseded by T" + e.TurnId);

        if (eligible.Count == 0)
        {
            trace.JudgeStatus = "skipped: nobody in earshot";
            Flush(trace);
            return;
        }

        ReactionTicket ticket = _gate.Submit(e, out string reject);
        if (ticket == null)
        {
            trace.JudgeStatus = "rejected: " + reject;
            Flush(trace);
            return;
        }
        _pendingTrace = trace;

        switch (mode)
        {
            case Mode.Rules:
                RunJudge(_rules, ticket, trace);
                break;
            case Mode.D1Sidecar:
                if (_d1Healthy) RunJudge(_d1, ticket, trace);
                else Fail(ticket, trace, "d1 sidecar unavailable at " + sidecarUrl);
                break;
            case Mode.GemmaBaseline:
                if (EnsureGemma())
                {
                    trace.JudgeStatus = WaitingForReply;
                    _awaitingReply = (ticket, trace);
                }
                else
                {
                    Fail(ticket, trace, "XybridModelService not ready");
                }
                break;
        }
    }

    private const string WaitingForReply = "waiting for reply";

    /// <summary>
    /// Marks the pending event as discarded. A judge that is still running logs it when it returns (with its
    /// timings); an event still waiting for its reply is logged now.
    /// </summary>
    private void DiscardPending(string status)
    {
        ReactionTrace trace = _pendingTrace;
        _pendingTrace = null;
        _awaitingReply = default;
        if (trace == null) return;
        if (trace.JudgeStatus == WaitingForReply)
        {
            trace.JudgeStatus = status;
            Flush(trace);
        }
        else if (trace.JudgeStatus == "pending")
        {
            trace.JudgeStatus = status;
        }
    }

    private bool EnsureGemma()
    {
        XybridModelService service = XybridModelService.Instance;
        if (service == null || !service.IsReady) return false;
        if (_gemma == null)
        {
            _gemmaRunner = new XybridGrammarRunner(service);
            _gemma = new GemmaStructuredJudge(_gemmaRunner);
        }
        return true;
    }

    private List<ListenerCandidate> BuildCandidates()
    {
        var candidates = new List<ListenerCandidate>();
        Transform ears = PlayerEyes();
        foreach (var kv in _members)
        {
            NPCIdentity npc = kv.Value;
            bool alive = npc != null && npc.isActiveAndEnabled;
            NPCFace face = alive ? npc.GetComponentInChildren<NPCFace>() : null;
            Transform head = alive ? NPCLookAt.HeadOf(npc) : null;
            candidates.Add(new ListenerCandidate
            {
                Id = kv.Key,
                IsTableMember = true,
                IsAlive = alive,
                IsSpeaking = face != null && face.IsSpeakingAudio,
                DistanceToSpeaker = head != null && ears != null ? Vector3.Distance(head.position, ears.position) : float.NaN,
            });
        }
        return candidates;
    }

    // ================================================================
    // Judging and acting
    // ================================================================

    private async void RunJudge(IListenerJudge judge, ReactionTicket ticket, ReactionTrace trace)
    {
        trace.JudgeId = judge.JudgeId;
        trace.ModelLabel = judge.ModelLabel;
        if (trace.JudgeStatus == WaitingForReply) trace.JudgeStatus = "pending";
        JudgeOutcome outcome;
        try
        {
            outcome = await judge.JudgeAsync(new JudgeRequest(ticket.Event, _profile), ticket.Token);
        }
        catch (OperationCanceledException)
        {
            if (trace.JudgeStatus == "pending") trace.JudgeStatus = Describe(_gate.Check(ticket, false)) + " (cancelled)";
            else trace.JudgeStatus += " (cancelled)";
            _gate.Release(ticket);
            if (ReferenceEquals(_pendingTrace, trace)) _pendingTrace = null;
            Flush(trace);
            return;
        }
        catch (Exception ex)
        {
            outcome = JudgeOutcome.Failed(judge.JudgeId, judge.ModelLabel, ex.GetType().Name + ": " + ex.Message, null, 0);
        }

        if (this == null) return;   // Destroyed while waiting
        trace.ApplyOutcome(outcome);
        trace.ArrivalSeconds = Time.realtimeSinceStartup - ticket.Event.RealtimeSeconds;

        TicketVerdict verdict = _gate.Check(ticket);
        if (verdict != TicketVerdict.Accept)
        {
            if (trace.JudgeStatus == "pending") trace.JudgeStatus = Describe(verdict);
            trace.JudgeStatus += " (arrived +" + trace.ArrivalSeconds.ToString("0.00") + " s)";
            _gate.Release(ticket);
            if (ReferenceEquals(_pendingTrace, trace)) _pendingTrace = null;
            Flush(trace);
            return;
        }

        if (!outcome.Success)
        {
            if (rulesOnJudgeFailure && judge != _rules)
            {
                JudgeOutcome fallback = await _rules.JudgeAsync(new JudgeRequest(ticket.Event, _profile), CancellationToken.None);
                string failure = outcome.Failure;
                trace.ApplyOutcome(fallback);
                trace.Failure = failure;
                trace.JudgeStatus = "fallback: rules";
                outcome = fallback;
            }
            else
            {
                trace.JudgeStatus = "failed";
                _gate.Release(ticket);
                if (ReferenceEquals(_pendingTrace, trace)) _pendingTrace = null;
                Flush(trace);
                return;
            }
        }
        else
        {
            trace.JudgeStatus = "accepted";
        }

        _gate.Release(ticket);
        if (ReferenceEquals(_pendingTrace, trace)) _pendingTrace = null;
        Act(ticket, trace, outcome.Judgment);
    }

    private void Fail(ReactionTicket ticket, ReactionTrace trace, string reason)
    {
        trace.JudgeStatus = "failed";
        trace.Failure = reason;
        _gate.Release(ticket);
        if (ReferenceEquals(_pendingTrace, trace)) _pendingTrace = null;
        Flush(trace);
    }

    private void Act(ReactionTicket ticket, ReactionTrace trace, EventJudgment judgment)
    {
        List<ReactionPlan> plans = _policy.Plan(ticket.Event, judgment, Time.time);
        int running = 0;
        foreach (ReactionPlan plan in plans)
        {
            ListenerTrace lt = trace.Listener(plan.ListenerId);
            lt.Requested = plan.Describe();
            if (plan.IsNone)
            {
                lt.Outcome = "none";
                continue;
            }

            running++;
            _members.TryGetValue(plan.ListenerId, out NPCIdentity npc);
            StartCoroutine(ListenerReactionPerformer.Perform(plan, npc, ResolveTarget,
                () => StillValid(ticket, plan.ListenerId), lt, table,
                () => { if (--running == 0) Flush(trace); }));
        }
        if (running == 0) Flush(trace);
    }

    private string StillValid(ReactionTicket ticket, string listenerId)
    {
        TicketVerdict v = _gate.CheckListener(ticket, listenerId, id => _members.TryGetValue(id, out NPCIdentity n) && n != null && n.isActiveAndEnabled);
        return v == TicketVerdict.Accept ? null : Describe(v);
    }

    private Transform ResolveTarget(string id)
    {
        if (id == SocialIds.Player) return PlayerEyes();
        return _members.TryGetValue(id ?? "", out NPCIdentity npc) && npc != null ? NPCLookAt.HeadOf(npc) : null;
    }

    private Transform PlayerEyes()
    {
        foreach (NPCIdentity npc in _members.Values)
            if (npc != null && npc.TryGetComponent(out NPCLookAt lookAt) && lookAt.PlayerEyes != null)
                return lookAt.PlayerEyes;
        Camera cam = Camera.main;
        return cam != null ? cam.transform : null;
    }

    private static string Describe(TicketVerdict v)
    {
        switch (v)
        {
            case TicketVerdict.Superseded: return "discarded: superseded";
            case TicketVerdict.SessionClosed: return "discarded: session closed";
            case TicketVerdict.Expired: return "discarded: expired";
            case TicketVerdict.ListenerGone: return "discarded: listener gone";
            default: return "accepted";
        }
    }

    private string JudgeLabel()
    {
        switch (mode)
        {
            case Mode.Rules: return _rules.ModelLabel;
            case Mode.D1Sidecar: return _d1.ModelLabel;
            case Mode.GemmaBaseline:
                XybridModelService s = XybridModelService.Instance;
                return (s != null ? s.ModelId : "dialogue LLM") + " structured-judgment baseline (generative, grammar-constrained)";
            default: return "generic watcher only";
        }
    }

    // ================================================================
    // Observability
    // ================================================================

    private void Remember(ReactionTrace trace)
    {
        _recent.Add(trace);
        if (_recent.Count > 6) _recent.RemoveAt(0);
    }

    private void Flush(ReactionTrace trace)
    {
        if (trace.Flushed) return;
        trace.Flushed = true;
        if (!QuietConsole) Debug.Log("[TableReactions] " + trace.CompactLine(), this);
        if (writeTraceFile)
        {
            JObject json = trace.ToJson();
            json["type"] = "reaction";
            WriteJsonLine(json);
        }
    }

    private void WriteJsonLine(JObject json)
    {
        if (!writeTraceFile) return;
        try
        {
            File.AppendAllText(_tracePath, json.ToString(Newtonsoft.Json.Formatting.None) + "\n");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TableReactions] Could not write {_tracePath}: {ex.Message}", this);
            writeTraceFile = false;
        }
    }

    private GUIStyle _style;

    private void OnGUI()
    {
        if (!showOverlay) return;
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 12,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(8, 8, 6, 6),
            };
            _style.normal.textColor = new Color(0.91f, 0.86f, 0.77f);
        }

        var sb = new StringBuilder();
        sb.Append("<b>Table reactions</b> — ").Append(mode).Append(" · ").Append(JudgeLabel());
        if (mode == Mode.D1Sidecar) sb.Append(_d1Healthy ? " · sidecar ready" : " · <color=#E08A6A>sidecar unavailable</color>");
        if (_loadError != null) sb.Append("\n<color=#E08A6A>").Append(_loadError).Append("</color>");
        sb.Append("   <i>F3 mode · F4 hide</i>");

        for (int i = _recent.Count - 1; i >= Math.Max(0, _recent.Count - 3); i--)
        {
            ReactionTrace t = _recent[i];
            sb.Append("\n\n<b>S").Append(t.SessionId).Append(" T").Append(t.TurnId).Append("</b> \"").Append(t.Utterance).Append("\"");
            sb.Append("\n  hears: ").Append(t.Eligible.Count > 0 ? string.Join(", ", t.Eligible) : "nobody");
            sb.Append(" · ").Append(t.JudgeId ?? t.Mode).Append(" ").Append(t.JudgeStatus);
            if (t.TotalMs > 0)
                sb.Append(" · wait ").Append(t.QueueWaitMs.ToString("0")).Append(" ms, infer ").Append(t.InferenceMs.ToString("0"))
                  .Append(" ms, arrived +").Append(t.ArrivalSeconds.ToString("0.00")).Append(" s");
            if (t.Failure != null) sb.Append("\n  <color=#E08A6A>").Append(t.Failure).Append("</color>");
            foreach (ListenerTrace l in t.Listeners)
            {
                sb.Append("\n  <b>").Append(l.ListenerId).Append("</b> inferred: ").Append(l.Interpretation ?? "-");
                sb.Append("\n      policy: ").Append(l.Requested ?? "-").Append("  ⇒  ").Append(l.Outcome);
                if (l.Steps.Count > 0) sb.Append(" [").Append(string.Join("; ", l.Steps)).Append("]");
            }
        }

        const float width = 620f;
        var content = new GUIContent(sb.ToString());
        float height = Mathf.Min(_style.CalcHeight(content, width), Screen.height - 20f);
        GUI.Box(new Rect(Screen.width - width - 10f, 10f, width, height), content, _style);
    }
}
