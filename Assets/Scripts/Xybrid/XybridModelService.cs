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

        [Header("Settings")]
        [SerializeField] private bool _persistAcrossScenes = true;

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
            // Use model assets if configured
            if (_modelAssets != null && _modelAssets.Length > 0)
            {
                // Build models on background thread, collect into local list first
                var loaded = new List<KeyValuePair<string, ModelEntry>>();

                await Task.Run(() =>
                {
                    XybridClient.Initialize();

                    foreach (var asset in _modelAssets)
                    {
                        if (asset == null) continue;

                        string path = asset.GetRuntimePath();
                        Model model;

                        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                        {
                            model = XybridClient.LoadModelFromBundle(path);
                        }
                        else
                        {
                            // Fall back to registry if bundle not in StreamingAssets
                            model = XybridClient.LoadModel(asset.modelId);
                        }

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
            // Legacy fallback: single model ID string
            else if (!string.IsNullOrWhiteSpace(_modelId))
            {
                Model model = null;
                await Task.Run(() =>
                {
                    XybridClient.Initialize();
                    model = XybridClient.LoadModel(_modelId);
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

            foreach (var kvp in _models)
            {
                string task = kvp.Value.Asset != null ? kvp.Value.Asset.task : "unknown";
                Debug.Log($"[XybridModelService] Loaded: {kvp.Key} ({task}), SDK v{_sdkVersion}");
            }
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
                if (kvp.Value.Asset == null) continue;
                string t = kvp.Value.Asset.task?.ToLowerInvariant() ?? "";
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
        public async Task<byte[]> RunTTSAsync(string text, string voiceId = null)
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
                    audioBytes = string.IsNullOrEmpty(voiceId)
                        ? entry.Model.RunTts(text)
                        : entry.Model.RunTts(text, voiceId);
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
        // Lifecycle
        // ================================================================

        private void OnDestroy()
        {
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
            public readonly SemaphoreSlim Lock = new SemaphoreSlim(1, 1);
        }
    }
}
