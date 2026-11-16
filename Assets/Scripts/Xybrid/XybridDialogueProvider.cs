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

        // Bumped by ClearNPCContext. A run that started before a clear must not push its turns into the
        // cleared history (the dialogue was closed while it ran; DialogueManagerV2 already discards the reply).
        private readonly Dictionary<string, int> _contextEpochs = new Dictionary<string, int>();

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
            => RunDialogueAsync(npc, GreetingTrigger(npc), recordUserTurn: false);

        public Task<DialogueResponse> GetResponseAsync(
            NPCIdentity npc, string playerInput, string[] conversationHistory)
            => RunDialogueAsync(npc, playerInput);

        public Task<DialogueResponse> GetGreetingStreamingAsync(NPCIdentity npc, Action<string> onToken)
            => RunDialogueAsync(npc, GreetingTrigger(npc), onToken, recordUserTurn: false);

        public Task<DialogueResponse> GetResponseStreamingAsync(
            NPCIdentity npc, string playerInput, string[] conversationHistory, Action<string> onToken)
            => RunDialogueAsync(npc, playerInput, onToken);

        /// <summary>
        /// Scaffolding that prompts the model to open the conversation. This is an instruction
        /// to the model, not something the player said, so it is never written to history —
        /// see the recordUserTurn argument on <see cref="RunDialogueAsync"/>.
        /// </summary>
        private static string GreetingTrigger(NPCIdentity npc)
            => $"Now with the information provided, generate {npc.npcName}'s greeting to the user:";

        /// <summary>What history records as the player's opening turn when the NPC greets first.</summary>
        private const string GreetingStandInTurn = "Hello.";

        public Task<string[]> GetPlayerOptionsAsync(NPCIdentity npc, int exchangeIndex)
            => Task.FromResult<string[]>(null);

        // ================================================================
        // Shared dialogue core
        // ================================================================

        /// <param name="recordUserTurn">
        /// False when <paramref name="userInput"/> is internal scaffolding rather than player
        /// speech. The model's reply is still recorded, so the NPC remembers what it said.
        /// </param>
        private async Task<DialogueResponse> RunDialogueAsync(
            NPCIdentity npc, string userInput, Action<string> onToken = null, bool recordUserTurn = true)
        {
            if (!_service.IsReady)
                return DialogueResponse.FromError("XybridModelService not ready");

            var context = GetOrCreateContext(npc);
            int epoch = ContextEpoch(npc.npcName);
            bool streaming = onToken != null;

            Debug.Log($"[XybridProvider] {(streaming ? "Streaming" : "Non-streaming")} for {npc.npcName} (input: {userInput.Length} chars)");

            var response = streaming
                ? await _service.RunStreamingAsync(userInput, context, onToken)
                : await _service.RunInferenceAsync(userInput, context);

            if (response.Success && ContextEpoch(npc.npcName) != epoch)
            {
                Debug.Log($"[XybridProvider] {npc.npcName}'s context was cleared while this reply ran; not recording it.");
                response.Text = CleanResponse(response.Text);
                return response;
            }

            if (response.Success)
            {
                response.Text = CleanResponse(response.Text);

                // Push the exchange into native context for future turns. A greeting has no
                // player turn to record - pushing the trigger here would make the model believe
                // the player opened by instructing it to greet them. Gemma chat templates still
                // require user/assistant turns to alternate (the next render fails otherwise),
                // so record the player simply saying hello in its place.
                context.Push(recordUserTurn ? userInput : GreetingStandInTurn, MessageRole.User);
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
        /// The Definition slot is loaded from Assets/Resources/NPCPrompts/{npcName}.md.
        /// </summary>
        private string BuildSystemPrompt(NPCIdentity npc)
        {
            string category = string.IsNullOrWhiteSpace(npc.category) ? "Villager" : npc.category;
            string description = BuildDescription(category);
            string definition = LoadPersona(npc.npcName);

            var sb = new StringBuilder();
            sb.AppendLine($"Enter Roleplay Mode. You are roleplaying as {npc.npcName}. You must always stay in character.");
            sb.AppendLine();
            sb.AppendLine($"Your goal is to create an immersive, fun, creative roleplaying experience for the user. You must respond in a way that drives the conversation forward. Output ONLY what {npc.npcName} says aloud, in first person, as one or two sentences of direct spoken dialogue. Never narrate the scene or {npc.npcName}'s actions. Never refer to {npc.npcName} in the third person. Never wrap any part of your reply in asterisks, brackets, or quotes.");
            sb.AppendLine();
            sb.AppendLine("Character Persona:");
            sb.AppendLine($"Name: {npc.npcName}");
            sb.AppendLine($"Category of your character: {category}");
            sb.AppendLine($"Description of your character: {description}");
            sb.Append("Definition of your character (contains example chats so that you can better roleplay as the character): ");
            sb.Append(definition);
            return sb.ToString();
        }

        private string BuildDescription(string category)
        {
            string world = _worldLore?.worldBrief?.Trim();
            string setting = _worldLore?.settingBrief?.Trim();

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(world)) parts.Add(world);
            if (!string.IsNullOrEmpty(setting)) parts.Add(setting);
            parts.Add($"A {category}.");
            return string.Join(" ", parts);
        }

        private string LoadPersona(string npcName)
        {
            var asset = Resources.Load<TextAsset>($"NPCPrompts/{npcName}");
            if (asset == null || string.IsNullOrWhiteSpace(asset.text))
            {
                Debug.LogWarning($"[XybridProvider] No persona found at Resources/NPCPrompts/{npcName}.md — using category-only fallback.");
                return "(No additional details provided.)";
            }
            return asset.text.Trim();
        }

        private int ContextEpoch(string npcName)
            => _contextEpochs.TryGetValue(npcName, out int epoch) ? epoch : 0;

        public void ClearNPCContext(string npcName)
        {
            _contextEpochs[npcName] = ContextEpoch(npcName) + 1;
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

            // Strip RP-style action markers: *shuffles behind the bar*
            response = System.Text.RegularExpressions.Regex
                .Replace(response, @"\*[^*]*\*", string.Empty)
                .Trim();

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
