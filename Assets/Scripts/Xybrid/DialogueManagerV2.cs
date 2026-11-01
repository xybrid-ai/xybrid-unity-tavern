using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
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
    // ================================================================
    // Dialogue events (consumed by ambient NPC behaviours: wanderers,
    // table conversations, awareness)
    // ================================================================

    /// <summary>Raised once the NPC has been set up to face the player at dialogue start.</summary>
    public static event System.Action<NPCIdentity> DialogueStarted;

    /// <summary>Raised when dialogue closes, before the current NPC reference is released.</summary>
    public static event System.Action<NPCIdentity> DialogueEnded;

    /// <summary>The NPC currently in dialogue with the player, or null when no dialogue is open.</summary>
    public static NPCIdentity ActiveNPC { get; private set; }

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

    [Header("TTS Audio")]
    [SerializeField] private TTSAudioPlayer _ttsPlayer;

    [Header("Debug")]
    [SerializeField] private NPCDebugPanel npcDebugPanel;

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
    private int _dialogueSession;   // Bumped on start/end so late async continuations can tell they're stale

    /// <summary>False while the provider is still loading or another dialogue is open/processing.</summary>
    public bool CanStartDialogue => _provider != null && !_isProcessing && !_isDialogueOpen;
    private CanvasGroup _panelCanvasGroup;

    // Streaming token queue — filled by background thread, consumed by typewriter coroutine
    private readonly ConcurrentQueue<string> _tokenQueue = new ConcurrentQueue<string>();
    private volatile bool _streamingComplete;
    private volatile bool _insideAsteriskBlock;
    private volatile bool _emittedNonWhitespace;

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
        if (!CanStartDialogue) return;
        _isDialogueOpen = true;
        int session = ++_dialogueSession;

        _currentNPC = npc;
        _currentExchangeIndex = 0;
        _conversationHistory.Clear();

        if (conversationHistoryUI != null)
            conversationHistoryUI.Clear();

        ShowPanel();
        npcNameText.text = npc.npcName;

        if (npcDebugPanel != null)
            npcDebugPanel.Show(npc);

        // TryGetComponent: in the Editor GetComponent returns a "fake null" that `?.` doesn't catch,
        // which would throw for NPCs without NPCLookAt and skip DialogueStarted below
        if (npc.TryGetComponent(out NPCLookAt npcLookAt))
            npcLookAt.StartLookingAtPlayer();

        // Let ambient behaviours (wanderers, table chatter, awareness) react
        ActiveNPC = npc;
        RaiseDialogueEvent(DialogueStarted, npc);

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

                response = await _provider.GetGreetingStreamingAsync(npc, token =>
                {
                    if (session == _dialogueSession) OnStreamToken(token);
                });
                if (session != _dialogueSession) return;
                _streamingComplete = true;
            }
            else
            {
                response = await _provider.GetGreetingAsync(npc);
            }
        }
        catch (System.Exception ex)
        {
            if (session != _dialogueSession) return;
            Debug.LogError($"[DialogueManager] Greeting failed: {ex.Message}");
            _streamingComplete = true;
            response = DialogueResponse.FromError(ex.Message);
        }

        // Dialogue was closed (and maybe another opened) while the greeting was generating
        if (session != _dialogueSession) return;

        SetThinking(false);

        if (!response.Success)
        {
            ShowNPCText("*The NPC stares blankly...*");
            return;
        }

        UpdateDemoOverlay(response);

        if (_provider.SupportsStreaming)
            ReconcileStreamedText(response.Text);
        else
            ShowNPCText(response.Text);

        _conversationHistory.Add($"{npc.npcName}: {response.Text}");

        if (conversationHistoryUI != null)
            conversationHistoryUI.AddMessage(npc.npcName, response.Text, isPlayer: false);

        ShowLineEmotion(npc, response.Text);

        // Fire-and-forget TTS (non-blocking, non-fatal)
        _ = PlayTTSAsync(npc, response.Text, npc.voiceId, npc.voiceSpeed);

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
        int session = _dialogueSession;
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
                        token =>
                        {
                            if (session == _dialogueSession) OnStreamToken(token);
                        });
                    if (session != _dialogueSession) return;
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
            if (session != _dialogueSession) return;
            Debug.LogError($"[DialogueManager] Response failed: {ex.Message}");
            _streamingComplete = true;
            response = DialogueResponse.FromError(ex.Message);
        }

        // Dialogue was closed mid-inference: EndDialogue already reset state, leave it alone
        if (session != _dialogueSession) return;

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

        if (_provider.SupportsStreaming)
            ReconcileStreamedText(response.Text);
        else
            ShowNPCText(response.Text);

        _conversationHistory.Add($"{_currentNPC.npcName}: {response.Text}");
        _currentExchangeIndex++;

        if (conversationHistoryUI != null)
            conversationHistoryUI.AddMessage(_currentNPC.npcName, response.Text, isPlayer: false);

        ShowLineEmotion(_currentNPC, response.Text);

        // Fire-and-forget TTS (non-blocking, non-fatal)
        _ = PlayTTSAsync(_currentNPC, response.Text, _currentNPC.voiceId, _currentNPC.voiceSpeed);

        UnlockAndClearInput();
        await ShowPlayerInput();
        if (session == _dialogueSession)
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
    // TTS playback
    // ================================================================

    /// <summary>
    /// Synthesizes and plays <paramref name="text"/> in <paramref name="npc"/>'s voice, positioned at their head.
    /// Their NPCFace (if any) lip-syncs from the clip's spectrum right away, then from Rhubarb visemes once
    /// analysed (Phonon reports no timings of its own).
    /// </summary>
    private async Task PlayTTSAsync(NPCIdentity npc, string text, string voiceId, float speed = 1.0f)
    {
        if (_ttsPlayer == null) return;

        var service = XybridModelService.Instance;
        if (service == null || !service.HasTTSModel) return;

        int session = _dialogueSession;
        try
        {
            byte[] pcm = await service.RunTTSAsync(text, voiceId, speed);
            // Dialogue closed (and maybe another opened) while synthesizing: stay silent
            if (session != _dialogueSession || _ttsPlayer == null) return;
            if (pcm == null || pcm.Length == 0) return;

            _ttsPlayer.SetSpeaker(npc != null ? npc.transform : null);
            AudioClip clip = _ttsPlayer.PlayPCM(pcm);
            if (clip == null || npc == null) return;

            // Explicit null checks rather than ?. : a destroyed face must not be called
            NPCFace face = npc.GetComponentInChildren<NPCFace>();
            if (face == null) return;
            face.Speak(_ttsPlayer.Source, clip, text);

            // Instant in-engine visemes from the clip's spectrum (works everywhere); Rhubarb, guided by
            // the transcript, refines them a moment later
            LipSyncCue[] spectralCues = SpectralLipSync.Analyze(clip);
            if (spectralCues != null)
                face.SetLipSyncCues(clip, spectralCues);

            if (!RhubarbLipSync.IsAvailable) return;
            LipSyncCue[] cues = await RhubarbLipSync.AnalyzeAsync(clip, text);
            // Stale session, failed analysis, or the clip was replaced/destroyed by a newer line
            if (session != _dialogueSession || cues == null || face == null || clip == null) return;
            face.SetLipSyncCues(clip, cues);
            Debug.Log($"[DialogueManager] Lip sync for {face.name} from Rhubarb ({cues.Length} cues)");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[DialogueManager] TTS failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Shows the emotion a line reads with on the NPC's face, held for roughly as long as the line lasts.
    /// Runs whether or not TTS is available.
    /// </summary>
    private static void ShowLineEmotion(NPCIdentity npc, string text)
    {
        if (npc == null || string.IsNullOrEmpty(text)) return;
        if (!LineEmotion.TryAnalyze(text, out FaceEmotion emotion, out float intensity)) return;

        NPCFace face = npc.GetComponentInChildren<NPCFace>();
        if (face == null) return;

        float holdSeconds = Mathf.Min(2.5f + 0.04f * text.Length, 8f);
        face.ShowEmotion(emotion, intensity, holdSeconds);
    }

    // ================================================================
    // Streaming token handling
    // ================================================================

    private void OnStreamToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return;

        string filtered = FilterAsteriskNarration(token);
        if (!string.IsNullOrEmpty(filtered))
            _tokenQueue.Enqueue(filtered);
    }

    /// <summary>
    /// Strips RP-style asterisk-wrapped narration (*action text*) from the token
    /// stream so it never reaches the dialog panel. State persists across tokens
    /// because a `*` can land at any byte boundary. Reset via
    /// <see cref="ClearTokenQueue"/> before each new stream.
    /// </summary>
    private string FilterAsteriskNarration(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (char c in token)
        {
            if (c == '*')
            {
                _insideAsteriskBlock = !_insideAsteriskBlock;
                continue;
            }
            if (_insideAsteriskBlock) continue;

            // Suppress leading whitespace so orphaned \n\n between stripped
            // asterisk blocks doesn't leak as a blank pause before real dialogue.
            if (!_emittedNonWhitespace)
            {
                if (char.IsWhiteSpace(c)) continue;
                _emittedNonWhitespace = true;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private void ClearTokenQueue()
    {
        while (_tokenQueue.TryDequeue(out _)) { }
        _insideAsteriskBlock = false;
        _emittedNonWhitespace = false;
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

    /// <summary>
    /// Replaces the streamed typewriter output with the cleaned final text.
    /// The raw stream may include narration (asterisk actions, scene-setting)
    /// that CleanResponse stripped; this keeps the dialog panel in sync with
    /// the cleaned text that history and TTS use.
    /// </summary>
    private void ReconcileStreamedText(string cleanedText)
    {
        if (npcDialogueText == null) return;
        if (npcDialogueText.text == cleanedText) return;

        if (_typewriterCoroutine != null)
            StopCoroutine(_typewriterCoroutine);

        npcDialogueText.text = cleanedText;
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

        if (_ttsPlayer != null)
        {
            _ttsPlayer.Stop();
            _ttsPlayer.SetSpeaker(null);
        }

        var npcAnimator = _currentNPC != null ? _currentNPC.GetComponentInChildren<Animator>() : null;
        if (npcAnimator != null)
        {
            npcAnimator.SetBool("isTalking", false);
            npcAnimator.SetBool("isThinking", false);
        }

        // Close the mouth now instead of waiting for the face to notice the clip is gone
        var npcFace = _currentNPC != null ? _currentNPC.GetComponentInChildren<NPCFace>() : null;
        if (npcFace != null)
            npcFace.StopSpeaking();

        // TryGetComponent (not GetComponent?.) so a missing NPCLookAt can't abort EndDialogue
        // before ActiveNPC is cleared and DialogueEnded is raised
        if (_currentNPC != null && _currentNPC.TryGetComponent(out NPCLookAt npcLookAt))
            npcLookAt.StopLookingAtPlayer(returnToOriginal: true);

        var cameraFocus = FindFirstObjectByType<DialogueCameraFocus>();
        cameraFocus?.Unfocus();

        if (_currentNPC != null && _xybridProvider != null)
            _xybridProvider.ClearNPCContext(_currentNPC.npcName);

        if (npcDebugPanel != null)
            npcDebugPanel.Hide();

        HidePanel();
        _isDialogueOpen = false;
        // Invalidate in-flight greeting/response continuations so they can't touch the next dialogue
        _dialogueSession++;
        _isProcessing = false;

        // Notify ambient behaviours before the NPC reference is released
        NPCIdentity endedNPC = _currentNPC;
        ActiveNPC = null;
        if (endedNPC != null)
            RaiseDialogueEvent(DialogueEnded, endedNPC);

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

    /// <summary>
    /// Invokes each subscriber in isolation so one faulty listener can't block
    /// the others or abort the dialogue flow.
    /// </summary>
    private static void RaiseDialogueEvent(System.Action<NPCIdentity> handlers, NPCIdentity npc)
    {
        if (handlers == null) return;

        foreach (System.Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((System.Action<NPCIdentity>)handler)(npc);
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }

    /// <summary>
    /// Clears static dialogue state on play-mode entry (needed when domain reload is disabled).
    /// Runs before any Awake/OnEnable, so subscribers re-register afterwards.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        ActiveNPC = null;
        DialogueStarted = null;
        DialogueEnded = null;
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

        if (Keyboard.current != null && Keyboard.current.backquoteKey.wasPressedThisFrame && npcDebugPanel != null)
            npcDebugPanel.Toggle();

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
