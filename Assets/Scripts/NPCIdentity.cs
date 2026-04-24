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

    [Tooltip("Short role/archetype. Fills the Gemma3NPC 'Category' slot (e.g., 'Barkeeper', 'Martial Arts Master').")]
    public string category = "Villager";

    [Header("Dialogue Data (V1 - Scripted)")]
    public DialogueData dialogueData;

    [Header("Voice (TTS)")]
    [Tooltip("Voice ID from the TTS model's voice catalog (e.g., 'af_heart'). Leave empty for model default.")]
    public string voiceId;

    // AI persona is authored in Assets/Resources/NPCPrompts/{npcName}.md and loaded at runtime.
}