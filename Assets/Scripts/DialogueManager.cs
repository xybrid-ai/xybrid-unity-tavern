using UnityEngine;
using UnityEngine.UI;
using System.Collections;

/// <summary>
/// Manages the dialogue UI and conversation flow.
/// Attach to an empty GameObject in the scene. 
/// Wire up the UI references in the Inspector.
/// </summary>
public class DialogueManager : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject dialoguePanel;
    [SerializeField] private Text npcNameText;
    [SerializeField] private Text npcDialogueText;
    [SerializeField] private Button[] optionButtons;    // 3 buttons
    [SerializeField] private Text[] optionButtonTexts;  // Text on each button
    [SerializeField] private Button closeButton;

    [Header("Typewriter Effect")]
    [SerializeField] private float typeSpeed = 0.03f;

    private NPCIdentity currentNPC;
    private int currentExchangeIndex;
    private Coroutine typewriterCoroutine;

    private void Start()
    {
        dialoguePanel.SetActive(false);
        closeButton.onClick.AddListener(EndDialogue);

        // Wire up option button clicks
        for (int i = 0; i < optionButtons.Length; i++)
        {
            int index = i; // Capture for closure
            optionButtons[i].onClick.AddListener(() => OnOptionSelected(index));
        }
    }

    /// <summary>
    /// Called by PlayerInteraction when player interacts with an NPC.
    /// </summary>
    public void StartDialogue(NPCIdentity npc)
    {
        currentNPC = npc;
        currentExchangeIndex = 0;

        dialoguePanel.SetActive(true);
        npc.GetComponentInChildren<Animator>()?.SetBool("isTalking", true);
        npcNameText.text = npc.npcName;

        // Show greeting
        ShowNPCText(npc.dialogueData.greeting);

        // Show first set of options (or close if no exchanges)
        if (npc.dialogueData.exchanges.Length > 0)
            ShowOptions(npc.dialogueData.exchanges[0]);
        else
            HideOptions();
    }

    private void OnOptionSelected(int optionIndex)
    {
        DialogueExchange exchange = currentNPC.dialogueData.exchanges[currentExchangeIndex];

        // Show NPC's response to the selected option
        if (optionIndex < exchange.npcResponses.Length)
            ShowNPCText(exchange.npcResponses[optionIndex]);

        // Advance to next exchange
        currentExchangeIndex++;

        if (currentExchangeIndex < currentNPC.dialogueData.exchanges.Length)
            ShowOptions(currentNPC.dialogueData.exchanges[currentExchangeIndex]);
        else
            HideOptions(); // No more exchanges, just show close button
    }

    private void ShowNPCText(string text)
    {
        if (typewriterCoroutine != null)
            StopCoroutine(typewriterCoroutine);

        typewriterCoroutine = StartCoroutine(TypewriterEffect(text));
    }

    private IEnumerator TypewriterEffect(string text)
    {
        npcDialogueText.text = "";
        foreach (char c in text)
        {
            npcDialogueText.text += c;
            yield return new WaitForSeconds(typeSpeed);
        }
    }

    private void ShowOptions(DialogueExchange exchange)
    {
        for (int i = 0; i < optionButtons.Length; i++)
        {
            if (i < exchange.playerOptions.Length)
            {
                optionButtons[i].gameObject.SetActive(true);
                optionButtonTexts[i].text = exchange.playerOptions[i];
            }
            else
            {
                optionButtons[i].gameObject.SetActive(false);
            }
        }
    }

    private void HideOptions()
    {
        foreach (Button btn in optionButtons)
            btn.gameObject.SetActive(false);
    }

    public void EndDialogue()
    {
        if (typewriterCoroutine != null)
            StopCoroutine(typewriterCoroutine);

        dialoguePanel.SetActive(false);
        currentNPC?.GetComponentInChildren<Animator>()?.SetBool("isTalking", false);
        currentNPC = null;

        // Unfreeze player
        PlayerInteraction player = FindFirstObjectByType<PlayerInteraction>();
        if (player != null)
            player.EndDialogue();
    }
}
