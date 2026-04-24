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

        var personaAsset = Resources.Load<TextAsset>($"NPCPrompts/{npc.npcName}");
        string persona = personaAsset != null
            ? personaAsset.text
            : $"(no prompt file at Resources/NPCPrompts/{npc.npcName}.md)";

        debugText.text =
            $"<b>Name:</b> {npc.npcName}\n\n" +
            $"<b>Category:</b> {npc.category}\n\n" +
            $"<b>Persona:</b>\n{persona}";
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
