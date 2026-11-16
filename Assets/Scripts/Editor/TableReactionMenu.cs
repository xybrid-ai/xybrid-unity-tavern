#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>Editor menu for the table-reaction experiment (dev tooling).</summary>
public static class TableReactionMenu
{
    private const string LiveCheck = "Tavern/Table Reactions/Run Live Check (Play mode)";

    /// <summary>
    /// Talks to Master Wu with the same five lines in each mode (Off, Rules, D1 Sidecar, Gemma Baseline), then runs
    /// the stale-result checks. Takes about five minutes; don't use mouse or keyboard meanwhile. Results go to
    /// Logs/table-reactions-live/ and Logs/table-reactions.jsonl.
    /// </summary>
    [MenuItem(LiveCheck)]
    private static void RunLiveCheck()
    {
        if (TableReactionLiveCheck.Running)
        {
            Debug.LogWarning("[TableReactionLiveCheck] Already running: " + TableReactionLiveCheck.Status);
            return;
        }
        TableReactionLiveCheck.Begin();
        Debug.Log("[TableReactionLiveCheck] Started. Hands off mouse and keyboard until it logs 'done' (about 5 minutes).");
    }

    [MenuItem(LiveCheck, true)]
    private static bool CanRunLiveCheck() => EditorApplication.isPlaying;

    private const string FramesOnly = "Tavern/Table Reactions/Run Live Check - Frames Only (Play mode)";

    /// <summary>Shorter run (about 1.5 minutes): Off and D1 Sidecar passes with frame captures, no stale checks.</summary>
    [MenuItem(FramesOnly)]
    private static void RunFramesOnly()
    {
        if (TableReactionLiveCheck.Running) return;
        TableReactionLiveCheck.Begin(framesOnly: true);
        Debug.Log("[TableReactionLiveCheck] Started (frames only). Hands off until it logs 'done' (about 1.5 minutes).");
    }

    [MenuItem(FramesOnly, true)]
    private static bool CanRunFramesOnly() => EditorApplication.isPlaying;
}
#endif
