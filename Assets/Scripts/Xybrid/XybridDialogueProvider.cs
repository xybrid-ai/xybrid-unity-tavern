using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Dialogue provider that uses Xybrid SDK for AI-generated responses.
    /// Delegates inference to XybridModelService (single model instance).
    /// Owns prompt construction, conversation history, and response cleaning.
    /// </summary>
    public class XybridDialogueProvider : IDialogueProvider
    {
        private const int MaxHistoryExchanges = 10;
        private const string GreetingInput = "A traveler has just approached you. Greet them naturally, in character.";

        private readonly XybridModelService _service;
        private WorldLore _worldLore;

        private readonly Dictionary<string, List<HistoryEntry>> _npcHistory
            = new Dictionary<string, List<HistoryEntry>>();

        public string ProviderName => $"Xybrid ({_service.ModelId})";
        public bool SupportsFreeInput => true;
        public bool SupportsStreaming => true;

        public uint LastLatencyMs { get; private set; }
        public string LastInferenceLocation { get; private set; } = "device";

        public XybridDialogueProvider(XybridModelService service)
        {
            _service = service;
        }

        public void SetWorldLore(WorldLore lore)
        {
            _worldLore = lore;
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
            if (!_service.IsReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var history = GetOrCreateHistory(npc.npcName);
            string fullPrompt = BuildFullPrompt(npc, history, userInput);
            bool streaming = onToken != null;

            Debug.Log($"[XybridProvider] {(streaming ? "Streaming" : "Non-streaming")} for {npc.npcName} ({fullPrompt.Length} chars)");

            var response = streaming
                ? await _service.RunStreamingAsync(fullPrompt, onToken)
                : await _service.RunInferenceAsync(fullPrompt);

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
        // Prompt construction
        // ================================================================

        private const int MaxPromptLength = 1800;

        private string BuildFullPrompt(NPCIdentity npc, List<HistoryEntry> history, string userInput)
        {
            var sb = new StringBuilder();

            sb.Append(BuildSystemPrompt(npc));
            sb.AppendLine();

            // TODO: Re-enable conversation history in prompt once model context
            // window is large enough to handle it without degrading responses.
            // History is still tracked for the ConversationHistoryUI sidebar.

            sb.AppendLine($"Traveler: {userInput}");
            sb.Append($"{npc.npcName}:");

            string prompt = sb.ToString();

            if (prompt.Length > MaxPromptLength)
                Debug.LogWarning($"[XybridProvider] Prompt for {npc.npcName} is {prompt.Length} chars (limit: {MaxPromptLength}). Risk of native FFI crash.");

            return prompt;
        }

        private string BuildSystemPrompt(NPCIdentity npc)
        {
            var sb = new StringBuilder();

            // "You are" framing — small completion models follow this better than
            // structured tags which they tend to echo back.
            sb.Append($"You are {npc.npcName}, {npc.description}.");
            sb.AppendLine($" {npc.personality}.");

            // Layer 1: World context (condensed)
            if (_worldLore != null)
            {
                string setting = !string.IsNullOrEmpty(_worldLore.settingBrief)
                    ? _worldLore.settingBrief
                    : "The Rusty Flagon tavern. Evening, fire crackling.";

                if (!string.IsNullOrEmpty(_worldLore.worldBrief))
                    sb.AppendLine($"{_worldLore.worldBrief} {setting}");
                else
                    sb.AppendLine(setting);
            }

            // Layer 2: NPC-specific details (knowledge, speech style, relationships)
            string extendedPersonality = npc.extendedPersonality;
            if (!string.IsNullOrEmpty(extendedPersonality))
                sb.AppendLine(extendedPersonality);

            // Rules — direct instruction
            sb.AppendLine("Reply in 1-2 sentences as spoken dialogue only. No quotes, no narration, no actions.");

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
        // Types
        // ================================================================

        private struct HistoryEntry
        {
            public string UserInput;
            public string AssistantResponse;
        }
    }
}
