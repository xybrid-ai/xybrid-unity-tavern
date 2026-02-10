using UnityEngine;

/// <summary>
/// Shared world lore that all NPCs reference.
/// Create via: Right-click → Create → Tavern → World Lore
/// </summary>
[CreateAssetMenu(fileName = "WorldLore", menuName = "Tavern/World Lore")]
public class WorldLore : ScriptableObject
{
    [Header("The World")]
    [TextArea(4, 8)]
    public string worldOverview;

    [Header("The Region")]
    [TextArea(4, 8)]
    public string regionDescription;

    [Header("Recent Events (Common Knowledge)")]
    [TextArea(4, 8)]
    public string recentEvents;

    [Header("Local Area")]
    [TextArea(4, 8)]
    public string localArea;

    [Header("The Tavern")]
    [TextArea(4, 8)]
    public string tavernHistory;

    [Header("Rumors & Mysteries")]
    [TextArea(4, 8)]
    public string rumorsAndMysteries;

    [Header("What NPCs Do NOT Know")]
    [TextArea(3, 6)]
    [Tooltip("Explicit boundaries - modern tech, other worlds, meta knowledge, etc.")]
    public string knowledgeBoundaries;

    [Header("Condensed Context (used in AI prompts)")]
    [TextArea(2, 4)]
    [Tooltip("1-2 sentences. Essential world context for AI prompts. Keep under 150 chars.")]
    public string worldBrief;

    [TextArea(2, 4)]
    [Tooltip("1 sentence. Immediate setting — tavern, time of day, atmosphere. Keep under 80 chars.")]
    public string settingBrief;
}
