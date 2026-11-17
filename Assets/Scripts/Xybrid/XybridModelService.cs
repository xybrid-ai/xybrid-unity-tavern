using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tavern.Telemetry;
using UnityEngine;
using Xybrid;
using Xybrid.ModelAsset;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Singleton service that owns multiple Xybrid model instances and serializes inference requests.
    /// Supports loading multiple models simultaneously (e.g., LLM + TTS).
    /// Attach to a GameObject in the scene. Persists across scene loads.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class XybridModelService : MonoBehaviour
    {
        public static XybridModelService Instance { get; private set; }

        [Header("Models")]
        [Tooltip("Drag .xyb model assets here. Supports multiple models (LLM, TTS, etc.)")]
        [SerializeField] private XybridModelAsset[] _modelAssets;

        [Header("GGUF Models")]
        [Tooltip("Paths to raw GGUF files (relative to StreamingAssets or absolute). Metadata is auto-generated.")]
        [SerializeField] private string[] _ggufModelPaths;

        [Header("Settings")]
        [SerializeField] private bool _persistAcrossScenes = true;

        // Telemetry is optional and configured outside serialized data:
        // Preferences > Xybrid Tavern > Telemetry (Editor), else .env / the
        // xybrid_api_key environment variable. See TelemetryApiKey.
        private bool _telemetryActive;

        // Legacy fallback — kept for backward compat with existing scenes
        [SerializeField, HideInInspector] private string _modelId;

        private readonly Dictionary<string, ModelEntry> _models = new Dictionary<string, ModelEntry>();
        private bool _isReady;
        private string _sdkVersion;
        private Task _initTask;

        public bool IsReady => _isReady;
        public string SdkVersion => _sdkVersion;

        /// <summary>
        /// Returns the LLM model ID for backward compatibility.
        /// </summary>
        public string ModelId
        {
            get
            {
                var llm = GetLLMEntry();
                return llm?.Model?.ModelId ?? _modelId;
            }
        }

        /// <summary>
        /// Returns true if any model is currently processing inference.
        /// </summary>
        public bool IsProcessing
        {
            get
            {
                foreach (var entry in _models.Values)
                {
                    if (entry.Lock.CurrentCount == 0) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Returns true if a TTS model is loaded and available.
        /// </summary>
        public bool HasTTSModel => GetTTSEntry() != null;

        /// <summary>
        /// Returns the TTS model asset (for reading voice catalog), or null.
        /// </summary>
        public XybridModelAsset TTSModelAsset => GetTTSEntry()?.Asset;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[XybridModelService] Duplicate instance destroyed.");
                Destroy(gameObject);
                return;
            }

            Instance = this;
            if (_persistAcrossScenes)
                DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// Initialize the Xybrid SDK and load all configured models.
        /// Safe to call from multiple callers — only the first call runs init,
        /// subsequent callers await the same task.
        /// </summary>
        public Task InitializeAsync()
        {
            if (_initTask == null)
                _initTask = InitializeCoreAsync();
            return _initTask;
        }

        private async Task InitializeCoreAsync()
        {
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            Debug.Log("[XybridModelService] Starting initialization...");

            await Task.Run(() => XybridClient.Initialize());
            TryInitializeTelemetry();

            // Use model assets if configured
            if (_modelAssets != null && _modelAssets.Length > 0)
            {
                // Build models on background thread, collect into local list first
                var loaded = new List<KeyValuePair<string, ModelEntry>>();

                await Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    XybridClient.Initialize();
                    Debug.Log($"[XybridModelService] XybridClient.Initialize() took {sw.ElapsedMilliseconds}ms");

                    foreach (var asset in _modelAssets)
                    {
                        if (asset == null) continue;

                        string path = asset.GetRuntimePath();
                        Model model;

                        sw.Restart();
                        switch (asset.sourceType)
                        {
                            case ModelSourceType.GgufFile:
                                Debug.Log($"[XybridModelService] Loading '{asset.modelId}' from GGUF: {path}");
                                model = XybridClient.LoadModelFromFile(path);
                                break;

                            case ModelSourceType.Directory:
                                Debug.Log($"[XybridModelService] Loading '{asset.modelId}' from directory: {path}");
                                using (var dirLoader = ModelLoader.FromDirectory(path))
                                    model = dirLoader.Load();
                                break;

                            case ModelSourceType.Bundle:
                            default:
                                bool bundleExists = !string.IsNullOrEmpty(path) && System.IO.File.Exists(path);
                                if (bundleExists)
                                {
                                    Debug.Log($"[XybridModelService] Loading '{asset.modelId}' from bundle: {path}");
                                    model = XybridClient.LoadModelFromBundle(path);
                                }
                                else
                                {
                                    Debug.Log($"[XybridModelService] Loading '{asset.modelId}' from registry (bundle not found at: {path})");
                                    model = XybridClient.LoadModel(asset.modelId);
                                }
                                break;
                        }
                        Debug.Log($"[XybridModelService] Loaded '{asset.modelId}' in {sw.ElapsedMilliseconds}ms");

                        loaded.Add(new KeyValuePair<string, ModelEntry>(
                            asset.modelId,
                            new ModelEntry { Model = model, Asset = asset }
                        ));
                    }
                });

                // Populate dictionary on main thread (no contention)
                foreach (var kvp in loaded)
                    _models[kvp.Key] = kvp.Value;
            }

            // Load raw GGUF files (auto-generates metadata from binary header)
            if (_ggufModelPaths != null && _ggufModelPaths.Length > 0)
            {
                var ggufLoaded = new List<KeyValuePair<string, ModelEntry>>();

                await Task.Run(() =>
                {
                    if (!XybridClient.IsInitialized)
                        XybridClient.Initialize();

                    foreach (var rawPath in _ggufModelPaths)
                    {
                        if (string.IsNullOrWhiteSpace(rawPath)) continue;

                        // Resolve path: if not absolute, treat as relative to StreamingAssets
                        string resolvedPath = System.IO.Path.IsPathRooted(rawPath)
                            ? rawPath
                            : System.IO.Path.Combine(Application.streamingAssetsPath, rawPath);

                        if (!System.IO.File.Exists(resolvedPath))
                        {
                            Debug.LogWarning($"[XybridModelService] GGUF file not found: {resolvedPath}");
                            continue;
                        }

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        Debug.Log($"[XybridModelService] Loading GGUF: {resolvedPath}");

                        Model model = XybridClient.LoadModelFromFile(resolvedPath);
                        string modelId = model.ModelId;
                        Debug.Log($"[XybridModelService] Loaded GGUF '{modelId}' in {sw.ElapsedMilliseconds}ms");

                        ggufLoaded.Add(new KeyValuePair<string, ModelEntry>(
                            modelId,
                            new ModelEntry { Model = model, Asset = null, Task = "text-generation" }
                        ));
                    }
                });

                foreach (var kvp in ggufLoaded)
                    _models[kvp.Key] = kvp.Value;
            }

            // Legacy fallback: single model ID string. Only when nothing above loaded a model,
            // otherwise it downloads an extra LLM from the registry on top of the configured assets.
            else if (_models.Count == 0 && !string.IsNullOrWhiteSpace(_modelId))
            {
                Model model = null;
                await Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    XybridClient.Initialize();
                    Debug.Log($"[XybridModelService] XybridClient.Initialize() took {sw.ElapsedMilliseconds}ms");

                    sw.Restart();
                    Debug.Log($"[XybridModelService] Loading '{_modelId}' from registry...");
                    model = XybridClient.LoadModel(_modelId);
                    Debug.Log($"[XybridModelService] Loaded '{_modelId}' in {sw.ElapsedMilliseconds}ms");
                });
                _models[_modelId] = new ModelEntry { Model = model, Asset = null };
            }
            else if (_models.Count == 0)
            {
                throw new InvalidOperationException(
                    "No model assets assigned. Configure Model Assets in the Inspector on XybridModelService.");
            }

            _sdkVersion = XybridClient.Version;
            _isReady = true;
            totalSw.Stop();

            foreach (var kvp in _models)
            {
                string task = kvp.Value.Asset != null
                    ? kvp.Value.Asset.task
                    : kvp.Value.Task ?? "unknown";
                Debug.Log($"[XybridModelService] Ready: {kvp.Key} ({task}), SDK v{_sdkVersion}");
            }
            Debug.Log($"[XybridModelService] Total initialization took {totalSw.ElapsedMilliseconds}ms");
        }

        // ================================================================
        // Model accessors
        // ================================================================

        /// <summary>
        /// Get a loaded model by its model ID.
        /// </summary>
        public Model GetModel(string modelId)
        {
            return _models.TryGetValue(modelId, out var entry) ? entry.Model : null;
        }

        private ModelEntry GetEntryByTask(params string[] taskNames)
        {
            foreach (var kvp in _models)
            {
                // Check asset task (for .xyb models) or Task override (for raw GGUF models)
                string t = kvp.Value.Asset != null
                    ? kvp.Value.Asset.task?.ToLowerInvariant() ?? ""
                    : kvp.Value.Task?.ToLowerInvariant() ?? "";
                foreach (var taskName in taskNames)
                {
                    if (t == taskName) return kvp.Value;
                }
            }
            // Fallback: if only one model loaded (legacy), return it for LLM queries
            if (taskNames.Length > 0 && (taskNames[0] == "text-generation" || taskNames[0] == "llm")
                && _models.Count == 1)
            {
                foreach (var entry in _models.Values)
                    return entry;
            }
            return null;
        }

        private ModelEntry GetLLMEntry() => GetEntryByTask("text-generation", "llm", "chat");
        private ModelEntry GetTTSEntry() => GetEntryByTask("text-to-speech", "tts");

        // ================================================================
        // LLM inference (backward-compatible API)
        // ================================================================

        /// <summary>
        /// Run inference with ConversationContext (system prompt + history managed natively).
        /// </summary>
        public async Task<DialogueResponse> RunInferenceAsync(string userInput, ConversationContext context)
        {
            if (!_isReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var entry = GetLLMEntry();
            if (entry == null)
                return DialogueResponse.FromError("No LLM model loaded");

            await WaitForegroundAsync(entry);
            try
            {
                string result = null;
                uint latency = 0;
                string error = null;

                await Task.Run(() =>
                {
                    using (var envelope = Envelope.Text(userInput))
                    using (var inferenceResult = entry.Model.Run(envelope, context))
                    {
                        if (inferenceResult.Success)
                        {
                            result = inferenceResult.Text;
                            latency = inferenceResult.LatencyMs;
                        }
                        else
                        {
                            error = inferenceResult.Error ?? "Inference failed";
                        }
                    }
                });

                if (error != null)
                    return DialogueResponse.FromError(error);

                return new DialogueResponse
                {
                    Text = result,
                    Success = true,
                    LatencyMs = latency,
                    ModelId = entry.Model.ModelId,
                    InferenceLocation = "device"
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[XybridModelService] Inference failed: {ex.Message}");
                return DialogueResponse.FromError(ex.Message);
            }
            finally
            {
                entry.Lock.Release();
            }
        }

        /// <summary>
        /// Run streaming inference with ConversationContext.
        /// </summary>
        public async Task<DialogueResponse> RunStreamingAsync(string userInput, ConversationContext context, Action<string> onToken)
        {
            if (!_isReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var entry = GetLLMEntry();
            if (entry == null)
                return DialogueResponse.FromError("No LLM model loaded");

            await WaitForegroundAsync(entry);
            try
            {
                string result = null;
                uint latency = 0;
                string error = null;

                await Task.Run(() =>
                {
                    using (var envelope = Envelope.Text(userInput))
                    using (var inferenceResult = entry.Model.RunStreaming(envelope, context, token =>
                    {
                        onToken?.Invoke(token.Token);
                    }))
                    {
                        if (inferenceResult.Success)
                        {
                            result = inferenceResult.Text;
                            latency = inferenceResult.LatencyMs;
                        }
                        else
                        {
                            error = inferenceResult.Error ?? "Streaming inference failed";
                        }
                    }
                });

                if (error != null)
                {
                    Debug.LogWarning($"[XybridModelService] Streaming inference returned an error: {error}");
                    return DialogueResponse.FromError(error);
                }

                return new DialogueResponse
                {
                    Text = result,
                    Success = true,
                    LatencyMs = latency,
                    ModelId = entry.Model.ModelId,
                    InferenceLocation = "device"
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[XybridModelService] Streaming inference failed: {ex.Message}");
                return DialogueResponse.FromError(ex.Message);
            }
            finally
            {
                entry.Lock.Release();
            }
        }

        // ================================================================
        // Background (low-priority) LLM runs
        // ================================================================

        /// <summary>Result of <see cref="RunBackgroundAsync"/> with its queue and inference timings.</summary>
        public sealed class BackgroundRunResult
        {
            public bool Success;
            public string Text;
            public string Error;
            public double QueueWaitMs;
            public double InferenceMs;
            public string ModelId;
        }

        private static async Task WaitForegroundAsync(ModelEntry entry)
        {
            Interlocked.Increment(ref entry.ForegroundPending);
            try
            {
                await entry.Lock.WaitAsync();
            }
            finally
            {
                Interlocked.Decrement(ref entry.ForegroundPending);
            }
        }

        /// <summary>
        /// Low-priority LLM run on the shared dialogue model, for background judgments (table reactions).
        /// It never starts while a player-facing request is waiting for the model and can be cancelled until
        /// it starts; once running, a batch run can't be interrupted, so keep <paramref name="config"/>'s
        /// max tokens small. Uses its own <paramref name="context"/>, never an NPC's dialogue history.
        /// </summary>
        public async Task<BackgroundRunResult> RunBackgroundAsync(string userInput, ConversationContext context,
            GenerationConfig config, CancellationToken cancellationToken)
        {
            if (!_isReady)
                return new BackgroundRunResult { Error = "XybridModelService not ready" };

            var entry = GetLLMEntry();
            if (entry == null)
                return new BackgroundRunResult { Error = "No LLM model loaded" };

            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref entry.ForegroundPending) == 0 && entry.Lock.Wait(0))
                {
                    // A reply may have queued between the check and the acquire: let it go first
                    if (Volatile.Read(ref entry.ForegroundPending) == 0) break;
                    entry.Lock.Release();
                }
                await Task.Delay(25, cancellationToken);
            }
            double queueWaitMs = waited.Elapsed.TotalMilliseconds;

            try
            {
                var result = new BackgroundRunResult { QueueWaitMs = queueWaitMs, ModelId = entry.Model.ModelId };
                var inference = System.Diagnostics.Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    using (var envelope = Envelope.Text(userInput))
                    using (var inferenceResult = entry.Model.Run(envelope, context, config, cancellationToken))
                    {
                        result.Success = inferenceResult.Success;
                        result.Text = inferenceResult.Text;
                        result.Error = inferenceResult.Success ? null : inferenceResult.Error ?? "Inference failed";
                    }
                });
                result.InferenceMs = inference.Elapsed.TotalMilliseconds;
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[XybridModelService] Background inference failed: {ex.Message}");
                return new BackgroundRunResult { Error = ex.Message, QueueWaitMs = queueWaitMs };
            }
            finally
            {
                entry.Lock.Release();
            }
        }

        // ================================================================
        // TTS inference
        // ================================================================

        /// <summary>
        /// Run TTS inference to generate audio from text.
        /// Returns raw PCM bytes (16-bit signed LE, 24kHz, mono), or null on failure.
        /// Phonon picks the voice per call and ignores <paramref name="speed"/>. A line it cut off at its
        /// per-chunk frame limit still plays: the partial audio is returned with a warning.
        /// </summary>
        public async Task<byte[]> RunTTSAsync(string text, string voiceId = null, double speed = 1.0)
        {
            // Phonon mumbles up to its frame limit on a line with no words, such as "..."
            if (!HasSpeakableText(text)) return null;

            var entry = GetTTSEntry();
            if (entry == null)
            {
                Debug.LogWarning("[XybridModelService] No TTS model loaded");
                return null;
            }

            await entry.Lock.WaitAsync();
            try
            {
                byte[] audioBytes = null;
                await Task.Run(() =>
                {
                    Envelope envelope = string.IsNullOrEmpty(voiceId) && speed == 1.0
                        ? Envelope.Text(text)
                        : Envelope.Text(text, string.IsNullOrEmpty(voiceId) ? null : voiceId, speed);
                    using (InferenceResult result = entry.Model.Run(envelope))
                    {
                        if (result.SpeechStatus == TtsStatus.Limited && result.HasAudio)
                            Debug.LogWarning($"[XybridModelService] TTS hit the frame limit in {result.LimitedChunks} chunk(s); playing partial audio: \"{text}\"");
                        else
                            result.ThrowIfFailed();
                        if (!result.HasAudio)
                            throw new InvalidOperationException($"TTS produced no audio (output: {result.OutputType})");
                        audioBytes = result.AudioBytes;
                    }
                });
                return audioBytes;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[XybridModelService] TTS failed: {ex.Message}");
                return null;
            }
            finally
            {
                entry.Lock.Release();
            }
        }

        private static bool HasSpeakableText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (char.IsLetterOrDigit(c)) return true;
            return false;
        }

        // ================================================================
        // Telemetry
        // ================================================================

        // Logs here stay generic: never the key, a configured value or a raw
        // exception message (SDK errors can echo request details).
        private void TryInitializeTelemetry()
        {
            string apiKey;
            TelemetryKeySource source;
            try
            {
                apiKey = TelemetryApiKey.Resolve(DotEnv.Get, out source);
            }
            catch (Exception)
            {
                Debug.LogWarning("[XybridModelService] Telemetry disabled: the API key configuration could not be read.");
                return;
            }

            if (apiKey == null)
            {
                Debug.Log("[XybridModelService] Telemetry disabled: no API key configured.");
                return;
            }

            try
            {
                using (var config = new TelemetryConfig(apiKey)
                    .WithDeviceAttribute("platform", Application.platform.ToString())
                    .WithBatchSize(32)
                    .WithFlushInterval(TimeSpan.FromSeconds(30)))
                {
                    XybridClient.InitializeTelemetry(config);
                }
                _telemetryActive = true;
                Debug.Log($"[XybridModelService] Telemetry initialized (key source: {source}).");
            }
            catch (Exception)
            {
                Debug.LogWarning("[XybridModelService] Telemetry disabled: initialization failed. Models load without it.");
            }
        }

        // ================================================================
        // Lifecycle
        // ================================================================

        private void OnApplicationPause(bool paused)
        {
            if (paused && _telemetryActive) XybridClient.FlushTelemetry();
        }

        private void OnDestroy()
        {
            if (_telemetryActive)
            {
                XybridClient.FlushTelemetry();
                XybridClient.ShutdownTelemetry();
                _telemetryActive = false;
            }

            foreach (var entry in _models.Values)
                entry.Model?.Dispose();
            _models.Clear();
            _isReady = false;
            if (Instance == this) Instance = null;
        }

        // ================================================================
        // Internal
        // ================================================================

        private class ModelEntry
        {
            public Model Model;
            public XybridModelAsset Asset;
            /// <summary>Task override for models loaded without an asset (e.g., raw GGUF).</summary>
            public string Task;
            public readonly SemaphoreSlim Lock = new SemaphoreSlim(1, 1);
            /// <summary>Player-facing requests waiting for <see cref="Lock"/>; background runs yield to them.</summary>
            public int ForegroundPending;
        }
    }
}
