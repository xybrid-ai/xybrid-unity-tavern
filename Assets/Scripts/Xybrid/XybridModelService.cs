using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Xybrid;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Singleton service that owns the Xybrid model instance and serializes inference requests.
    /// Attach to a GameObject in the scene. Persists across scene loads.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class XybridModelService : MonoBehaviour
    {
        public static XybridModelService Instance { get; private set; }

        [SerializeField] private string _modelId;
        [SerializeField] private bool _persistAcrossScenes = true;

        private Model _model;
        private bool _isReady;
        private string _sdkVersion;
        private readonly SemaphoreSlim _inferenceLock = new SemaphoreSlim(1, 1);

        public bool IsReady => _isReady;
        public bool IsProcessing => _inferenceLock.CurrentCount == 0;
        public string ModelId => _model?.ModelId ?? _modelId;
        public string SdkVersion => _sdkVersion;

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
        /// Initialize the Xybrid SDK and load the model. Idempotent.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isReady) return;

            if (string.IsNullOrWhiteSpace(_modelId))
                throw new InvalidOperationException("Model ID is not set. Configure it in the Inspector on XybridModelService.");

            await Task.Run(() =>
            {
                XybridClient.Initialize();
                _model = XybridClient.LoadModel(_modelId);
            });

            _sdkVersion = XybridClient.Version;
            _isReady = true;
            Debug.Log($"[XybridModelService] Ready: model={_model.ModelId}, SDK v{_sdkVersion}");
        }

        /// <summary>
        /// Run inference with ConversationContext (system prompt + history managed natively).
        /// The envelope carries the current user input; context provides system prompt and history.
        /// </summary>
        public async Task<DialogueResponse> RunInferenceAsync(string userInput, ConversationContext context)
        {
            if (!_isReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            await _inferenceLock.WaitAsync();
            try
            {
                string result = null;
                uint latency = 0;
                string error = null;

                await Task.Run(() =>
                {
                    using (var envelope = Envelope.Text(userInput))
                    using (var inferenceResult = _model.Run(envelope, context))
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
                    ModelId = _model.ModelId,
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
                _inferenceLock.Release();
            }
        }

        /// <summary>
        /// Run streaming inference with ConversationContext.
        /// </summary>
        public async Task<DialogueResponse> RunStreamingAsync(string userInput, ConversationContext context, Action<string> onToken)
        {
            if (!_isReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            await _inferenceLock.WaitAsync();
            try
            {
                string result = null;
                uint latency = 0;
                string error = null;

                await Task.Run(() =>
                {
                    using (var envelope = Envelope.Text(userInput))
                    using (var inferenceResult = _model.RunStreaming(envelope, context, token =>
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
                    ModelId = _model.ModelId,
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
                _inferenceLock.Release();
            }
        }

        private void OnDestroy()
        {
            _model?.Dispose();
            _model = null;
            _isReady = false;
            if (Instance == this) Instance = null;
        }
    }
}
