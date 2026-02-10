using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using Tavern.Dialogue;

/// <summary>
/// Manages the dialogue UI and conversation flow.
/// Supports both scripted (V1) and AI-generated (V2) dialogue.
/// Uses streaming inference for AI dialogue — text appears token-by-token.
/// </summary>
public class DialogueManagerV2 : MonoBehaviour
{
    [Header("Provider Settings")]
    [SerializeField] private bool useAIDialogue = false;
    [SerializeField] private WorldLore worldLore;

    [Header("UI References")]
    [SerializeField] private GameObject dialoguePanel;
    [SerializeField] private TMP_Text npcNameText;
    [SerializeField] private TMP_Text npcDialogueText;
    [SerializeField] private Button[] optionButtons;
    [SerializeField] private TMP_Text[] optionButtonTexts;
    [SerializeField] private Button closeButton;
    [SerializeField] private TMP_InputField playerInputField;
    [SerializeField] private Button sendButton;
    [SerializeField] private GameObject thinkingIndicator;

    [Header("Conversation History")]
    [SerializeField] private ConversationHistoryUI conversationHistoryUI;

    [Header("Demo Overlay")]
    [SerializeField] private GameObject demoOverlay;
    [SerializeField] private TMP_Text latencyText;
    [SerializeField] private TMP_Text modelText;
    [SerializeField] private TMP_Text locationText;

    [Header("Typewriter Effect")]
    [SerializeField] private float typeSpeed = 0.03f;
    [SerializeField] private AudioSource typingAudioSource;
    [SerializeField] private AudioClip[] typingSounds;
    [SerializeField] private float typingVolume = 0.3f;
    [SerializeField] private float typingPitchVariation = 0.1f;

    [Header("Panel Animation")]
    [SerializeField] private float panelFadeDuration = 0.2f;

    // Providers
    private IDialogueProvider _provider;
    private XybridDialogueProvider _xybridProvider;
    private ScriptedDialogueProvider _scriptedProvider;

    // State
    private NPCIdentity _currentNPC;
    private int _currentExchangeIndex;
    private Coroutine _typewriterCoroutine;
    private Coroutine _panelFadeCoroutine;
    private List<string> _conversationHistory = new List<string>();
    private bool _isProcessing;
    private bool _isDialogueOpen;
    private CanvasGroup _panelCanvasGroup;

    // Streaming token queue — filled by background thread, consumed by typewriter coroutine
    private readonly ConcurrentQueue<string> _tokenQueue = new ConcurrentQueue<string>();
    private volatile bool _streamingComplete;

    private async void Start()
    {
        // Get or add CanvasGroup for fade animation
        _panelCanvasGroup = dialoguePanel.GetComponent<CanvasGroup>();
        if (_panelCanvasGroup == null)
            _panelCanvasGroup = dialoguePanel.AddComponent<CanvasGroup>();
        _panelCanvasGroup.alpha = 0f;

        // Initialize providers
        _scriptedProvider = new ScriptedDialogueProvider();

        if (useAIDialogue)
        {
            try
            {
                var service = XybridModelService.Instance;
                if (service == null)
                    throw new System.InvalidOperationException(
                        "XybridModelService not found in scene. Add it to a GameObject.");

                await service.InitializeAsync();

                _xybridProvider = new XybridDialogueProvider(service);
                if (worldLore != null)
                    _xybridProvider.SetWorldLore(worldLore);

                _provider = _xybridProvider;
                Debug.Log($"[DialogueManager] Using Xybrid AI dialogue ({service.ModelId})");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[DialogueManager] Xybrid init failed: {ex.Message}");
                Debug.LogWarning("[DialogueManager] Falling back to scripted dialogue");
                _provider = _scriptedProvider;
            }
        }
        else
        {
            _provider = _scriptedProvider;
            Debug.Log("[DialogueManager] Using scripted dialogue");
        }

        // Setup UI
        dialoguePanel.SetActive(false);
        if (thinkingIndicator != null) thinkingIndicator.SetActive(false);
        if (demoOverlay != null) demoOverlay.SetActive(false);

        closeButton.onClick.AddListener(EndDialogue);

        for (int i = 0; i < optionButtons.Length; i++)
        {
            int index = i;
            optionButtons[i].onClick.AddListener(() => OnOptionSelected(index));
        }

        if (sendButton != null)
            sendButton.onClick.AddListener(OnSendClicked);

        // No onEndEdit listener — Enter key is handled in Update() to avoid
        // spurious triggers on focus loss.

        UpdateInputMode();
    }

    public void SetUseAIDialogue(bool useAI)
    {
        useAIDialogue = useAI;
        _provider = useAI ? (IDialogueProvider)_xybridProvider : _scriptedProvider;
        UpdateInputMode();
        Debug.Log($"[DialogueManager] Switched to {_provider.ProviderName}");
    }

    private void UpdateInputMode()
    {
        bool freeInput = _provider?.SupportsFreeInput ?? false;

        if (playerInputField != null)
            playerInputField.gameObject.SetActive(freeInput);
        if (sendButton != null)
            sendButton.gameObject.SetActive(freeInput);

        foreach (var btn in optionButtons)
            btn.gameObject.SetActive(!freeInput);
    }

    // ================================================================
    // Dialogue flow
    // ================================================================

    public async void StartDialogue(NPCIdentity npc)
    {
        if (_isProcessing || _isDialogueOpen) return;
        _isDialogueOpen = true;

        _currentNPC = npc;
        _currentExchangeIndex = 0;
        _conversationHistory.Clear();

        if (conversationHistoryUI != null)
            conversationHistoryUI.Clear();

        ShowPanel();
        npcNameText.text = npc.npcName;

        npc.GetComponent<NPCLookAt>()?.StartLookingAtPlayer();

        var cameraFocus = FindFirstObjectByType<DialogueCameraFocus>();
        cameraFocus?.FocusOn(npc.transform);

        SetThinking(true);

        DialogueResponse response;
        try
        {
            if (_provider.SupportsStreaming)
            {
                ClearTokenQueue();
                _streamingComplete = false;
                _typewriterCoroutine = StartCoroutine(StreamingTypewriterEffect());

                response = await _provider.GetGreetingStreamingAsync(npc, OnStreamToken);
                _streamingComplete = true;
            }
            else
            {
                response = await _provider.GetGreetingAsync(npc);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DialogueManager] Greeting failed: {ex.Message}");
            _streamingComplete = true;
            response = DialogueResponse.FromError(ex.Message);
        }

        SetThinking(false);

        if (!response.Success)
        {
            ShowNPCText("*The NPC stares blankly...*");
            return;
        }

        UpdateDemoOverlay(response);

        if (!_provider.SupportsStreaming)
            ShowNPCText(response.Text);

        _conversationHistory.Add($"{npc.npcName}: {response.Text}");

        if (conversationHistoryUI != null)
            conversationHistoryUI.AddMessage(npc.npcName, response.Text, isPlayer: false);

        await ShowPlayerInput();
    }

    private async void OnOptionSelected(int optionIndex)
    {
        if (_isProcessing) return;

        var options = await _provider.GetPlayerOptionsAsync(_currentNPC, _currentExchangeIndex);
        if (options == null || optionIndex >= options.Length) return;

        string playerChoice = options[optionIndex];
        await ProcessPlayerInput(playerChoice, optionIndex);
    }

    private void OnSendClicked()
    {
        if (_isProcessing) return;
        if (playerInputField == null) return;
        if (string.IsNullOrWhiteSpace(playerInputField.text)) return;

        string input = playerInputField.text.Trim();

        // Freeze: keep sent text visible but disable interaction
        SetInputLocked(true);

        _ = ProcessPlayerInput(input, -1);
    }

    private async System.Threading.Tasks.Task ProcessPlayerInput(string playerInput, int optionIndex)
    {
        _isProcessing = true;
        _conversationHistory.Add($"Player: {playerInput}");

        if (conversationHistoryUI != null)
            conversationHistoryUI.AddMessage("You", playerInput, isPlayer: true);

        SetThinking(true);
        HideOptions();

        DialogueResponse response;

        try
        {
            if (_provider.SupportsFreeInput)
            {
                if (_provider.SupportsStreaming)
                {
                    ClearTokenQueue();
                    _streamingComplete = false;
                    _typewriterCoroutine = StartCoroutine(StreamingTypewriterEffect());

                    response = await _provider.GetResponseStreamingAsync(
                        _currentNPC,
                        playerInput,
                        _conversationHistory.ToArray(),
                        OnStreamToken);
                    _streamingComplete = true;
                }
                else
                {
                    response = await _provider.GetResponseAsync(
                        _currentNPC,
                        playerInput,
                        _conversationHistory.ToArray());
                }
            }
            else
            {
                var exchanges = _currentNPC.dialogueData.exchanges;
                if (_currentExchangeIndex < exchanges.Length && optionIndex >= 0)
                {
                    string npcResponse = exchanges[_currentExchangeIndex].npcResponses[optionIndex];
                    response = DialogueResponse.FromText(npcResponse);
                }
                else
                {
                    response = DialogueResponse.FromText("...");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DialogueManager] Response failed: {ex.Message}");
            _streamingComplete = true;
            response = DialogueResponse.FromError(ex.Message);
        }

        SetThinking(false);

        if (!response.Success)
        {
            ShowNPCText("*The NPC seems lost in thought...*");
            _isProcessing = false;
            UnlockAndClearInput();
            await ShowPlayerInput();
            return;
        }

        UpdateDemoOverlay(response);

        if (!_provider.SupportsStreaming)
            ShowNPCText(response.Text);

        _conversationHistory.Add($"{_currentNPC.npcName}: {response.Text}");
        _currentExchangeIndex++;

        if (conversationHistoryUI != null)
            conversationHistoryUI.AddMessage(_currentNPC.npcName, response.Text, isPlayer: false);

        UnlockAndClearInput();
        await ShowPlayerInput();
        _isProcessing = false;
    }

    // ================================================================
    // Input field lock/unlock
    // ================================================================

    private void SetInputLocked(bool locked)
    {
        if (playerInputField != null)
            playerInputField.interactable = !locked;
        if (sendButton != null)
            sendButton.interactable = !locked;
    }

    private void UnlockAndClearInput()
    {
        if (playerInputField != null)
        {
            playerInputField.text = "";
            playerInputField.interactable = true;
        }
        if (sendButton != null)
            sendButton.interactable = true;
    }

    // ================================================================
    // Streaming token handling
    // ================================================================

    private void OnStreamToken(string token)
    {
        if (!string.IsNullOrEmpty(token))
            _tokenQueue.Enqueue(token);
    }

    private void ClearTokenQueue()
    {
        while (_tokenQueue.TryDequeue(out _)) { }
    }

    private IEnumerator StreamingTypewriterEffect()
    {
        npcDialogueText.text = "";
        bool firstToken = true;

        while (!_streamingComplete || _tokenQueue.Count > 0)
        {
            if (_tokenQueue.TryDequeue(out string token))
            {
                if (firstToken)
                {
                    firstToken = false;
                    SetThinking(false);
                    if (_currentNPC != null)
                    {
                        var animator = _currentNPC.GetComponentInChildren<Animator>();
                        if (animator != null)
                            animator.SetBool("isTalking", true);
                    }
                }

                foreach (char c in token)
                {
                    npcDialogueText.text += c;

                    if (typingAudioSource != null && typingSounds != null && typingSounds.Length > 0 && !char.IsWhiteSpace(c))
                    {
                        AudioClip clip = typingSounds[Random.Range(0, typingSounds.Length)];
                        typingAudioSource.pitch = 1f + Random.Range(-typingPitchVariation, typingPitchVariation);
                        typingAudioSource.PlayOneShot(clip, typingVolume);
                    }

                    yield return new WaitForSeconds(typeSpeed);
                }
            }
            else
            {
                yield return null;
            }
        }

        if (typingAudioSource != null)
            typingAudioSource.pitch = 1f;
    }

    // ================================================================
    // Non-streaming typewriter (used for scripted dialogue)
    // ================================================================

    private async System.Threading.Tasks.Task ShowPlayerInput()
    {
        if (_provider.SupportsFreeInput)
        {
            if (playerInputField != null)
            {
                playerInputField.gameObject.SetActive(true);
                playerInputField.text = "";
                playerInputField.interactable = true;
                playerInputField.ActivateInputField();
            }
            if (sendButton != null)
                sendButton.interactable = true;
            HideOptions();
        }
        else
        {
            var options = await _provider.GetPlayerOptionsAsync(_currentNPC, _currentExchangeIndex);
            if (options != null && options.Length > 0)
                ShowOptions(options);
            else
                HideOptions();
        }
    }

    private void ShowOptions(string[] options)
    {
        for (int i = 0; i < optionButtons.Length; i++)
        {
            if (i < options.Length)
            {
                optionButtons[i].gameObject.SetActive(true);
                optionButtonTexts[i].text = options[i];
            }
            else
            {
                optionButtons[i].gameObject.SetActive(false);
            }
        }
    }

    private void HideOptions()
    {
        foreach (var btn in optionButtons)
            btn.gameObject.SetActive(false);
    }

    private void ShowNPCText(string text)
    {
        if (_typewriterCoroutine != null)
            StopCoroutine(_typewriterCoroutine);

        if (_currentNPC != null)
        {
            var animator = _currentNPC.GetComponentInChildren<Animator>();
            if (animator != null)
                animator.SetBool("isTalking", true);
        }

        _typewriterCoroutine = StartCoroutine(TypewriterEffect(text));
    }

    private IEnumerator TypewriterEffect(string text)
    {
        npcDialogueText.text = "";
        foreach (char c in text)
        {
            npcDialogueText.text += c;

            if (typingAudioSource != null && typingSounds != null && typingSounds.Length > 0 && !char.IsWhiteSpace(c))
            {
                AudioClip clip = typingSounds[Random.Range(0, typingSounds.Length)];
                typingAudioSource.pitch = 1f + Random.Range(-typingPitchVariation, typingPitchVariation);
                typingAudioSource.PlayOneShot(clip, typingVolume);
            }

            yield return new WaitForSeconds(typeSpeed);
        }

        if (typingAudioSource != null)
            typingAudioSource.pitch = 1f;
    }

    // ================================================================
    // UI state helpers
    // ================================================================

    private void SetThinking(bool isThinking)
    {
        if (thinkingIndicator != null)
            thinkingIndicator.SetActive(isThinking);

        if (isThinking)
            npcDialogueText.text = "";

        if (_currentNPC != null)
        {
            var animator = _currentNPC.GetComponentInChildren<Animator>();
            if (animator != null)
            {
                animator.SetBool("isThinking", isThinking);
                if (isThinking)
                    animator.SetBool("isTalking", false);
            }
        }
    }

    private void UpdateDemoOverlay(DialogueResponse response)
    {
        if (demoOverlay == null) return;

        if (latencyText != null)
            latencyText.text = $"Latency: {response.LatencyMs}ms";
        if (modelText != null)
            modelText.text = $"Model: {response.ModelId}";
        if (locationText != null)
            locationText.text = $"Location: {response.InferenceLocation}";
    }

    public void ToggleDemoOverlay()
    {
        if (demoOverlay != null)
            demoOverlay.SetActive(!demoOverlay.activeSelf);
    }

    public void EndDialogue()
    {
        if (_typewriterCoroutine != null)
            StopCoroutine(_typewriterCoroutine);

        _currentNPC?.GetComponentInChildren<Animator>()?.SetBool("isTalking", false);
        _currentNPC?.GetComponent<NPCLookAt>()?.StopLookingAtPlayer(returnToOriginal: true);

        var cameraFocus = FindFirstObjectByType<DialogueCameraFocus>();
        cameraFocus?.Unfocus();

        if (_currentNPC != null && _xybridProvider != null)
            _xybridProvider.ClearNPCContext(_currentNPC.npcName);

        HidePanel();
        _isDialogueOpen = false;
        _currentNPC = null;
        _conversationHistory.Clear();
        ClearTokenQueue();
        UnlockAndClearInput();

        if (conversationHistoryUI != null)
            conversationHistoryUI.Clear();

        var player = FindFirstObjectByType<PlayerInteraction>();
        if (player != null)
            player.EndDialogue();
    }

    private void ShowPanel()
    {
        dialoguePanel.SetActive(true);
        if (_panelFadeCoroutine != null)
            StopCoroutine(_panelFadeCoroutine);
        _panelFadeCoroutine = StartCoroutine(FadePanel(1f));
    }

    private void HidePanel()
    {
        if (_panelFadeCoroutine != null)
            StopCoroutine(_panelFadeCoroutine);
        _panelFadeCoroutine = StartCoroutine(FadePanel(0f, deactivateOnComplete: true));
    }

    private IEnumerator FadePanel(float targetAlpha, bool deactivateOnComplete = false)
    {
        float startAlpha = _panelCanvasGroup.alpha;
        float elapsed = 0f;

        while (elapsed < panelFadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / panelFadeDuration;
            _panelCanvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            yield return null;
        }

        _panelCanvasGroup.alpha = targetAlpha;

        if (deactivateOnComplete && targetAlpha == 0f)
            dialoguePanel.SetActive(false);
    }

    private void OnDestroy()
    {
        // Model lifecycle is owned by XybridModelService — nothing to dispose here.
    }

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
            ToggleDemoOverlay();

        if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
            SetUseAIDialogue(!useAIDialogue);

        // Submit on Enter while input field is focused (replaces onEndEdit to avoid
        // spurious inference triggers on focus loss)
        if (!_isProcessing && playerInputField != null && playerInputField.isFocused &&
            Keyboard.current != null &&
            (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame))
        {
            OnSendClicked();
        }
    }
}
