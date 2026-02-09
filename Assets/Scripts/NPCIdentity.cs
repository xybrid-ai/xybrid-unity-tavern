using UnityEngine;

/// <summary>
/// Identifies an NPC and holds their personality data.
/// Attach to each NPC GameObject (the parent, above the model).
/// The GameObject must have a Collider and be on the "NPC" layer.
/// </summary>
public class NPCIdentity : MonoBehaviour
{
    [Header("NPC Info")]
    public string npcName = "Unknown";

    [TextArea(3, 6)]
    public string description = "A mysterious stranger.";

    [TextArea(3, 6)]
    public string personality = "Neutral and quiet.";

    [Header("Dialogue Data (V1 - Scripted)")]
    public DialogueData dialogueData;

    [Header("AI Dialogue - Extended Personality")]
    [TextArea(6, 12)]
    [Tooltip("Detailed character info for AI: speech style, knowledge, relationships, secrets.")]
    public string extendedPersonality;
}