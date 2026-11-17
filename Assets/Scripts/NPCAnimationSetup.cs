using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Forces the correct animation state on Start.
/// Attach to the NPC root next to the Animator (an Animator on a child is used as a fallback).
/// Each NPC starts its looping state at a random point and plays at a slightly different speed,
/// so a room full of NPCs never breathes and sways in lockstep.
/// </summary>
public class NPCAnimationSetup : MonoBehaviour
{
    public enum Posture
    {
        Standing = 0,
        Sitting = 1,
        Bartending = 2,
        SittingFloor = 3
    }

    [Header("Default State")]
    public Posture posture = Posture.Standing;
    public bool startTalking = false;
    public bool startThinking = false;

    [Header("Variation")]
    [Tooltip("Start the looping state at a random normalized time instead of frame 0.")]
    [SerializeField] private bool randomizeStartTime = true;
    [Tooltip("Animator.speed is picked randomly in this range (x = min, y = max).")]
    [SerializeField] private Vector2 speedRange = new Vector2(0.94f, 1.06f);

    private const string PostureParam = "posture";
    private const string TalkingParam = "isTalking";
    private const string ThinkingParam = "isThinking";

    private Animator _animator;
    private bool _parametersApplied;

    private void Start()
    {
        if (!TryGetComponent(out _animator))
            _animator = GetComponentInChildren<Animator>();
        if (_animator == null || _animator.runtimeAnimatorController == null)
        {
            enabled = false;
            return;
        }

        TryApplyParameters();

        // Directly jump to the correct state, at a random point of its loop
        // State path format: "SubStateMachineName.StateName"
        float startTime = randomizeStartTime ? Random.value : 0f;
        PlayState(_animator, GetStartState(), startTime);

        // Slight per-NPC speed offset so idles drift apart over time
        if (speedRange.x > 0f && speedRange.y >= speedRange.x)
            _animator.speed = Random.Range(speedRange.x, speedRange.y);
    }

    private void Update()
    {
        // The Animator can report no parameters during its first frames; finish the setup once it does
        if (!_parametersApplied)
            TryApplyParameters();
        else
            enabled = false;
    }

    private void TryApplyParameters()
    {
        if (_animator.parameterCount == 0) return;

        // Only touch parameters this controller actually has (avoids "Parameter does not exist" warnings)
        var parameters = new HashSet<string>();
        foreach (AnimatorControllerParameter p in _animator.parameters)
            parameters.Add(p.name);

        if (parameters.Contains(PostureParam))
            _animator.SetInteger(PostureParam, (int)posture);

        // Only push "true": both default to false, and writing false could undo a value another
        // component (e.g. TableConversation) already set this frame
        if (startTalking && parameters.Contains(TalkingParam))
            _animator.SetBool(TalkingParam, true);
        if (startThinking && parameters.Contains(ThinkingParam))
            _animator.SetBool(ThinkingParam, true);

        _parametersApplied = true;
    }

    private string GetStartState()
    {
        switch (posture)
        {
            case Posture.Standing:
                if (startTalking) return "standing.talking";
                if (startThinking) return "standing.thinking";
                return "standing.idle";

            case Posture.Sitting:
                if (startTalking) return "sitting.talking";
                if (startThinking) return "sitting.thinking";
                return "sitting.idle";

            case Posture.Bartending:
                return "bartending.tending";

            case Posture.SittingFloor:
                return "sitting_floor.idle";

            default:
                return null;
        }
    }

    /// <summary>
    /// Plays a base-layer state, trying the fully qualified path ("Base Layer.standing.idle") first and
    /// then the sub-state path as written. Logs once and leaves the default state if neither exists.
    /// </summary>
    private void PlayState(Animator animator, string statePath, float normalizedTime)
    {
        if (string.IsNullOrEmpty(statePath)) return;

        int fullPathHash = Animator.StringToHash(animator.GetLayerName(0) + "." + statePath);
        if (animator.HasState(0, fullPathHash))
        {
            animator.Play(fullPathHash, 0, normalizedTime);
            return;
        }

        int pathHash = Animator.StringToHash(statePath);
        if (animator.HasState(0, pathHash))
        {
            animator.Play(pathHash, 0, normalizedTime);
            return;
        }

        Debug.LogWarning($"[NPCAnimationSetup] {name}: state '{statePath}' not found on the base layer; " +
                         "relying on the posture parameter instead.");
    }
}
