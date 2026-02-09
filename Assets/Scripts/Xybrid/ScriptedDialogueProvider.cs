using System;
using System.Threading.Tasks;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Dialogue provider that uses pre-written DialogueData ScriptableObjects.
    /// This is the V1 implementation.
    /// </summary>
    public class ScriptedDialogueProvider : IDialogueProvider
    {
        public string ProviderName => "Scripted";
        public bool SupportsFreeInput => false;
        public bool SupportsStreaming => false;

        public Task<DialogueResponse> GetGreetingAsync(NPCIdentity npc)
        {
            if (npc.dialogueData == null)
            {
                return Task.FromResult(DialogueResponse.FromError("No dialogue data assigned"));
            }

            return Task.FromResult(DialogueResponse.FromText(npc.dialogueData.greeting));
        }

        public Task<DialogueResponse> GetResponseAsync(
            NPCIdentity npc,
            string playerInput,
            string[] conversationHistory)
        {
            // For scripted dialogue, playerInput is matched to an option index
            // The DialogueManager handles this mapping
            // This method is called with the NPC's response text directly
            return Task.FromResult(DialogueResponse.FromText(playerInput));
        }

        public Task<DialogueResponse> GetGreetingStreamingAsync(NPCIdentity npc, Action<string> onToken)
        {
            // No streaming for scripted — return full text
            return GetGreetingAsync(npc);
        }

        public Task<DialogueResponse> GetResponseStreamingAsync(
            NPCIdentity npc,
            string playerInput,
            string[] conversationHistory,
            Action<string> onToken)
        {
            // No streaming for scripted — return full text
            return GetResponseAsync(npc, playerInput, conversationHistory);
        }

        public Task<string[]> GetPlayerOptionsAsync(NPCIdentity npc, int exchangeIndex)
        {
            if (npc.dialogueData == null)
            {
                return Task.FromResult<string[]>(null);
            }

            if (exchangeIndex >= npc.dialogueData.exchanges.Length)
            {
                return Task.FromResult<string[]>(null);
            }

            return Task.FromResult(npc.dialogueData.exchanges[exchangeIndex].playerOptions);
        }
    }
}
