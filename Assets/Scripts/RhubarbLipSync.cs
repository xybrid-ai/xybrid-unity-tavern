using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Runs the Rhubarb Lip Sync 1.14 CLI on a spoken TTS clip and returns its mouth cues (shapes A-H and X)
/// for <see cref="NPCFace"/>. macOS only: the binary ships in StreamingAssets/Rhubarb.
/// Every failure returns null, so the face simply keeps its loudness-driven jaw.
///
/// Rhubarb 1.14 crashes (SIGSEGV in its PocketSphinx log callback) when its folder path is longer than
/// about 130 characters, which a built app's StreamingAssets path easily is. It therefore runs from a short
/// $TMPDIR folder holding a copy of the shipped binary next to a link to the shipped res/ folder.
/// </summary>
public static class RhubarbLipSync
{
    private const string RelativeExecutable = "Rhubarb/Rhubarb-Lip-Sync-1.14.0-macOS/rhubarb";
    private const string ValidShapes = "ABCDEFGHX";
    private const string LauncherPrefix = "tavern-rhubarb-";
    private const string JobFolderName = "tavern-lipsync";

    // JsonUtility wrapper for Rhubarb's JSON export: {"mouthCues":[{"start":0.0,"end":0.1,"value":"X"}]}
#pragma warning disable 0649   // assigned by JsonUtility
    [Serializable] private class RawCue { public float start; public float end; public string value; }
    [Serializable] private class CueFile { public RawCue[] mouthCues; }
#pragma warning restore 0649

    private static readonly object LauncherGate = new object();
    private static readonly HashSet<string> WarnedKinds = new HashSet<string>();

    private static bool _checked;
    private static volatile bool _available;
    private static string _shippedExecutable;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// True on macOS (Editor or player) when the Rhubarb executable is present in StreamingAssets.
    /// Becomes false for the rest of the session if the launcher copy can't be prepared.
    /// Call from the main thread (the first call reads Application.streamingAssetsPath).
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (!_checked)
            {
                _checked = true;
                _available = false;

                bool isMac = Application.platform == RuntimePlatform.OSXEditor ||
                             Application.platform == RuntimePlatform.OSXPlayer;
                if (isMac)
                {
                    try
                    {
                        _shippedExecutable = Path.Combine(Application.streamingAssetsPath, RelativeExecutable);
                        _available = File.Exists(_shippedExecutable);
                    }
                    catch (Exception)
                    {
                        _available = false;
                    }

                    if (!_available)
                        Warn("missing", $"[RhubarbLipSync] Rhubarb not found at '{_shippedExecutable}'. " +
                                        "NPC mouths use the in-engine SpectralLipSync visemes instead.");
                }
            }
            return _available;
        }
    }

    /// <summary>
    /// Analyses <paramref name="clip"/> with Rhubarb (phonetic recognizer, guided by the transcript) and
    /// returns its mouth cues, ordered and clamped to the clip length. Returns null on any failure,
    /// on cancellation, or when Rhubarb is unavailable.
    /// MUST be called on the main thread: the clip's samples are copied here, then the WAV/transcript
    /// writing, the CLI run and the JSON parsing all happen on a background task.
    /// </summary>
    public static async Task<LipSyncCue[]> AnalyzeAsync(AudioClip clip, string transcript,
        CancellationToken cancellationToken = default, float timeoutSeconds = 10f)
    {
        if (clip == null || !IsAvailable) return null;
        if (cancellationToken.IsCancellationRequested) return null;

        // Unity objects are main-thread only: copy everything Rhubarb needs out of the clip now
        int channels = clip.channels;
        int frequency = clip.frequency;
        int frames = clip.samples;
        float clipLength = clip.length;
        if (channels <= 0 || frequency <= 0 || frames <= 0)
        {
            Warn("clip", $"[RhubarbLipSync] Clip '{clip.name}' has no readable samples; skipping lip sync.");
            return null;
        }

        float[] samples = new float[frames * channels];
        bool read;
        try
        {
            read = clip.GetData(samples, 0);
        }
        catch (Exception)
        {
            read = false;
        }
        if (!read)
        {
            Warn("clip", $"[RhubarbLipSync] Could not read samples of clip '{clip.name}' " +
                         "(streamed or compressed clips are not supported); skipping lip sync.");
            return null;
        }

        string shipped = _shippedExecutable;
        string text = transcript ?? string.Empty;
        float timeout = timeoutSeconds > 0f ? timeoutSeconds : 10f;

        try
        {
            return await Task.Run(() =>
                Analyze(shipped, samples, channels, frequency, clipLength, text, timeout, cancellationToken));
        }
        catch (Exception ex)
        {
            Warn("unexpected", $"[RhubarbLipSync] Lip sync failed: {ex.Message}");
            return null;
        }
    }

    // ================================================================
    // Background work
    // ================================================================

    private static LipSyncCue[] Analyze(string shipped, float[] samples, int channels, int frequency,
        float clipLength, string transcript, float timeoutSeconds, CancellationToken token)
    {
        string executable;
        try
        {
            executable = Launcher(shipped);
        }
        catch (Exception ex)
        {
            // Without a runnable copy every later line would fail the same way: stop trying this session
            _available = false;
            Warn("prepare", $"[RhubarbLipSync] Could not prepare Rhubarb ({ex.Message}). " +
                            "Lip sync disabled for this session.");
            return null;
        }

        string directory = null;
        try
        {
            directory = Path.Combine(Path.GetTempPath(), JobFolderName, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            string wav = Path.Combine(directory, "speech.wav");
            string cues = Path.Combine(directory, "mouth.json");
            string dialog = Path.Combine(directory, "script.txt");

            WriteWav(wav, samples, channels, frequency);

            bool hasDialog = !string.IsNullOrWhiteSpace(transcript);
            if (hasDialog)
                File.WriteAllText(dialog, transcript, new UTF8Encoding(false));

            if (token.IsCancellationRequested) return null;

            string arguments = "-r phonetic -f json --quiet --threads 2 " +
                               (hasDialog ? "--dialogFile " + Quote(dialog) + " " : "") +
                               "-o " + Quote(cues) + " " + Quote(wav);

            string failure = RunRhubarb(executable, arguments, timeoutSeconds, token, out string failureKind);
            if (failure != null)
            {
                // A binary that can't even start won't start next line either (e.g. the x86_64-only
                // Rhubarb on Apple Silicon without Rosetta 2): stop trying this session
                if (failureKind == "launch") _available = false;
                if (failureKind != "cancelled") Warn(failureKind, failure);
                return null;
            }

            if (!File.Exists(cues))
            {
                Warn("output", "[RhubarbLipSync] Rhubarb finished but wrote no cue file.");
                return null;
            }

            LipSyncCue[] parsed = Parse(File.ReadAllText(cues), clipLength, out string parseError);
            if (parsed == null)
                Warn("parse", $"[RhubarbLipSync] Unusable Rhubarb output: {parseError}");
            return parsed;
        }
        catch (Exception ex)
        {
            Warn("io", $"[RhubarbLipSync] Lip sync job failed: {ex.Message}");
            return null;
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Runs Rhubarb and waits for it with a timeout. Returns null on success, else a warning message
    /// and its failure kind ("launch", "timeout", "exit" or "cancelled").
    /// </summary>
    private static string RunRhubarb(string executable, string arguments, float timeoutSeconds,
        CancellationToken token, out string failureKind)
    {
        failureKind = null;

        var start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using (var process = new Process { StartInfo = start })
        {
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                failureKind = "launch";
                return $"[RhubarbLipSync] Could not start Rhubarb: {ex.Message.TrimEnd('.')}.{ArchitectureHint()} " +
                       "Rhubarb disabled for this session; mouths use the in-engine SpectralLipSync visemes.";
            }

            // Drain both pipes so a chatty run can never block on a full buffer
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            var timer = Stopwatch.StartNew();

            try
            {
                while (!process.WaitForExit(50))
                {
                    if (token.IsCancellationRequested)
                    {
                        failureKind = "cancelled";
                        return "cancelled";
                    }
                    if (timer.Elapsed.TotalSeconds > timeoutSeconds)
                    {
                        failureKind = "timeout";
                        return $"[RhubarbLipSync] Rhubarb timed out after {timeoutSeconds:0.#} s; " +
                               "that line falls back to the loudness jaw.";
                    }
                }

                int exitCode = process.ExitCode;
                if (exitCode == 0)
                {
                    PipeText(stdout);
                    return null;
                }

                string errorText = PipeText(stderr);

                // The OS could not execute the binary at all (permissions, wrong architecture): 126/127 by
                // shell convention, or 255 with no output, which is how Mono reports a failed exec (the
                // x86_64-only Rhubarb on Apple Silicon without Rosetta 2 starts "fine" and exits 255)
                if (exitCode == 126 || exitCode == 127 ||
                    (exitCode == 255 && errorText.Length == 0 && PipeText(stdout).Length == 0))
                {
                    failureKind = "launch";
                    return $"[RhubarbLipSync] Rhubarb could not be executed (exit code {exitCode}).{ArchitectureHint()} " +
                           "Rhubarb disabled for this session; mouths use the in-engine SpectralLipSync visemes.";
                }

                failureKind = "exit";
                return $"[RhubarbLipSync] Rhubarb exited with code {exitCode}: {errorText}";
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                }
                catch (Exception)
                {
                    // Already gone
                }
            }
        }
    }

    /// <summary>
    /// The path to start Rhubarb from: a copy in $TMPDIR/tavern-rhubarb-&lt;hash of the shipped folder&gt;/
    /// (refreshed when the shipped binary changes) next to a symlink to the shipped res/ folder.
    /// </summary>
    private static string Launcher(string shipped)
    {
        if (string.IsNullOrEmpty(shipped) || !File.Exists(shipped))
            throw new FileNotFoundException("Rhubarb is not installed at " + shipped, shipped);

        string source = Path.GetDirectoryName(shipped);
        uint hash = 2166136261;
        unchecked
        {
            foreach (char c in source)
                hash = (hash ^ c) * 16777619;
        }

        string folder = Path.Combine(Path.GetTempPath(), LauncherPrefix + hash.ToString("x8"));
        string binary = Path.Combine(folder, Path.GetFileName(shipped));
        string resources = Path.Combine(folder, "res");

        lock (LauncherGate)
        {
            var original = new FileInfo(shipped);
            var copy = new FileInfo(binary);
            if (copy.Exists && copy.Length == original.Length &&
                copy.LastWriteTimeUtc == original.LastWriteTimeUtc && Directory.Exists(resources))
                return binary;

            Directory.CreateDirectory(folder);
            File.Copy(shipped, binary, true);
            File.SetLastWriteTimeUtc(binary, original.LastWriteTimeUtc);
            RunTool("/bin/chmod", "755 " + Quote(binary), required: true);
            // A quarantined copy would be killed by Gatekeeper on launch. The attribute is usually
            // absent (xattr then exits non-zero), so this step is best effort.
            RunTool("/usr/bin/xattr", "-d com.apple.quarantine " + Quote(binary), required: false);
            RunTool("/bin/ln", "-sfn " + Quote(Path.Combine(source, "res")) + " " + Quote(resources), required: true);
            return binary;
        }
    }

    private static void RunTool(string tool, string arguments, bool required)
    {
        try
        {
            using (var process = Process.Start(new ProcessStartInfo(tool, arguments)
                   {
                       UseShellExecute = false,
                       CreateNoWindow = true
                   }))
            {
                bool ok = process != null && process.WaitForExit(10000) && process.ExitCode == 0;
                if (!ok && required)
                    throw new InvalidOperationException("Could not prepare Rhubarb: " + tool + " " + arguments);
            }
        }
        catch (Exception) when (!required)
        {
            // Best-effort step
        }
    }

    // ================================================================
    // File formats
    // ================================================================

    /// <summary>Writes the samples as a 16-bit PCM mono WAV (channels are averaged).</summary>
    private static void WriteWav(string path, float[] samples, int channels, int frequency)
    {
        int frames = samples.Length / channels;
        int dataBytes = frames * 2;
        var data = new byte[dataBytes];

        for (int f = 0; f < frames; f++)
        {
            float mono = 0f;
            for (int c = 0; c < channels; c++)
                mono += samples[f * channels + c];
            mono /= channels;
            if (float.IsNaN(mono) || float.IsInfinity(mono)) mono = 0f;

            int value = (int)Math.Round(mono * 32767.0);
            if (value > short.MaxValue) value = short.MaxValue;
            else if (value < short.MinValue) value = short.MinValue;

            data[f * 2] = (byte)(value & 0xFF);
            data[f * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataBytes);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);                 // fmt chunk size
            writer.Write((short)1);           // PCM
            writer.Write((short)1);           // mono
            writer.Write(frequency);
            writer.Write(frequency * 2);      // byte rate
            writer.Write((short)2);           // block align
            writer.Write((short)16);          // bits per sample
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataBytes);
            writer.Write(data);
        }
    }

    /// <summary>
    /// Parses Rhubarb's JSON into ordered, non-overlapping cues within the clip. Invalid cues are dropped.
    /// Returns null (with <paramref name="error"/>) when nothing usable remains.
    /// </summary>
    private static LipSyncCue[] Parse(string json, float clipLength, out string error)
    {
        error = null;

        CueFile file;
        try
        {
            file = JsonUtility.FromJson<CueFile>(json);   // JsonUtility is safe on background threads
        }
        catch (Exception ex)
        {
            error = "invalid JSON (" + ex.Message + ")";
            return null;
        }

        if (file == null || file.mouthCues == null || file.mouthCues.Length == 0)
        {
            error = "no mouth cues";
            return null;
        }

        float limit = clipLength > 0f ? clipLength : float.MaxValue;
        var cues = new List<LipSyncCue>(file.mouthCues.Length);
        float previousEnd = 0f;

        foreach (RawCue raw in file.mouthCues)
        {
            if (raw == null || string.IsNullOrEmpty(raw.value)) continue;
            if (float.IsNaN(raw.start) || float.IsNaN(raw.end) ||
                float.IsInfinity(raw.start) || float.IsInfinity(raw.end)) continue;

            char shape = char.ToUpperInvariant(raw.value[0]);
            if (ValidShapes.IndexOf(shape) < 0) continue;

            float cueStart = Math.Max(Math.Max(raw.start, 0f), previousEnd);
            float cueEnd = Math.Min(raw.end, limit);
            if (cueEnd <= cueStart) continue;

            cues.Add(new LipSyncCue { start = cueStart, end = cueEnd, shape = shape });
            previousEnd = cueEnd;
        }

        if (cues.Count == 0)
        {
            error = "no valid cues within the clip";
            return null;
        }
        return cues.ToArray();
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string ArchitectureHint() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? " The shipped Rhubarb 1.14 is x86_64-only: on Apple Silicon it needs Rosetta 2 " +
              "(softwareupdate --install-rosetta --agree-to-license)."
            : "";

    private static string PipeText(Task<string> pipe)
    {
        try
        {
            if (pipe == null || !pipe.Wait(500)) return "";
            string text = pipe.Result?.Trim() ?? "";
            return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return;
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch (Exception)
        {
            // Temp folder; the OS cleans it eventually
        }
    }

    /// <summary>Logs a warning once per failure kind so a broken setup doesn't spam every line.</summary>
    private static void Warn(string kind, string message)
    {
        lock (WarnedKinds)
        {
            if (!WarnedKinds.Add(kind)) return;
        }
        Debug.LogWarning(message);
    }

    /// <summary>
    /// Re-checks availability and re-arms warnings on play-mode entry (needed when domain reload is disabled).
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _checked = false;
        _available = false;
        _shippedExecutable = null;
        lock (WarnedKinds)
        {
            WarnedKinds.Clear();
        }
    }
}
