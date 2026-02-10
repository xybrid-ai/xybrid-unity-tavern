using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Xybrid;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Dialogue provider that uses Xybrid SDK for AI-generated responses.
    /// Owns the Model directly. Uses Task.Run to keep inference off the main thread.
    /// </summary>
    public class XybridDialogueProvider : IDialogueProvider, IDisposable
    {
        private const int MaxHistoryExchanges = 10;
        private const string GreetingInput = "A traveler has just approached you. Greet them naturally, in character.";

        private Model _model;
        private bool _initialized;
        private readonly string _modelId;
        private WorldLore _worldLore;

        private readonly Dictionary<string, List<HistoryEntry>> _npcHistory
            = new Dictionary<string, List<HistoryEntry>>();

        public string ProviderName => $"Xybrid ({_modelId})";
        public bool SupportsFreeInput => true;
        public bool SupportsStreaming => true;

        public uint LastLatencyMs { get; private set; }
        public string LastInferenceLocation { get; private set; } = "device";

        public XybridDialogueProvider(string modelId)
        {
            _modelId = modelId;
        }

        public void SetWorldLore(WorldLore lore)
        {
            _worldLore = lore;
        }

        public async Task InitializeAsync()
        {
            if (_initialized) return;

            Debug.Log("[XybridProvider] Initializing SDK...");
            await Task.Run(() =>
            {
                XybridClient.Initialize();
                _model = XybridClient.LoadModel(_modelId);
            });

            _initialized = true;
            Debug.Log($"[XybridProvider] Ready: model={_model.ModelId}, SDK v{XybridClient.Version}");
        }

        // ================================================================
        // IDialogueProvider — thin wrappers delegating to shared core
        // ================================================================

        public Task<DialogueResponse> GetGreetingAsync(NPCIdentity npc)
            => RunDialogueAsync(npc, GreetingInput);

        public Task<DialogueResponse> GetResponseAsync(
            NPCIdentity npc, string playerInput, string[] conversationHistory)
            => RunDialogueAsync(npc, playerInput);

        public Task<DialogueResponse> GetGreetingStreamingAsync(NPCIdentity npc, Action<string> onToken)
            => RunDialogueAsync(npc, GreetingInput, onToken);

        public Task<DialogueResponse> GetResponseStreamingAsync(
            NPCIdentity npc, string playerInput, string[] conversationHistory, Action<string> onToken)
            => RunDialogueAsync(npc, playerInput, onToken);

        public Task<string[]> GetPlayerOptionsAsync(NPCIdentity npc, int exchangeIndex)
            => Task.FromResult<string[]>(null);

        // ================================================================
        // Shared dialogue core
        // ================================================================

        private async Task<DialogueResponse> RunDialogueAsync(
            NPCIdentity npc, string userInput, Action<string> onToken = null)
        {
            if (!_initialized)
                return DialogueResponse.FromError("XybridProvider not initialized");

            var history = GetOrCreateHistory(npc.npcName);
            string fullPrompt = BuildFullPrompt(npc, history, userInput);
            bool streaming = onToken != null;

            Debug.Log($"[XybridProvider] {(streaming ? "Streaming" : "Non-streaming")} for {npc.npcName} ({fullPrompt.Length} chars)");

            var response = streaming
                ? await RunStreamingInferenceAsync(fullPrompt, onToken)
                : await RunInferenceAsync(fullPrompt);

            if (response.Success)
            {
                response.Text = CleanResponse(response.Text);
                PushHistory(history, userInput, response.Text);
                LastLatencyMs = response.LatencyMs;
                LastInferenceLocation = response.InferenceLocation;
            }

            return response;
        }

        // ================================================================
        // Inference — Task.Run to keep main thread free
        // ================================================================

        private async Task<DialogueResponse> RunInferenceAsync(string fullPrompt)
        {
            try
            {
                string result = null;
                uint latency = 0;
                string errorMsg = null;

                await Task.Run(() =>
                {
                    using (var envelope = Envelope.Text(fullPrompt))
                    using (var inferenceResult = _model.Run(envelope))
                    {
                        if (inferenceResult.Success)
                        {
                            result = inferenceResult.Text;
                            latency = inferenceResult.LatencyMs;
                        }
                        else
                        {
                            errorMsg = inferenceResult.Error ?? "Inference returned Success=false";
                        }
                    }
                });

                if (errorMsg != null)
                    return DialogueResponse.FromError(errorMsg);

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
                Debug.LogError($"[XybridProvider] Inference failed: {ex.Message}");
                return DialogueResponse.FromError(ex.Message);
            }
        }

        private async Task<DialogueResponse> RunStreamingInferenceAsync(string fullPrompt, Action<string> onToken)
        {
            try
            {
                string result = null;
                uint latency = 0;
                string errorMsg = null;

                await Task.Run(() =>
                {
                    using (var envelope = Envelope.Text(fullPrompt))
                    using (var inferenceResult = _model.RunStreaming(envelope, token =>
                    {
                        // Debug.Log($"[XybridProvider] Streaming token: {token.Token}");
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
                            errorMsg = inferenceResult.Error ?? "Streaming inference returned Success=false";
                        }
                    }
                });

                if (errorMsg != null)
                    return DialogueResponse.FromError(errorMsg);

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
                Debug.LogError($"[XybridProvider] Streaming inference failed: {ex.Message}");
                return DialogueResponse.FromError(ex.Message);
            }
        }

        // ================================================================
        // Prompt construction
        // ================================================================

        private string BuildFullPrompt(NPCIdentity npc, List<HistoryEntry> history, string userInput)
        {
            var sb = new StringBuilder();

            sb.Append(BuildSystemPrompt(npc));
            sb.AppendLine();

            if (history.Count > 0)
            {
                sb.AppendLine("=== CONVERSATION SO FAR ===");
                foreach (var entry in history)
                {
                    sb.AppendLine($"Traveler: {entry.UserInput}");
                    sb.AppendLine($"{npc.npcName}: {entry.AssistantResponse}");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"Traveler: {userInput}");
            sb.Append($"{npc.npcName}:");

            return sb.ToString();
        }

        private string BuildSystemPrompt(NPCIdentity npc)
        {
            var sb = new StringBuilder();

            sb.Append(BuildWorldSection());

            sb.AppendLine("=== SETTING ===");
            sb.AppendLine("The Rusty Flagon tavern. Evening time, fire crackling, ambient chatter.");
            sb.AppendLine();

            sb.AppendLine("=== YOUR CHARACTER ===");
            sb.AppendLine($"Name: {npc.npcName}");
            sb.AppendLine($"Role: {npc.description}");
            sb.AppendLine($"Core traits: {npc.personality}");

            string extendedPersonality = npc.extendedPersonality;
            if (!string.IsNullOrEmpty(extendedPersonality))
            {
                sb.AppendLine();
                sb.AppendLine("=== CHARACTER DETAILS ===");
                sb.AppendLine(extendedPersonality);
            }

            sb.AppendLine();
            sb.AppendLine("=== RULES ===");
            sb.AppendLine("- Write ONLY your character's spoken words, nothing else");
            sb.AppendLine("- 1-2 sentences maximum");
            sb.AppendLine("- Match the speech style described above");
            sb.AppendLine("- Only reference things your character would know");
            sb.AppendLine("- No quotation marks, no action descriptions, no narration");
            sb.AppendLine("- Stay fully in character");

            return sb.ToString();
        }

        private string BuildWorldSection()
        {
            var sb = new StringBuilder();

            if (_worldLore != null)
            {
                sb.AppendLine("=== WORLD ===");
                if (!string.IsNullOrEmpty(_worldLore.worldOverview))
                    sb.AppendLine(_worldLore.worldOverview);
                if (!string.IsNullOrEmpty(_worldLore.regionDescription))
                    sb.AppendLine(_worldLore.regionDescription);
                sb.AppendLine();

                sb.AppendLine("=== LOCAL AREA ===");
                if (!string.IsNullOrEmpty(_worldLore.localArea))
                    sb.AppendLine(_worldLore.localArea);
                if (!string.IsNullOrEmpty(_worldLore.tavernHistory))
                    sb.AppendLine($"The Tavern: {_worldLore.tavernHistory}");
                sb.AppendLine();

                if (!string.IsNullOrEmpty(_worldLore.recentEvents))
                {
                    sb.AppendLine("=== COMMON KNOWLEDGE (what everyone knows) ===");
                    sb.AppendLine(_worldLore.recentEvents);
                    sb.AppendLine();
                }

                if (!string.IsNullOrEmpty(_worldLore.rumorsAndMysteries))
                {
                    sb.AppendLine("=== RUMORS (may or may not be true) ===");
                    sb.AppendLine(_worldLore.rumorsAndMysteries);
                    sb.AppendLine();
                }

                if (!string.IsNullOrEmpty(_worldLore.knowledgeBoundaries))
                {
                    sb.AppendLine("=== KNOWLEDGE BOUNDARIES (you do NOT know about) ===");
                    sb.AppendLine(_worldLore.knowledgeBoundaries);
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        // ================================================================
        // Conversation history
        // ================================================================

        private List<HistoryEntry> GetOrCreateHistory(string npcName)
        {
            if (!_npcHistory.TryGetValue(npcName, out var history))
            {
                history = new List<HistoryEntry>();
                _npcHistory[npcName] = history;
            }
            return history;
        }

        private void PushHistory(List<HistoryEntry> history, string userInput, string assistantResponse)
        {
            history.Add(new HistoryEntry { UserInput = userInput, AssistantResponse = assistantResponse });

            while (history.Count > MaxHistoryExchanges)
                history.RemoveAt(0);
        }

        public void ClearNPCContext(string npcName)
        {
            if (_npcHistory.TryGetValue(npcName, out var history))
            {
                history.Clear();
                Debug.Log($"[XybridProvider] Cleared history for {npcName}");
            }
        }

        // ================================================================
        // Response post-processing
        // ================================================================

        private string CleanResponse(string response)
        {
            if (string.IsNullOrEmpty(response)) return "...";

            response = response.Trim();

            var leakagePatterns = new[] { " says:", " responds:", " replies:" };
            foreach (var pattern in leakagePatterns)
            {
                int idx = response.LastIndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    string beforePattern = response.Substring(0, idx);
                    int lastNewline = beforePattern.LastIndexOf('\n');
                    if (lastNewline >= 0 && idx - lastNewline < 50)
                    {
                        response = beforePattern.Substring(0, lastNewline).Trim();
                    }
                }
            }

            if (response.StartsWith("\"") && response.EndsWith("\""))
                response = response.Substring(1, response.Length - 2);
            if (response.StartsWith("'") && response.EndsWith("'"))
                response = response.Substring(1, response.Length - 2);

            if (response.Contains("\n\n"))
            {
                var paragraphs = response.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (paragraphs.Length > 0)
                    response = paragraphs[0].Trim();
            }

            if (response.Length > 200)
            {
                int lastPeriod = response.LastIndexOf('.', 200);
                if (lastPeriod > 50)
                    response = response.Substring(0, lastPeriod + 1);
                else
                    response = response.Substring(0, 197) + "...";
            }

            if (string.IsNullOrWhiteSpace(response))
                return "...";

            return response;
        }

        // ================================================================
        // Dispose + Types
        // ================================================================

        public void Dispose()
        {
            Debug.Log("[XybridProvider] Disposing model...");
            _model?.Dispose();
            _model = null;
            _initialized = false;
        }

        private struct HistoryEntry
        {
            public string UserInput;
            public string AssistantResponse;
        }
    }
}
