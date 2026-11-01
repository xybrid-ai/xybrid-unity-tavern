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
/// <c>Tavern &gt; Bake Face Expression Library</c> (FaceExpressionBaker). Shape names are ARKit names
/// without the "MESHBlends." prefix; weights are 0..1 offsets that <see cref="NPCFace"/> adds on top of
/// each character's sculpted rest weights, so one library serves every NPC.
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

    public List<Expression> expressions = new List<Expression>();

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
