using System;
using System.Threading.Tasks;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Result of a dialogue generation request.
    /// </summary>
    public class DialogueResponse
    {
        public string Text { get; set; }
        public bool Success { get; set; }
        public uint LatencyMs { get; set; }
        public string ModelId { get; set; }
        public string InferenceLocation { get; set; } // "device", "edge", "cloud"
        public string Error { get; set; }

        public static DialogueResponse FromText(string text)
        {
            return new DialogueResponse
            {
                Text = text,
                Success = true,
                LatencyMs = 0,
                ModelId = "scripted",
                InferenceLocation = "local"
            };
        }

        public static DialogueResponse FromError(string error)
        {
            return new DialogueResponse
            {
                Text = "...",
                Success = false,
                Error = error
            };
        }
    }

    /// <summary>
    /// Interface for dialogue generation providers.
    /// Allows swapping between scripted (V1) and AI-generated (V2) dialogue.
    /// </summary>
    public interface IDialogueProvider
    {
        /// <summary>
        /// Generate a greeting when the player first talks to an NPC.
        /// </summary>
        Task<DialogueResponse> GetGreetingAsync(NPCIdentity npc);

        /// <summary>
        /// Generate a response to a player's dialogue choice.
        /// </summary>
        Task<DialogueResponse> GetResponseAsync(
            NPCIdentity npc,
            string playerInput,
            string[] conversationHistory);

        /// <summary>
        /// Generate a greeting with streaming token callback.
        /// onToken receives each token string as it's generated.
        /// </summary>
        Task<DialogueResponse> GetGreetingStreamingAsync(NPCIdentity npc, Action<string> onToken);

        /// <summary>
        /// Generate a response with streaming token callback.
        /// onToken receives each token string as it's generated.
        /// </summary>
        Task<DialogueResponse> GetResponseStreamingAsync(
            NPCIdentity npc,
            string playerInput,
            string[] conversationHistory,
            Action<string> onToken);

        /// <summary>
        /// Get dialogue options for the player to choose from.
        /// For scripted: returns predefined options.
        /// For AI: may return null (free-form input) or generated suggestions.
        /// </summary>
        Task<string[]> GetPlayerOptionsAsync(NPCIdentity npc, int exchangeIndex);

        /// <summary>
        /// Whether this provider supports free-form player input.
        /// </summary>
        bool SupportsFreeInput { get; }

        /// <summary>
        /// Whether this provider supports streaming token output.
        /// </summary>
        bool SupportsStreaming { get; }

        /// <summary>
        /// Provider name for debugging/display.
        /// </summary>
        string ProviderName { get; }
    }
}
