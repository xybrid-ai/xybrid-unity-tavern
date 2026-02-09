using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Dialogue provider that uses Xybrid SDK for AI-generated responses.
    /// Builds full prompts with system context + conversation history baked in,
    /// since native ConversationContext has a crash bug (xybrid_model_run_with_context).
    /// TODO: Switch to native ConversationContext once xybrid-ffi is fixed.
    /// </summary>
    public class XybridDialogueProvider : IDialogueProvider
    {
        private const int MaxHistoryExchanges = 10; // Keep last 10 exchanges (20 messages)

        private readonly Dictionary<string, List<HistoryEntry>> _npcHistory
            = new Dictionary<string, List<HistoryEntry>>();

        private WorldLore _worldLore;

        public string ProviderName => $"Xybrid ({XybridModelService.Instance?.ModelId ?? "not ready"})";
        public bool SupportsFreeInput => true;
        public bool SupportsStreaming => false;

        // Last inference stats for the demo overlay
        public uint LastLatencyMs { get; private set; }
        public string LastInferenceLocation { get; private set; } = "device";

        /// <summary>
        /// Set the world lore reference for system prompts.
        /// </summary>
        public void SetWorldLore(WorldLore lore)
        {
            _worldLore = lore;
        }

        // ================================================================
        // IDialogueProvider — non-streaming
        // ================================================================

        public async Task<DialogueResponse> GetGreetingAsync(NPCIdentity npc)
        {
            var service = XybridModelService.Instance;
            if (service == null || !service.IsReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var history = GetOrCreateHistory(npc.npcName);
            string greetingInput = "A traveler has just approached you. Greet them naturally, in character.";
            string fullPrompt = BuildFullPrompt(npc, history, greetingInput);

            var response = await service.RunInferenceAsync(fullPrompt);

            if (response.Success)
            {
                response.Text = CleanResponse(response.Text);
                PushHistory(history, greetingInput, response.Text);
                LastLatencyMs = response.LatencyMs;
                LastInferenceLocation = response.InferenceLocation;
            }

            return response;
        }

        public async Task<DialogueResponse> GetResponseAsync(
            NPCIdentity npc,
            string playerInput,
            string[] conversationHistory)
        {
            var service = XybridModelService.Instance;
            if (service == null || !service.IsReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var history = GetOrCreateHistory(npc.npcName);
            string fullPrompt = BuildFullPrompt(npc, history, playerInput);

            var response = await service.RunInferenceAsync(fullPrompt);

            if (response.Success)
            {
                response.Text = CleanResponse(response.Text);
                PushHistory(history, playerInput, response.Text);
                LastLatencyMs = response.LatencyMs;
                LastInferenceLocation = response.InferenceLocation;
            }

            return response;
        }

        // ================================================================
        // Streaming — uses model.RunStreaming(envelope, onToken), no context
        // ================================================================

        public async Task<DialogueResponse> GetGreetingStreamingAsync(NPCIdentity npc, Action<string> onToken)
        {
            var service = XybridModelService.Instance;
            if (service == null || !service.IsReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var history = GetOrCreateHistory(npc.npcName);
            string greetingInput = "A traveler has just approached you. Greet them naturally, in character.";
            string fullPrompt = BuildFullPrompt(npc, history, greetingInput);

            Debug.Log($"[XybridProvider] Streaming greeting for {npc.npcName}");
            var response = await service.RunStreamingAsync(fullPrompt, onToken);

            if (response.Success)
            {
                response.Text = CleanResponse(response.Text);
                PushHistory(history, greetingInput, response.Text);
                LastLatencyMs = response.LatencyMs;
                LastInferenceLocation = response.InferenceLocation;
            }

            return response;
        }

        public async Task<DialogueResponse> GetResponseStreamingAsync(
            NPCIdentity npc, string playerInput, string[] conversationHistory, Action<string> onToken)
        {
            var service = XybridModelService.Instance;
            if (service == null || !service.IsReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var history = GetOrCreateHistory(npc.npcName);
            string fullPrompt = BuildFullPrompt(npc, history, playerInput);

            Debug.Log($"[XybridProvider] Streaming response for {npc.npcName}, input=\"{playerInput}\"");
            var response = await service.RunStreamingAsync(fullPrompt, onToken);

            if (response.Success)
            {
                response.Text = CleanResponse(response.Text);
                PushHistory(history, playerInput, response.Text);
                LastLatencyMs = response.LatencyMs;
                LastInferenceLocation = response.InferenceLocation;
            }

            return response;
        }

        public Task<string[]> GetPlayerOptionsAsync(NPCIdentity npc, int exchangeIndex)
        {
            return Task.FromResult<string[]>(null);
        }

        // ================================================================
        // Prompt construction
        // ================================================================

        /// <summary>
        /// Builds the complete prompt: system context + conversation history + current user input.
        /// This replaces native ConversationContext until the FFI bug is fixed.
        /// </summary>
        private string BuildFullPrompt(NPCIdentity npc, List<HistoryEntry> history, string userInput)
        {
            var sb = new StringBuilder();

            // System prompt
            sb.Append(BuildSystemPrompt(npc));
            sb.AppendLine();

            // Conversation history
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

            // Current input
            sb.AppendLine($"Traveler: {userInput}");
            sb.Append($"{npc.npcName}:");

            return sb.ToString();
        }

        private string BuildSystemPrompt(NPCIdentity npc)
        {
            var sb = new StringBuilder();

            // World lore
            sb.Append(BuildWorldSection());

            // Setting context
            sb.AppendLine("=== SETTING ===");
            sb.AppendLine("The Rusty Flagon tavern. Evening time, fire crackling, ambient chatter.");
            sb.AppendLine();

            // Character card
            sb.AppendLine("=== YOUR CHARACTER ===");
            sb.AppendLine($"Name: {npc.npcName}");
            sb.AppendLine($"Role: {npc.description}");
            sb.AppendLine($"Core traits: {npc.personality}");

            // Extended personality if available
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
        // Conversation history (managed in C# — replaces native ConversationContext)
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

            // FIFO pruning
            while (history.Count > MaxHistoryExchanges)
                history.RemoveAt(0);
        }

        /// <summary>
        /// Clear conversation history for an NPC. Called when dialogue ends.
        /// </summary>
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

            // Remove prompt leakage patterns
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

            // Remove surrounding quotes
            if (response.StartsWith("\"") && response.EndsWith("\""))
                response = response.Substring(1, response.Length - 2);
            if (response.StartsWith("'") && response.EndsWith("'"))
                response = response.Substring(1, response.Length - 2);

            // Take only the first paragraph
            if (response.Contains("\n\n"))
            {
                var paragraphs = response.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (paragraphs.Length > 0)
                    response = paragraphs[0].Trim();
            }

            // Truncate if too long
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
