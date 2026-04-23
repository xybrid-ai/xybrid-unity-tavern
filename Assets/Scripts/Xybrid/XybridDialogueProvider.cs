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
    /// Delegates inference to XybridModelService (single model instance).
    /// Manages per-NPC ConversationContext (system prompt + history handled natively).
    /// </summary>
    public class XybridDialogueProvider : IDialogueProvider
    {
        private const int MaxHistoryLength = 20;

        private readonly XybridModelService _service;
        private WorldLore _worldLore;

        // Per-NPC native conversation contexts (system prompt persists across Clear)
        private readonly Dictionary<string, ConversationContext> _npcContexts
            = new Dictionary<string, ConversationContext>();

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
            => RunDialogueAsync(npc, GreetingTrigger(npc));

        public Task<DialogueResponse> GetResponseAsync(
            NPCIdentity npc, string playerInput, string[] conversationHistory)
            => RunDialogueAsync(npc, playerInput);

        public Task<DialogueResponse> GetGreetingStreamingAsync(NPCIdentity npc, Action<string> onToken)
            => RunDialogueAsync(npc, GreetingTrigger(npc), onToken);

        public Task<DialogueResponse> GetResponseStreamingAsync(
            NPCIdentity npc, string playerInput, string[] conversationHistory, Action<string> onToken)
            => RunDialogueAsync(npc, playerInput, onToken);

        private static string GreetingTrigger(NPCIdentity npc)
            => $"Now with the information provided, generate {npc.npcName}'s greeting to the user:";

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

            var context = GetOrCreateContext(npc);
            bool streaming = onToken != null;

            Debug.Log($"[XybridProvider] {(streaming ? "Streaming" : "Non-streaming")} for {npc.npcName} (input: {userInput.Length} chars)");

            var response = streaming
                ? await _service.RunStreamingAsync(userInput, context, onToken)
                : await _service.RunInferenceAsync(userInput, context);

            if (response.Success)
            {
                response.Text = CleanResponse(response.Text);

                // Push the exchange into native context for future turns
                context.Push(userInput, MessageRole.User);
                context.Push(response.Text, MessageRole.Assistant);

                LastLatencyMs = response.LatencyMs;
                LastInferenceLocation = response.InferenceLocation;
            }

            return response;
        }

        // ================================================================
        // ConversationContext management
        // ================================================================

        private ConversationContext GetOrCreateContext(NPCIdentity npc)
        {
            if (!_npcContexts.TryGetValue(npc.npcName, out var context))
            {
                context = new ConversationContext(npc.npcName);
                context.SetSystem(BuildSystemPrompt(npc));
                context.SetMaxHistoryLength(MaxHistoryLength);
                _npcContexts[npc.npcName] = context;

                Debug.Log($"[XybridProvider] Created ConversationContext for {npc.npcName}");
            }
            return context;
        }

        /// <summary>
        /// Build the Gemma3NPC-formatted roleplay prompt.
        /// Emitted via ConversationContext.SetSystem(); the GGUF's jinja chat template
        /// prepends it to the first user turn with "\n\n", matching the article's
        /// training-time layout (https://huggingface.co/blog/chimbiwide/gemma3npc).
        /// </summary>
        private string BuildSystemPrompt(NPCIdentity npc)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"Enter Roleplay Mode. You are roleplaying as {npc.npcName}. You must always stay in character.");
            sb.AppendLine();
            sb.AppendLine("Your goal is to create an immersive, fun, creative roleplaying experience for the user. You must respond in a way that drives the conversation forward.");
            sb.AppendLine();
            sb.AppendLine("Character Persona:");
            sb.AppendLine($"Name: {npc.npcName}");
            sb.AppendLine($"Category of your character: {(string.IsNullOrWhiteSpace(npc.category) ? "Villager" : npc.category)}");
            sb.Append("Description of your character: ");
            sb.AppendLine(BuildDescription(npc));
            sb.Append("Definition of your character (contains example chats so that you can better roleplay as the character): ");
            sb.Append(BuildDefinition(npc));

            return sb.ToString();
        }

        private string BuildDescription(NPCIdentity npc)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(npc.description))
                sb.Append(npc.description.Trim());
            if (!string.IsNullOrWhiteSpace(npc.personality))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(npc.personality.Trim());
            }
            if (_worldLore != null)
            {
                string world = _worldLore.worldBrief?.Trim();
                string setting = _worldLore.settingBrief?.Trim();
                if (!string.IsNullOrEmpty(world))
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(world);
                }
                if (!string.IsNullOrEmpty(setting))
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(setting);
                }
            }
            return sb.ToString();
        }

        private static string BuildDefinition(NPCIdentity npc)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(npc.extendedPersonality))
                sb.AppendLine(npc.extendedPersonality.Trim());
            sb.Append("Reply in 1-2 sentences as spoken dialogue only. No quotes, no narration, no actions.");
            return sb.ToString();
        }

        public void ClearNPCContext(string npcName)
        {
            if (_npcContexts.TryGetValue(npcName, out var context))
            {
                context.Clear(); // Wipes history, keeps system prompt
                Debug.Log($"[XybridProvider] Cleared context for {npcName}");
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
    }
}
