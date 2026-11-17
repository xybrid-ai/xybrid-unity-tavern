using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Facial expressions an NPC can show. Every value except <see cref="Thinking"/> matches a Synty
/// Sidekick face pose clip (A_FacePose_&lt;Name&gt;.anim); Thinking has no clip and is defined in code by
/// <see cref="NPCFace"/> unless a library provides it.
/// </summary>
public enum FaceEmotion
{
    Neutral,
    Content,
    Happy,
    Delighted,
    Smug,
    Suspicious,
    Grumpy,
    Angry,
    Worried,
    Sad,
    Surprised,
    Scared,
    Disgusted,
    Thinking
}

/// <summary>
/// Per-emotion face blend shape weights, baked from the Synty face pose clips by
/// <c>Tavern &gt; Bake Face Expression Library</c> (FaceExpressionBaker), plus a looping idle motion
/// capture. Shape names are ARKit names without the "MESHBlends." prefix; weights are 0..1 offsets that
/// <see cref="NPCFace"/> adds on top of each character's sculpted rest weights, so one library serves every NPC.
/// </summary>
[CreateAssetMenu(menuName = "Tavern/Face Expression Library", fileName = "FaceExpressions")]
public class FaceExpressionLibrary : ScriptableObject
{
    /// <summary>One blend shape offset of an expression.</summary>
    [System.Serializable]
    public class ShapeWeight
    {
        [Tooltip("ARKit blend shape name without the 'MESHBlends.' prefix, e.g. browInnerUpLeft.")]
        public string shape;
        [Tooltip("Offset added on top of the rest weight, 0..1 (1 = blend shape weight 100).")]
        public float weight;
    }

    /// <summary>The blend shape offsets that make up one emotion.</summary>
    [System.Serializable]
    public class Expression
    {
        public FaceEmotion emotion;
        [Tooltip("Animation clip this expression was baked from (informational).")]
        public string sourceClip;
        public List<ShapeWeight> weights = new List<ShapeWeight>();
    }

    /// <summary>One blend shape's samples within a <see cref="MotionCycle"/>.</summary>
    [System.Serializable]
    public class MotionChannel
    {
        [Tooltip("ARKit blend shape name without the 'MESHBlends.' prefix.")]
        public string shape;
        [Tooltip("Offsets 0..1 at the cycle's sample rate, starting at time 0.")]
        public float[] samples;
    }

    /// <summary>
    /// A looping capture of small face movements (brows, nose, cheeks, lips) baked from a Synty face
    /// cycle clip. Blinks, gaze, jaw and tongue are left out: NPCFace drives those itself.
    /// </summary>
    [System.Serializable]
    public class MotionCycle
    {
        [Tooltip("Animation clip this cycle was baked from (informational).")]
        public string sourceClip;
        [Tooltip("Samples per second of every channel.")]
        public float sampleRate = 30f;
        public List<MotionChannel> channels = new List<MotionChannel>();

        /// <summary>True when the cycle holds at least one channel with two or more samples.</summary>
        public bool IsUsable
        {
            get
            {
                if (channels == null || sampleRate <= 0f) return false;
                for (int i = 0; i < channels.Count; i++)
                    if (channels[i] != null && channels[i].samples != null && channels[i].samples.Length > 1) return true;
                return false;
            }
        }
    }

    public List<Expression> expressions = new List<Expression>();

    [Tooltip("Idle face motion NPCFace loops on top of every expression (baked from A_Face_Neutral).")]
    public MotionCycle ambientMotion = new MotionCycle();

    /// <summary>Finds the first expression for <paramref name="emotion"/>. False when the library has none.</summary>
    public bool TryGet(FaceEmotion emotion, out Expression expression)
    {
        if (expressions != null)
        {
            for (int i = 0; i < expressions.Count; i++)
            {
                Expression candidate = expressions[i];
                if (candidate != null && candidate.emotion == emotion)
                {
                    expression = candidate;
                    return true;
                }
            }
        }

        expression = null;
        return false;
    }
}
