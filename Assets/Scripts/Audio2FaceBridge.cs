using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Development bridge to NVIDIA Audio2Face-3D v2.3 ("Mark"): a spoken TTS line's PCM goes to a resident local
/// Python worker (tools/audio2face/a2f_worker.py) and comes back as a <see cref="FaceTrack"/> of Sidekick mouth and
/// jaw weights at 30 fps, about a quarter of the line's length later on an Apple-silicon CPU, for
/// <see cref="NPCFace.SetFaceTrack"/>. It stands in for an Xybrid SDK integration so the hybrid face (Audio2Face
/// mouth, NPCFace's own brows, blinks and gaze) can be judged in Play mode. Editor on macOS only.
/// Paths come from the project .env, with defaults: A2F_PYTHON (tools/audio2face/.venv/bin/python),
/// A2F_MODEL_DIR (~/Developer/xybrid-models/audio2face-3d-v2.3-mark), A2F_THREADS (6). Setup:
/// tools/audio2face/README.md. Every failure returns null, so the face keeps its own lip sync.
/// </summary>
public static class Audio2FaceBridge
{
    private const string WorkerScript = "tools/audio2face/a2f_worker.py";
    private const string DefaultPython = "tools/audio2face/.venv/bin/python";
    private const string DefaultModelDir = "Developer/xybrid-models/audio2face-3d-v2.3-mark";
    private const string DefaultThreads = "6";
    private const string JobFolderName = "tavern-a2f";
    private const float TrackFps = 30f;
    private const float ReadySeconds = 30f;     // Python start-up plus the model load

#pragma warning disable 0649   // assigned by JsonUtility
    [Serializable] private class Reply { public int id = -1; public bool ok; public bool ready; public int frames; public float ms; public float load_s; public string error; }
    [Serializable] private class TrackFile { public float fps; public int frames; public string[] names; public float[] weights; }
#pragma warning restore 0649

    private static readonly object Gate = new object();
    private static readonly HashSet<string> WarnedKinds = new HashSet<string>();
    private static readonly Dictionary<int, TaskCompletionSource<Reply>> Pending = new Dictionary<int, TaskCompletionSource<Reply>>();

    private static bool _checked;
    private static bool _available;
    private static string _python, _script, _modelDir, _threads;
    private static Process _worker;
    private static TaskCompletionSource<bool> _ready;
    private static int _nextId;
    private static string _lastError = "";
    private static bool _hooked;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// True in the Editor on macOS when the worker script, its Python and the model directory exist (and the worker
    /// hasn't failed to start this session). Call from the main thread.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (_checked) return _available;
            _checked = true;
            _available = false;
            if (Application.platform != RuntimePlatform.OSXEditor) return false;

            string project = Path.GetDirectoryName(Application.dataPath);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            _script = Path.Combine(project, WorkerScript);
            _python = Setting("A2F_PYTHON", Path.Combine(project, DefaultPython));
            _modelDir = Setting("A2F_MODEL_DIR", Path.Combine(home, DefaultModelDir));
            _threads = Setting("A2F_THREADS", DefaultThreads);

            if (!File.Exists(_script)) Warn("script", $"[Audio2FaceBridge] Worker script missing at '{_script}'.");
            else if (!File.Exists(_python))
                Warn("python", $"[Audio2FaceBridge] No Python at '{_python}'. Set it up as in tools/audio2face/README.md " +
                               "(or set A2F_PYTHON in .env).");
            else if (!File.Exists(Path.Combine(_modelDir, "network.onnx")))
                Warn("model", $"[Audio2FaceBridge] No Audio2Face model in '{_modelDir}'. See tools/audio2face/README.md " +
                              "(or set A2F_MODEL_DIR in .env).");
            else _available = true;
            return _available;
        }
    }

    /// <summary>Starts the worker now so the first line doesn't wait for Python and the model load.</summary>
    public static void Prewarm()
    {
        if (IsAvailable) EnsureWorker();
    }

    /// <summary>
    /// Runs Audio2Face on one line of 16-bit little-endian mono PCM. <paramref name="emotion"/> is A2F's explicit
    /// emotion list (see <see cref="EmotionFor"/>). Returns null on any failure or timeout. Call on the main thread.
    /// </summary>
    public static async Task<FaceTrack> AnalyzeAsync(byte[] pcm16, int sampleRate, string emotion,
        float timeoutSeconds = 20f, CancellationToken token = default)
    {
        if (pcm16 == null || pcm16.Length < 2 || sampleRate <= 0 || !IsAvailable) return null;
        if (!EnsureWorker()) return null;

        string folder = Path.Combine(Path.GetTempPath(), JobFolderName);
        string job = Guid.NewGuid().ToString("N");
        string pcmPath = Path.Combine(folder, job + ".pcm");
        string outPath = Path.Combine(folder, job + ".json");
        TaskCompletionSource<bool> ready;
        lock (Gate) ready = _ready;

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(pcmPath, pcm16);
            });

            Task readyFirst = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(ReadySeconds), token));
            if (readyFirst != ready.Task || !ready.Task.Result)
            {
                Warn("ready", "[Audio2FaceBridge] The Audio2Face worker didn't come up" + LastError() + ".");
                return null;
            }

            var reply = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            lock (Gate)
            {
                if (_worker == null) return null;
                id = ++_nextId;
                Pending[id] = reply;
                _worker.StandardInput.WriteLine(Request(id, pcmPath, sampleRate, emotion, outPath));
                _worker.StandardInput.Flush();
            }

            Task first = await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), token));
            if (first != reply.Task)
            {
                lock (Gate) Pending.Remove(id);
                if (!token.IsCancellationRequested)
                    Warn("timeout", $"[Audio2FaceBridge] No Audio2Face track after {timeoutSeconds:0} s; that line keeps Rhubarb.");
                return null;
            }

            Reply result = reply.Task.Result;
            if (!result.ok)
            {
                Warn("request", $"[Audio2FaceBridge] Audio2Face failed: {result.error}");
                return null;
            }

            // JsonUtility is safe off the main thread
            TrackFile file = await Task.Run(() => JsonUtility.FromJson<TrackFile>(File.ReadAllText(outPath)));
            if (file == null || file.names == null || file.names.Length == 0 || file.weights == null ||
                file.weights.Length != file.frames * file.names.Length)
            {
                Warn("parse", "[Audio2FaceBridge] Unusable Audio2Face track file.");
                return null;
            }
            return new FaceTrack(file.fps > 0f ? file.fps : TrackFps, file.names, file.weights);
        }
        catch (Exception ex)
        {
            Warn("unexpected", $"[Audio2FaceBridge] Audio2Face request failed: {ex.Message}");
            return null;
        }
        finally
        {
            TryDelete(pcmPath);
            TryDelete(outPath);
        }
    }

    /// <summary>
    /// A2F's explicit emotions (amazement, anger, cheekiness, disgust, fear, grief, joy, outofbreath, pain, sadness)
    /// for a face emotion at a <see cref="LineEmotion"/> intensity (0.3..0.9). In the hybrid only their effect on the
    /// mouth is used (a smiling or tight delivery); NPCFace shows the rest of the expression itself.
    /// </summary>
    public static string EmotionFor(FaceEmotion emotion, float intensity)
    {
        float k = Mathf.Clamp(intensity / 0.6f, 0.5f, 1.25f);
        string E(string name, float value) => name + "=" + Mathf.Clamp01(value * k).ToString("0.00", CultureInfo.InvariantCulture);
        switch (emotion)
        {
            case FaceEmotion.Content: return E("joy", 0.3f);
            case FaceEmotion.Happy: return E("joy", 0.6f);
            case FaceEmotion.Delighted: return E("joy", 0.8f) + "," + E("amazement", 0.2f);
            case FaceEmotion.Smug: return E("cheekiness", 0.5f);
            case FaceEmotion.Suspicious: return E("anger", 0.15f);
            case FaceEmotion.Grumpy: return E("anger", 0.3f);
            case FaceEmotion.Angry: return E("anger", 0.7f);
            case FaceEmotion.Worried: return E("fear", 0.3f);
            case FaceEmotion.Sad: return E("sadness", 0.6f);
            case FaceEmotion.Scared: return E("fear", 0.7f);
            case FaceEmotion.Surprised: return E("amazement", 0.6f);
            case FaceEmotion.Disgusted: return E("disgust", 0.6f);
            default: return "";
        }
    }

    // ================================================================
    // Worker process
    // ================================================================

    private static bool EnsureWorker()
    {
        lock (Gate)
        {
            if (_worker != null && !_worker.HasExited) return true;
            HookShutdown();

            var start = new ProcessStartInfo
            {
                FileName = _python,
                Arguments = "-u " + Quote(_script),
                WorkingDirectory = Path.GetDirectoryName(_script),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.EnvironmentVariables["A2F_MODEL_DIR"] = _modelDir;
            start.EnvironmentVariables["A2F_THREADS"] = _threads;

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnOutput(e.Data);
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _lastError = e.Data.Trim(); };
            process.Exited += (_, __) => OnExited(process);
            _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _available = false;
                _ready.TrySetResult(false);
                Warn("launch", $"[Audio2FaceBridge] Could not start '{_python}': {ex.Message}. Audio2Face disabled for this session.");
                return false;
            }
            _worker = process;
            return true;
        }
    }

    /// <summary>Worker stdout, on a pipe thread: the ready line, then one reply per request.</summary>
    private static void OnOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Reply reply;
        try
        {
            reply = JsonUtility.FromJson<Reply>(line);
        }
        catch (Exception)
        {
            return;
        }
        if (reply == null) return;

        lock (Gate)
        {
            if (reply.id < 0)
            {
                if (!reply.ready && !string.IsNullOrEmpty(reply.error)) _lastError = reply.error;
                _ready?.TrySetResult(reply.ready);
                return;
            }
            if (Pending.TryGetValue(reply.id, out TaskCompletionSource<Reply> waiting))
            {
                Pending.Remove(reply.id);
                waiting.TrySetResult(reply);
            }
        }
    }

    private static void OnExited(Process process)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(process, _worker)) return;
            _worker = null;
            _ready?.TrySetResult(false);
            foreach (TaskCompletionSource<Reply> waiting in Pending.Values)
                waiting.TrySetResult(new Reply { ok = false, error = "the worker exited" + LastError() });
            Pending.Clear();
        }
    }

    /// <summary>Stops the worker when Play mode ends (Application.quitting) or scripts reload.</summary>
    private static void HookShutdown()
    {
        if (_hooked) return;
        _hooked = true;
        Application.quitting += StopWorker;
        AppDomain.CurrentDomain.DomainUnload += (_, __) => StopWorker();
    }

    private static void StopWorker()
    {
        Process process;
        lock (Gate)
        {
            process = _worker;
            _worker = null;
            _ready?.TrySetResult(false);
        }
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();   // The worker exits at end of input
                if (!process.WaitForExit(500)) process.Kill();
            }
        }
        catch (Exception)
        {
            // Already gone
        }
        finally
        {
            process.Dispose();
        }
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static string Request(int id, string pcm, int rate, string emotion, string output)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"id\":").Append(id)
          .Append(",\"pcm\":\"").Append(JsonEscape(pcm))
          .Append("\",\"rate\":").Append(rate)
          .Append(",\"emotion\":\"").Append(JsonEscape(emotion ?? ""))
          .Append("\",\"fps\":").Append(TrackFps.ToString(CultureInfo.InvariantCulture))
          .Append(",\"out\":\"").Append(JsonEscape(output)).Append("\"}");
        return sb.ToString();
    }

    private static string JsonEscape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Setting(string key, string fallback)
    {
        string value = Tavern.Dialogue.DotEnv.Get(key);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        value = value.Trim();
        return value.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), value.Substring(2))
            : value;
    }

    private static string LastError() => string.IsNullOrEmpty(_lastError) ? "" : " (" + _lastError + ")";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Temp file; the OS cleans it eventually
        }
    }

    /// <summary>Logs a warning once per failure kind so a missing setup doesn't spam every line.</summary>
    private static void Warn(string kind, string message)
    {
        lock (WarnedKinds)
        {
            if (!WarnedKinds.Add(kind)) return;
        }
        Debug.LogWarning(message);
    }

    /// <summary>Re-checks the setup and re-arms warnings on Play-mode entry (needed when domain reload is disabled).</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        StopWorker();
        _checked = false;
        _available = false;
        _lastError = "";
        lock (WarnedKinds) WarnedKinds.Clear();
        lock (Gate) Pending.Clear();
    }
}
