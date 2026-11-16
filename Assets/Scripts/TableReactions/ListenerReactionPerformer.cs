using System;
using System.Collections;
using System.Collections.Generic;
using Tavern.Social;
using UnityEngine;

/// <summary>
/// Plays one listener's <see cref="ReactionPlan"/> through the existing character systems and records
/// what each of them accepted:
/// gaze → <see cref="NPCLookAt.LookAtFor"/> at Awareness priority (above the table's Social watch look,
/// below Dialogue; it expires on its own and the watch look resumes), gestures → <see cref="NPCGestures.Play"/>
/// (which refuses while busy or too soon; refusals are logged, never retried), faces →
/// <see cref="NPCFace.ShowEmotion"/>. Never touches the NPC in dialogue, movement or speech.
/// </summary>
public static class ListenerReactionPerformer
{
    /// <summary>Seconds after the look starts before a nod/shake, so the head is turning first.</summary>
    private const float GestureAfterLook = 0.25f;

    /// <param name="stillValid">Re-checked before each step (session open, turn current, listener alive).</param>
    public static IEnumerator Perform(ReactionPlan plan, NPCIdentity listener, Func<string, Transform> resolveTarget,
        Func<string> stillValid, ListenerTrace trace, TableConversation table, Action onDone)
    {
        yield return new WaitForSeconds((float)plan.DelaySeconds);

        string rejection = stillValid();
        if (rejection == null && listener == null) rejection = "listener gone";
        if (rejection == null && listener == DialogueManagerV2.ActiveNPC) rejection = "listener is in dialogue";

        NPCFace face = listener != null ? listener.GetComponentInChildren<NPCFace>() : null;
        if (rejection == null && face != null && face.IsSpeakingAudio) rejection = "listener is speaking";
        if (rejection != null)
        {
            trace.Outcome = "rejected: " + rejection;
            onDone();
            yield break;
        }

        listener.TryGetComponent(out NPCLookAt lookAt);
        listener.TryGetComponent(out NPCGestures gestures);
        int accepted = 0, declined = 0;

        if (table != null)
            table.SuppressWatchGlance(listener, (float)Math.Max(plan.GazeSeconds, 1.5) + 0.5f);

        // Gaze
        if (plan.Gaze != GazeKind.None)
        {
            Transform target = resolveTarget(plan.GazeTargetId);
            if (lookAt == null || target == null)
            {
                trace.Steps.Add("look " + plan.GazeTargetId + ": unavailable");
                declined++;
            }
            else
            {
                lookAt.LookAtFor(target, (float)plan.GazeSeconds, LookPriority.Awareness);
                trace.Steps.Add("look " + plan.GazeTargetId + ": accepted");
                accepted++;
            }
        }

        // Face (one transient slot; it fades back to the base mood on its own)
        if (plan.Face != null)
        {
            if (face == null || !Enum.TryParse(plan.Face, out FaceEmotion emotion))
            {
                trace.Steps.Add("face " + plan.Face + ": unavailable");
                declined++;
            }
            else
            {
                face.ShowEmotion(emotion, (float)plan.FaceIntensity, (float)plan.FaceHoldSeconds);
                trace.Steps.Add("face " + plan.Face + ": accepted");
                accepted++;
            }
        }

        // Gesture
        if (plan.Gesture != ReactionGesture.None)
        {
            if (plan.Gaze != GazeKind.None && (plan.Gesture == ReactionGesture.Nod || plan.Gesture == ReactionGesture.Shake))
                yield return new WaitForSeconds(GestureAfterLook);

            string again = stillValid();
            if (again != null || listener == null)
            {
                trace.Steps.Add("gesture: dropped (" + (again ?? "listener gone") + ")");
                declined++;
            }
            else if (gestures == null)
            {
                trace.Steps.Add("gesture " + plan.Gesture + ": no NPCGestures");
                declined++;
            }
            else
            {
                NPCGesture? g = ToGesture(plan, gestures.transform, resolveTarget);
                bool played = g.HasValue && gestures.Play(g.Value);
                trace.Steps.Add("gesture " + (g.HasValue ? g.Value.ToString() : plan.Gesture.ToString()) +
                                (played ? ": accepted" : ": declined (busy, too soon, or no target)"));
                if (played) accepted++;
                else declined++;
            }
        }

        trace.Outcome = declined == 0 ? "executed" : accepted > 0 ? "partial (" + declined + " declined)" : "declined";
        onDone();
    }

    private static NPCGesture? ToGesture(ReactionPlan plan, Transform self, Func<string, Transform> resolveTarget)
    {
        switch (plan.Gesture)
        {
            case ReactionGesture.Nod: return NPCGesture.Nod;
            case ReactionGesture.Shake: return NPCGesture.Shake;
            case ReactionGesture.GlanceAway:
            {
                // Look off to the side away from the player (deflecting, embarrassed)
                Transform player = resolveTarget(SocialIds.Player);
                if (player == null) return NPCGesture.GlanceLeft;
                return self.InverseTransformPoint(player.position).x < 0f ? NPCGesture.GlanceRight : NPCGesture.GlanceLeft;
            }
            case ReactionGesture.GlanceToReferent:
            {
                Transform target = resolveTarget(plan.GestureTargetId);
                if (target == null) return null;
                return self.InverseTransformPoint(target.position).x < 0f ? NPCGesture.GlanceLeft : NPCGesture.GlanceRight;
            }
            default:
                return null;
        }
    }
}
