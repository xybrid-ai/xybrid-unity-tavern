using UnityEngine;

/// <summary>
/// ScriptableObject holding scripted dialogue for an NPC.
/// Create via: Right-click in Project → Create → Dialogue → NPC Dialogue
/// </summary>
[CreateAssetMenu(fileName = "NewDialogue", menuName = "Dialogue/NPC Dialogue")]
public class DialogueData : ScriptableObject
{
    public string greeting;

    public DialogueExchange[] exchanges;
}

[System.Serializable]
public class DialogueExchange
{
    [TextArea(2, 4)]
    public string[] playerOptions;

    [TextArea(2, 4)]
    public string[] npcResponses; // Same length as playerOptions, index-matched
}
