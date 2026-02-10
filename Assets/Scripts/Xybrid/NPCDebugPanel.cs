using UnityEngine;
using TMPro;

/// <summary>
/// Debug overlay that shows the current NPC's identity data from NPCIdentity.
/// Toggle with F2 during dialogue.
/// </summary>
public class NPCDebugPanel : MonoBehaviour
{
    [SerializeField] private TMP_Text debugText;

    private GameObject _panel;

    private void Awake()
    {
        _panel = debugText != null ? debugText.transform.parent.gameObject : gameObject;
        _panel.SetActive(false);
    }

    public void Show(NPCIdentity npc)
    {
        if (debugText == null || npc == null) return;

        debugText.text =
            $"<b>Name:</b> {npc.npcName}\n\n" +
            $"<b>Description:</b>\n{npc.description}\n\n" +
            $"<b>Personality:</b>\n{npc.personality}\n\n" +
            $"<b>Extended Personality:</b>\n{(string.IsNullOrEmpty(npc.extendedPersonality) ? "(empty)" : npc.extendedPersonality)}";
    }

    public void Toggle()
    {
        if (_panel != null)
            _panel.SetActive(!_panel.activeSelf);
    }

    public void Hide()
    {
        if (_panel != null)
            _panel.SetActive(false);
    }
}
