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
    /// Uses XybridModelService for inference and per-NPC ConversationContext for history.
    /// </summary>
    public class XybridDialogueProvider : IDialogueProvider, IDisposable
    {
        private readonly Dictionary<string, ConversationContext> _npcContexts
            = new Dictionary<string, ConversationContext>();

        private WorldLore _worldLore;

        public string ProviderName => $"Xybrid ({XybridModelService.Instance?.ModelId ?? "not ready"})";
        public bool SupportsFreeInput => true;
        public bool SupportsStreaming => true;

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
        // Non-streaming methods (IDialogueProvider)
        // ================================================================

        public async Task<DialogueResponse> GetGreetingAsync(NPCIdentity npc)
        {
            var service = XybridModelService.Instance;
            if (service == null || !service.IsReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var context = GetOrCreateContext(npc);
            string greetingPrompt = "A traveler has just approached you. Greet them naturally, in character.";

            var response = await service.RunInferenceAsync(greetingPrompt, context);
            if (response.Success)
                FinalizeResponse(response, context, greetingPrompt);

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

            var context = GetOrCreateContext(npc);

            var response = await service.RunInferenceAsync(playerInput, context);
            if (response.Success)
                FinalizeResponse(response, context, playerInput);

            return response;
        }

        // ================================================================
        // Streaming methods (IDialogueProvider)
        // ================================================================

        public async Task<DialogueResponse> GetGreetingStreamingAsync(NPCIdentity npc, Action<string> onToken)
        {
            var service = XybridModelService.Instance;
            if (service == null || !service.IsReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var context = GetOrCreateContext(npc);
            string greetingPrompt = "A traveler has just approached you. Greet them naturally, in character.";

            var response = await service.RunStreamingInferenceAsync(
                greetingPrompt,
                context,
                streamToken => onToken?.Invoke(streamToken.Token));

            if (response.Success)
                FinalizeResponse(response, context, greetingPrompt);

            return response;
        }

        public async Task<DialogueResponse> GetResponseStreamingAsync(
            NPCIdentity npc,
            string playerInput,
            string[] conversationHistory,
            Action<string> onToken)
        {
            var service = XybridModelService.Instance;
            if (service == null || !service.IsReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var context = GetOrCreateContext(npc);

            var response = await service.RunStreamingInferenceAsync(
                playerInput,
                context,
                streamToken => onToken?.Invoke(streamToken.Token));

            if (response.Success)
                FinalizeResponse(response, context, playerInput);

            return response;
        }

        public Task<string[]> GetPlayerOptionsAsync(NPCIdentity npc, int exchangeIndex)
        {
            // AI provider supports free-form input, no predefined options
            return Task.FromResult<string[]>(null);
        }

        // ================================================================
        // Context management
        // ================================================================

        private ConversationContext GetOrCreateContext(NPCIdentity npc)
        {
            if (_npcContexts.TryGetValue(npc.npcName, out var existing))
                return existing;

            var context = new ConversationContext(npc.npcName);
            context.SetSystem(BuildSystemPrompt(npc));
            context.SetMaxHistoryLength(20); // 10 exchanges before FIFO pruning

            _npcContexts[npc.npcName] = context;
            Debug.Log($"[XybridProvider] Created context for {npc.npcName} (history max=20)");

            return context;
        }

        /// <summary>
        /// Clear conversation history for an NPC. Preserves the system prompt.
        /// Called by DialogueManagerV2 when dialogue ends.
        /// </summary>
        public void ClearNPCContext(string npcName)
        {
            if (_npcContexts.TryGetValue(npcName, out var context))
            {
                context.Clear();
                Debug.Log($"[XybridProvider] Cleared context for {npcName}");
            }
        }

        // ================================================================
        // System prompt construction
        // ================================================================

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
        // Response post-processing
        // ================================================================

        /// <summary>
        /// Clean, push to context, and update stats after a successful inference.
        /// </summary>
        private void FinalizeResponse(DialogueResponse response, ConversationContext context, string userInput)
        {
            string cleaned = CleanResponse(response.Text);

            // Push both sides to context — cleaned version so model memory matches display
            context.Push(userInput, MessageRole.User);
            context.Push(cleaned, MessageRole.Assistant);

            response.Text = cleaned;
            LastLatencyMs = response.LatencyMs;
            LastInferenceLocation = response.InferenceLocation;
        }

        private string CleanResponse(string response)
        {
            if (string.IsNullOrEmpty(response)) return "...";

            // Trim whitespace
            response = response.Trim();

            // Remove prompt leakage patterns like "CharacterName says:" or "CharacterName responds:"
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

            // Remove surrounding quotes if present
            if (response.StartsWith("\"") && response.EndsWith("\""))
            {
                response = response.Substring(1, response.Length - 2);
            }

            if (response.StartsWith("'") && response.EndsWith("'"))
            {
                response = response.Substring(1, response.Length - 2);
            }

            // If response contains multiple paragraphs, take only the first meaningful one
            if (response.Contains("\n\n"))
            {
                var paragraphs = response.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (paragraphs.Length > 0)
                {
                    response = paragraphs[0].Trim();
                }
            }

            // Truncate if too long (keeps the tavern vibe snappy)
            if (response.Length > 200)
            {
                int lastPeriod = response.LastIndexOf('.', 200);
                if (lastPeriod > 50)
                {
                    response = response.Substring(0, lastPeriod + 1);
                }
                else
                {
                    response = response.Substring(0, 197) + "...";
                }
            }

            if (string.IsNullOrWhiteSpace(response))
            {
                return "...";
            }

            return response;
        }

        // ================================================================
        // Cleanup
        // ================================================================

        public void Dispose()
        {
            DisposeAllContexts();
        }

        private void DisposeAllContexts()
        {
            foreach (var ctx in _npcContexts.Values)
            {
                ctx?.Dispose();
            }
            _npcContexts.Clear();
        }
    }
}
