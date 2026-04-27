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

    [Tooltip("Speech speed multiplier (1.0 = normal, 0.5 = half, 2.0 = double).")]
    [Range(0.5f, 2.0f)]
    public float voiceSpeed = 1.0f;

    // AI persona is authored in Assets/Resources/NPCPrompts/{npcName}.md and loaded at runtime.
}