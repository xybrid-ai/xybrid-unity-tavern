using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

        [Header("Telemetry (optional)")]
        [Tooltip("Env var name holding the telemetry API key. Read from project-root .env in Editor, or process env in builds. Leave empty to disable.")]
        [SerializeField] private string _telemetryApiKeyEnvVar = "xybrid_api_key";

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

            // Legacy fallback: single model ID string
            else if (!string.IsNullOrWhiteSpace(_modelId))
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
            else
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

            await entry.Lock.WaitAsync();
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

            await entry.Lock.WaitAsync();
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
                Debug.LogError($"[XybridModelService] Streaming inference failed: {ex.Message}");
                return DialogueResponse.FromError(ex.Message);
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
        /// </summary>
        public async Task<byte[]> RunTTSAsync(string text, string voiceId = null, double speed = 1.0)
        {
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
                    if (string.IsNullOrEmpty(voiceId) && speed == 1.0)
                        audioBytes = entry.Model.RunTts(text);
                    else
                        audioBytes = entry.Model.RunTts(
                            text,
                            string.IsNullOrEmpty(voiceId) ? null : voiceId,
                            speed);
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

        // ================================================================
        // Telemetry
        // ================================================================

        private void TryInitializeTelemetry()
        {
            if (string.IsNullOrWhiteSpace(_telemetryApiKeyEnvVar))
            {
                Debug.Log("[XybridModelService] Telemetry disabled (no API key env var configured).");
                return;
            }

            string apiKey = DotEnv.Get(_telemetryApiKeyEnvVar);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Debug.LogWarning($"[XybridModelService] Telemetry disabled: '{_telemetryApiKeyEnvVar}' not found in .env or environment.");
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
                Debug.Log("[XybridModelService] Telemetry initialized → ingest.xybrid.dev");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[XybridModelService] Telemetry init failed: {ex.Message}");
            }
        }

        // ================================================================
        // Lifecycle
        // ================================================================

        private void OnApplicationPause(bool paused)
        {
            if (paused) XybridClient.FlushTelemetry();
        }

        private void OnDestroy()
        {
            XybridClient.FlushTelemetry();
            XybridClient.ShutdownTelemetry();

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
        }
    }
}
