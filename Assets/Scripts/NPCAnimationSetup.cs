using UnityEngine;

/// <summary>
/// Forces the correct animation state on Start.
/// Attach to the same GameObject as the Animator (the model child).
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

    private void Start()
    {
        Animator animator = GetComponent<Animator>();
        if (animator == null) return;

        // Set parameters
        animator.SetInteger("posture", (int)posture);
        animator.SetBool("isTalking", startTalking);
        animator.SetBool("isThinking", startThinking);

        // Directly jump to the correct state
        // State path format: "SubStateMachineName.StateName"
        switch (posture)
        {
            case Posture.Standing:
                if (startTalking)
                    animator.Play("standing.talking", 0, 0f);
                else if (startThinking)
                    animator.Play("standing.thinking", 0, 0f);
                else
                    animator.Play("standing.idle", 0, 0f);
                break;
                
            case Posture.Sitting:
                if (startThinking)
                    animator.Play("sitting.thinking", 0, 0f);
                else
                    animator.Play("sitting.idle", 0, 0f);  // This is your sitting idle
                break;
                
            case Posture.Bartending:
                animator.Play("bartending.tending", 0, 0f);
                break;
                
            case Posture.SittingFloor:
                animator.Play("sitting_floor.idle", 0, 0f);
                break;
        }
    }
}