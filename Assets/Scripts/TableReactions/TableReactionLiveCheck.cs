using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Tavern.Dialogue;
using UnityEngine;

/// <summary>
/// DEV ONLY. Scripted Play-mode check for the table-reaction experiment, started from a Unity MCP command:
/// <c>TableReactionLiveCheck.Begin()</c>. Talks to Master Wu with the same lines in each mode (Off, Rules,
/// D1 Sidecar, Gemma Baseline), records frame times per pass (FrameTimeProbe), captures frames of the table at
/// fixed delays after each line, then runs the stale-result checks (closing during evaluation, rapid turns).
/// Output: Logs/table-reactions-live/ (PNGs + live.jsonl); reaction traces go to Logs/table-reactions.jsonl.
/// Not part of the scene.
/// </summary>
public class TableReactionLiveCheck : MonoBehaviour
{
    public static readonly string[] Lines =
    {
        "Varien seems like your most disciplined student.",
        "What's good to drink here?",
        "Shara told me she's already surpassed you.",
        "I'm not saying Varien is a coward.",
        "Looks like rain is coming over the hills.",
    };

    private static readonly float[] WideShots = { 1.0f, 2.0f, 3.0f };
    private static readonly float[] CloseShots = { 1.5f, 2.5f };

    public static bool Running { get; private set; }
    public static string Status { get; private set; } = "idle";

    private DialogueManagerV2 _dm;
    private TableReactionDirector _director;
    private NPCIdentity _wu;
    private readonly List<NPCIdentity> _listeners = new List<NPCIdentity>();
    private int _lastCompletedTurn = -1;
    private int _lastCompletedSession;
    private string _outDir;
    private Camera _cam;
    private RenderTexture _rt;

    public static TableReactionLiveCheck Begin(bool captures = true, bool passes = true, bool staleChecks = true,
        bool framesOnly = false)
    {
        var go = new GameObject("TableReactionLiveCheck") { hideFlags = HideFlags.DontSave };
        var check = go.AddComponent<TableReactionLiveCheck>();
        check._framesOnly = framesOnly;
        check.StartCoroutine(check.Run(captures, passes, staleChecks));
        return check;
    }

    private bool _framesOnly;   // Only the Off and D1 passes, with frames, no stale checks

    private void OnEnable() => DialogueManagerV2.NpcLineCompleted += OnLine;
    private void OnDisable() => DialogueManagerV2.NpcLineCompleted -= OnLine;

    private void OnLine(DialogueManagerV2.DialogueLineInfo info)
    {
        _lastCompletedSession = info.Session;
        _lastCompletedTurn = info.Turn;
    }

    private IEnumerator Run(bool captures, bool passes, bool staleChecks)
    {
        Running = true;
        _outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "table-reactions-live",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(_outDir);

        Status = "waiting for models";
        float deadline = Time.realtimeSinceStartup + 180f;
        while ((XybridModelService.Instance == null || !XybridModelService.Instance.IsReady) && Time.realtimeSinceStartup < deadline)
            yield return null;

        _dm = FindAnyObjectByType<DialogueManagerV2>();
        _director = FindAnyObjectByType<TableReactionDirector>();
        TableConversation table = FindAnyObjectByType<TableConversation>();
        if (_dm == null || _director == null || table == null || XybridModelService.Instance == null || !XybridModelService.Instance.IsReady)
        {
            Finish("missing dialogue manager, director, table or models");
            yield break;
        }
        foreach (NPCIdentity npc in table.Members)
        {
            if (npc == null) continue;
            if (npc.npcName == "Master Wu") _wu = npc;
            else _listeners.Add(npc);
        }
        if (_wu == null) { Finish("Master Wu not found at the table"); yield break; }

        _dm.SetUseAIDialogue(true);
        PlacePlayerInFrontOf(_wu, 1.4f);
        yield return new WaitForSeconds(1f);
        Log(new JObject { ["type"] = "setup", ["listeners"] = new JArray(_listeners.ConvertAll(l => l.npcName)),
            ["distances"] = Distances() });

        if (passes)
        {
            TableReactionDirector.Mode[] modes = _framesOnly
                ? new[] { TableReactionDirector.Mode.Off, TableReactionDirector.Mode.D1Sidecar }
                : new[] { TableReactionDirector.Mode.Off, TableReactionDirector.Mode.Rules,
                          TableReactionDirector.Mode.D1Sidecar, TableReactionDirector.Mode.GemmaBaseline };
            foreach (TableReactionDirector.Mode mode in modes)
            {
                bool shoot = captures && (mode == TableReactionDirector.Mode.Off || mode == TableReactionDirector.Mode.D1Sidecar);
                yield return Pass(mode, shoot);
            }
        }

        if (staleChecks && !_framesOnly)
        {
            yield return CloseDuringEvaluation(TableReactionDirector.Mode.D1Sidecar);
            yield return CloseDuringEvaluation(TableReactionDirector.Mode.GemmaBaseline);
            yield return RapidTurns();
        }

        Finish("done");
    }

    private IEnumerator Pass(TableReactionDirector.Mode mode, bool shoot)
    {
        Status = "pass " + mode;
        _director.SetMode(mode);
        yield return new WaitForSeconds(mode == TableReactionDirector.Mode.D1Sidecar ? 1.5f : 0.5f);   // d1 health probe

        FrameTimeProbe.Begin("live-" + mode);
        yield return OpenDialogue();
        int session = DialogueManagerV2.CurrentSession;

        for (int i = 0; i < Lines.Length; i++)
        {
            if (!_dm.SubmitPlayerLine(Lines[i]))
            {
                Log(new JObject { ["type"] = "error", ["mode"] = mode.ToString(), ["line"] = Lines[i], ["error"] = "submit refused" });
                continue;
            }
            float t0 = Time.realtimeSinceStartup;
            int turn = i + 1;
            if (shoot) StartCoroutine(Shots(mode + "-t" + turn, t0));
            yield return WaitForTurn(session, turn, 40f);
            yield return WaitForSpeech(_wu);
            // Let reactions finish (latest start ~2 s after the line, gaze up to 2 s) before the next line
            float settle = Mathf.Max(0f, 4.5f - (Time.realtimeSinceStartup - t0));
            yield return new WaitForSeconds(Mathf.Max(1f, settle));
        }

        FrameTimeProbe.End();
        _dm.EndDialogue();
        yield return new WaitForSeconds(3f);
    }

    private IEnumerator CloseDuringEvaluation(TableReactionDirector.Mode mode)
    {
        Status = "close during evaluation, " + mode;
        _director.SetMode(mode);
        yield return new WaitForSeconds(1.5f);
        yield return OpenDialogue();
        _dm.SubmitPlayerLine(Lines[0]);
        yield return null;   // the judgment is now in flight (d1) or waiting for the reply (Gemma)
        _dm.EndDialogue();
        Log(new JObject { ["type"] = "stale-check", ["check"] = "close during evaluation", ["mode"] = mode.ToString() });
        yield return new WaitForSeconds(4f);
    }

    private IEnumerator RapidTurns()
    {
        Status = "rapid turns, Gemma";
        _director.SetMode(TableReactionDirector.Mode.GemmaBaseline);
        yield return new WaitForSeconds(0.5f);
        yield return OpenDialogue();
        int session = DialogueManagerV2.CurrentSession;
        _dm.SubmitPlayerLine(Lines[0]);
        yield return WaitForTurn(session, 1, 40f);
        // Next line as soon as input unlocks: the Gemma judgment for turn 1 has just started on the shared model
        float until = Time.realtimeSinceStartup + 2f;
        while (_dm.IsBusy && Time.realtimeSinceStartup < until) yield return null;
        bool ok = _dm.SubmitPlayerLine(Lines[3]);
        Log(new JObject { ["type"] = "stale-check", ["check"] = "rapid second turn", ["submitted"] = ok });
        yield return WaitForTurn(session, 2, 40f);
        yield return WaitForSpeech(_wu);
        yield return new WaitForSeconds(4f);
        _dm.EndDialogue();
        yield return new WaitForSeconds(2f);
    }

    private IEnumerator OpenDialogue()
    {
        float until = Time.realtimeSinceStartup + 20f;
        while (!_dm.CanStartDialogue && Time.realtimeSinceStartup < until) yield return null;
        _lastCompletedTurn = -1;
        _dm.StartDialogue(_wu);
        int session = DialogueManagerV2.CurrentSession;
        yield return WaitForTurn(session, 0, 40f);
        yield return WaitForSpeech(_wu);
        yield return new WaitForSeconds(0.5f);
    }

    private IEnumerator WaitForTurn(int session, int turn, float timeout)
    {
        float until = Time.realtimeSinceStartup + timeout;
        while (!(_lastCompletedSession == session && _lastCompletedTurn >= turn) && Time.realtimeSinceStartup < until)
            yield return null;
        // input unlocks right after the line event
        yield return null;
    }

    private static IEnumerator WaitForSpeech(NPCIdentity npc)
    {
        NPCFace face = npc.GetComponentInChildren<NPCFace>();
        if (face == null) yield break;
        float until = Time.realtimeSinceStartup + 4f;
        while (!face.IsSpeakingAudio && Time.realtimeSinceStartup < until) yield return null;
        until = Time.realtimeSinceStartup + 20f;
        while (face.IsSpeakingAudio && Time.realtimeSinceStartup < until) yield return null;
    }

    // ================================================================
    // Frames
    // ================================================================

    private IEnumerator Shots(string prefix, float t0)
    {
        var times = new List<(float t, Transform target, string name)>();
        foreach (float t in WideShots) times.Add((t, null, "wide"));
        foreach (float t in CloseShots)
            foreach (NPCIdentity l in _listeners)
                times.Add((t, NPCLookAt.HeadOf(l), l.npcName));
        times.Sort((a, b) => a.t.CompareTo(b.t));

        foreach (var shot in times)
        {
            while (Time.realtimeSinceStartup - t0 < shot.t) yield return null;
            yield return new WaitForEndOfFrame();
            Capture(prefix + "-" + shot.t.ToString("0.0") + "s-" + shot.name, shot.target);
        }
    }

    private void Capture(string name, Transform closeUp)
    {
        Camera main = Camera.main;
        if (main == null) return;
        if (_cam == null)
        {
            var go = new GameObject("LiveCheckCamera") { hideFlags = HideFlags.DontSave };
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;
            _rt = new RenderTexture(1280, 720, 24);
        }

        Vector3 center = Vector3.zero;
        var heads = new List<Transform> { NPCLookAt.HeadOf(_wu) };
        foreach (NPCIdentity l in _listeners) heads.Add(NPCLookAt.HeadOf(l));
        foreach (Transform h in heads) center += h.position;
        center /= heads.Count;

        // What the player sees: from the player's own eyes (an offset camera ended up inside stairs and railings)
        _cam.transform.position = main.transform.position;
        _cam.transform.LookAt(closeUp != null ? closeUp.position : center);
        _cam.fieldOfView = closeUp != null ? 24f : 75f;
        _cam.nearClipPlane = 0.05f;
        _cam.targetTexture = _rt;
        _cam.Render();

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = _rt;
        var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = previous;
        File.WriteAllBytes(Path.Combine(_outDir, name + ".png"), tex.EncodeToPNG());
        Destroy(tex);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static void PlacePlayerInFrontOf(NPCIdentity npc, float distance)
    {
        PlayerMovement player = FindAnyObjectByType<PlayerMovement>();
        if (player == null) return;
        var controller = player.GetComponent<CharacterController>();
        if (controller != null) controller.enabled = false;
        Vector3 target = npc.transform.position + npc.transform.forward * distance;
        target.y = player.transform.position.y;
        player.transform.position = target;
        Vector3 look = npc.transform.position - target;
        look.y = 0f;
        if (look.sqrMagnitude > 0.001f) player.transform.rotation = Quaternion.LookRotation(look);
        if (controller != null) controller.enabled = true;
    }

    private JObject Distances()
    {
        var o = new JObject();
        Camera main = Camera.main;
        if (main == null) return o;
        foreach (NPCIdentity l in _listeners)
            o[l.npcName] = Math.Round(Vector3.Distance(NPCLookAt.HeadOf(l).position, main.transform.position), 2);
        return o;
    }

    private void Log(JObject o)
    {
        o["utc"] = DateTime.UtcNow.ToString("o");
        File.AppendAllText(Path.Combine(_outDir, "live.jsonl"), o.ToString(Newtonsoft.Json.Formatting.None) + "\n");
    }

    private void Finish(string status)
    {
        Status = status;
        Running = false;
        Log(new JObject { ["type"] = "finished", ["status"] = status });
        if (_rt != null) _rt.Release();
        Debug.Log("[TableReactionLiveCheck] " + status);
    }
}
