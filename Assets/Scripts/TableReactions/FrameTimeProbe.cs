using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Dev measurement helper for the table-reaction experiment: records frame times, GC collections and memory
/// between <see cref="Begin"/> and <see cref="End"/> and appends a summary to Logs/frame-probe.jsonl.
/// Created on demand (e.g. from a Unity MCP command in Play mode); not part of the scene.
/// </summary>
public class FrameTimeProbe : MonoBehaviour
{
    private static FrameTimeProbe _instance;

    private readonly List<float> _frames = new List<float>(4096);
    private string _label;
    private bool _recording;
    private int _gcStart;
    private long _allocatedStart;
    private long _monoStart;
    private float _startTime;

    public static bool IsRecording => _instance != null && _instance._recording;

    public static void Begin(string label)
    {
        if (_instance == null)
        {
            var go = new GameObject("FrameTimeProbe") { hideFlags = HideFlags.DontSave };
            _instance = go.AddComponent<FrameTimeProbe>();
        }
        FrameTimeProbe p = _instance;
        p._frames.Clear();
        p._label = label;
        p._gcStart = GC.CollectionCount(0);
        p._allocatedStart = Profiler.GetTotalAllocatedMemoryLong();
        p._monoStart = Profiler.GetMonoUsedSizeLong();
        p._startTime = Time.realtimeSinceStartup;
        p._recording = true;
    }

    public static string End()
    {
        if (_instance == null || !_instance._recording) return "probe not recording";
        FrameTimeProbe p = _instance;
        p._recording = false;

        var sorted = new List<float>(p._frames);
        sorted.Sort();
        float Pct(double q) => sorted.Count == 0 ? 0f : sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(q * sorted.Count) - 1))];
        int over33 = sorted.FindAll(f => f > 33.3f).Count;

        var json = new JObject
        {
            ["utc"] = DateTime.UtcNow.ToString("o"),
            ["label"] = p._label,
            ["seconds"] = Math.Round(Time.realtimeSinceStartup - p._startTime, 2),
            ["frames"] = sorted.Count,
            ["p50_ms"] = Math.Round(Pct(0.5), 2),
            ["p95_ms"] = Math.Round(Pct(0.95), 2),
            ["p99_ms"] = Math.Round(Pct(0.99), 2),
            ["max_ms"] = Math.Round(sorted.Count > 0 ? sorted[sorted.Count - 1] : 0, 2),
            ["frames_over_33ms"] = over33,
            ["gc_gen0"] = GC.CollectionCount(0) - p._gcStart,
            ["unity_allocated_delta_mb"] = Math.Round((Profiler.GetTotalAllocatedMemoryLong() - p._allocatedStart) / 1048576.0, 2),
            ["mono_used_delta_mb"] = Math.Round((Profiler.GetMonoUsedSizeLong() - p._monoStart) / 1048576.0, 2),
            ["process_rss_mb"] = Math.Round(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 0),
        };
        string line = json.ToString(Newtonsoft.Json.Formatting.None);
        try
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "frame-probe.jsonl"));
            File.AppendAllText(path, line + "\n");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FrameTimeProbe] " + ex.Message);
        }
        Debug.Log("[FrameTimeProbe] " + line);
        return line;
    }

    private void Update()
    {
        if (_recording) _frames.Add(Time.unscaledDeltaTime * 1000f);
    }
}
