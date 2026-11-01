using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Bakes the Synty Sidekick face pose clips (A_FacePose_&lt;Name&gt;.anim) into a
/// <see cref="FaceExpressionLibrary"/> asset that NPCFace reads at runtime. Each clip's blend-shape
/// curves are sampled once (value at time 0, or the max-abs value when the clip has several keys),
/// converted from Unity's 0..100 range to 0..1, and stored by ARKit name without the "MESHBlends."
/// prefix. Body-shape blends, tongue shapes and eye-gaze (eyeLook*) shapes are never baked.
/// The asset is updated in place so its GUID (and every reference to it) survives a re-bake;
/// hand-authored entries for emotions that have no clip (e.g. Thinking) are kept.
/// Synty packs are local-only dependencies: when the face pose folder is missing the bake just
/// warns and returns null.
/// </summary>
public static class FaceExpressionBaker
{
    private const string DefaultAssetPath = "Assets/Revamp/FaceExpressions.asset";
    private const string FacePoseFolder = "Assets/Synty/SidekickCharacters/Animations/FacePoses";
    private const string ClipPrefix = "A_FacePose_";
    private const string BlendShapePrefix = "blendShape.";
    private const string MeshBlendsPrefix = "MESHBlends.";
    private const float MinWeight = 0.02f;          // Weights below this (after /100) are noise
    private const float SamplesPerSecond = 60f;     // Multi-key clips: sampling rate for the max-abs scan
    private const int SummaryShapeCount = 4;        // Shapes listed in each per-expression log line

    // Character body shapes share the face mesh; a face expression must never touch them
    private static readonly string[] BodyShapes = { "defaultHeavy", "defaultBuff", "defaultSkinny", "masculineFeminine" };

    // Clip suffix -> emotion. Variants (Happy2, Angry2, Enraged, Distraught, Terrified, InPain) and the
    // Blink / Eye* clips are ignored: NPCFace drives blinks itself, and Thinking is defined in code.
    private static readonly (string clip, FaceEmotion emotion)[] ClipMap =
    {
        ("Neutral", FaceEmotion.Neutral),
        ("Content", FaceEmotion.Content),
        ("Happy", FaceEmotion.Happy),
        ("Delighted", FaceEmotion.Delighted),
        ("Smug", FaceEmotion.Smug),
        ("Suspicious", FaceEmotion.Suspicious),
        ("Grumpy", FaceEmotion.Grumpy),
        ("Angry", FaceEmotion.Angry),
        ("Worried", FaceEmotion.Worried),
        ("Sad", FaceEmotion.Sad),
        ("Surprised", FaceEmotion.Surprised),
        ("Scared", FaceEmotion.Scared),
        ("Disgusted", FaceEmotion.Disgusted),
    };

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>Menu entry: bakes into the default asset path and pings the result.</summary>
    [MenuItem("Tavern/Bake Face Expression Library")]
    public static void BakeMenu()
    {
        FaceExpressionLibrary library = Bake();
        if (library != null && !Application.isBatchMode)
            EditorGUIUtility.PingObject(library);
    }

    /// <summary>
    /// Bakes every mapped face pose clip into the library at <paramref name="assetPath"/>, creating the
    /// asset (and its folders) when missing or updating it in place otherwise. Returns the library, or
    /// null when the Synty folder is missing, no clip could be baked, or the path is unusable.
    /// </summary>
    public static FaceExpressionLibrary Bake(string assetPath = DefaultAssetPath)
    {
        if (!IsValidAssetPath(assetPath))
        {
            Debug.LogWarning($"[FaceExpressionBaker] '{assetPath}' is not a valid asset path " +
                             "(expected 'Assets/.../Name.asset'). Nothing baked.");
            return null;
        }

        if (!AssetDatabase.IsValidFolder(FacePoseFolder))
        {
            Debug.LogWarning($"[FaceExpressionBaker] Face pose folder '{FacePoseFolder}' not found " +
                             "(Synty Sidekick Characters not installed?). Nothing baked.");
            return null;
        }

        // ---- Bake each clip ----
        var baked = new List<FaceExpressionLibrary.Expression>(ClipMap.Length);
        var bakedEmotions = new HashSet<FaceEmotion>();
        foreach ((string clipSuffix, FaceEmotion emotion) in ClipMap)
        {
            string clipName = ClipPrefix + clipSuffix;
            AnimationClip clip = FindClip(clipName);
            if (clip == null)
            {
                Debug.LogWarning($"[FaceExpressionBaker] {emotion}: clip '{clipName}' not found under {FacePoseFolder}; skipped.");
                continue;
            }

            FaceExpressionLibrary.Expression expression = BakeClip(clip, emotion);
            baked.Add(expression);
            bakedEmotions.Add(emotion);
            Debug.Log(Summary(expression));
        }

        if (baked.Count == 0)
        {
            // Don't wipe an existing library because the clips vanished
            Debug.LogWarning("[FaceExpressionBaker] No face pose clips could be baked; the library was not changed.");
            return null;
        }

        // ---- Create or update the asset ----
        FaceExpressionLibrary library = LoadOrCreate(assetPath, out bool created);
        if (library == null) return null;

        if (!created)
            Undo.RecordObject(library, "Bake Face Expression Library");

        List<FaceExpressionLibrary.Expression> kept = KeepUnbaked(library.expressions, bakedEmotions);
        library.expressions = Merge(baked, kept);

        if (created)
            AssetDatabase.CreateAsset(library, assetPath);

        EditorUtility.SetDirty(library);
        AssetDatabase.SaveAssetIfDirty(library);

        Debug.Log($"[FaceExpressionBaker] {(created ? "Created" : "Updated")} {assetPath}: " +
                  $"{baked.Count} baked expression(s), {kept.Count} hand-authored kept.");
        return library;
    }

    // ================================================================
    // Clip sampling
    // ================================================================

    /// <summary>Looks for the clip at its usual path first, then anywhere under the face pose folder.</summary>
    private static AnimationClip FindClip(string clipName)
    {
        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(FacePoseFolder + "/" + clipName + ".anim");
        if (clip != null) return clip;

        // Fallback: subfolders, or a clip that only exists inside its .fbx. FindAssets matches names
        // loosely ("Happy" also finds "Happy2"), so require an exact name
        foreach (string guid in AssetDatabase.FindAssets(clipName + " t:AnimationClip", new[] { FacePoseFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is AnimationClip candidate && candidate.name == clipName)
                    return candidate;
            }
        }
        return null;
    }

    private static FaceExpressionLibrary.Expression BakeClip(AnimationClip clip, FaceEmotion emotion)
    {
        // Shape -> weight; a shape can be bound more than once (several paths), keep the strongest
        var weights = new Dictionary<string, float>(StringComparer.Ordinal);

        foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
        {
            string shape = ShapeName(binding.propertyName);
            if (shape == null || IsExcluded(shape)) continue;

            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            float weight = PoseValue(curve) / 100f;
            if (Mathf.Abs(weight) < MinWeight) continue;

            // Library weights are 0..1: Synty overshoots 100 on a few shapes, and negative weights
            // aren't meaningful for these blends
            weight = Mathf.Clamp01(weight);
            if (weight < MinWeight) continue;

            if (!weights.TryGetValue(shape, out float existing) || weight > existing)
                weights[shape] = weight;
        }

        var expression = new FaceExpressionLibrary.Expression
        {
            emotion = emotion,
            sourceClip = clip.name
        };
        foreach (KeyValuePair<string, float> pair in weights)
            expression.weights.Add(new FaceExpressionLibrary.ShapeWeight { shape = pair.Key, weight = pair.Value });

        // Strongest first (readable in the Inspector), ties by name so re-bakes produce identical assets
        expression.weights.Sort((a, b) =>
        {
            int byWeight = b.weight.CompareTo(a.weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.shape, b.shape);
        });
        return expression;
    }

    /// <summary>
    /// The pose value of a curve: its value at time 0, or for multi-key curves the value with the
    /// largest magnitude over the clip (keys plus sampled in-betweens, to catch tangent overshoot).
    /// </summary>
    private static float PoseValue(AnimationCurve curve)
    {
        if (curve == null || curve.length == 0) return 0f;
        if (curve.length == 1) return curve.Evaluate(0f);

        Keyframe[] keys = curve.keys;
        float best = 0f;
        for (int i = 0; i < keys.Length; i++)
            best = MaxAbs(best, keys[i].value);

        float start = keys[0].time;
        float end = keys[keys.Length - 1].time;
        int samples = Mathf.Clamp(Mathf.CeilToInt((end - start) * SamplesPerSecond), 1, 600);
        for (int i = 0; i <= samples; i++)
            best = MaxAbs(best, curve.Evaluate(Mathf.Lerp(start, end, i / (float)samples)));

        return best;
    }

    private static float MaxAbs(float a, float b)
    {
        return Mathf.Abs(b) > Mathf.Abs(a) ? b : a;
    }

    /// <summary>"blendShape.MESHBlends.jawOpen" -> "jawOpen"; null when the binding isn't a blend shape.</summary>
    private static string ShapeName(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName) || !propertyName.StartsWith(BlendShapePrefix, StringComparison.Ordinal))
            return null;

        string shape = propertyName.Substring(BlendShapePrefix.Length);
        if (shape.StartsWith(MeshBlendsPrefix, StringComparison.Ordinal))
            shape = shape.Substring(MeshBlendsPrefix.Length);
        return shape.Length > 0 ? shape : null;
    }

    private static bool IsExcluded(string shape)
    {
        if (shape.StartsWith("tongue", StringComparison.OrdinalIgnoreCase)) return true;
        // Gaze belongs to NPCLookAt's eye IK; Synty poses set eyeLook* up to 1.0, which would fight it
        if (shape.StartsWith("eyeLook", StringComparison.OrdinalIgnoreCase)) return true;
        for (int i = 0; i < BodyShapes.Length; i++)
        {
            if (string.Equals(shape, BodyShapes[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ================================================================
    // Asset handling
    // ================================================================

    private static bool IsValidAssetPath(string assetPath)
    {
        return !string.IsNullOrEmpty(assetPath)
               && assetPath.StartsWith("Assets/", StringComparison.Ordinal)
               && assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
               && Path.GetFileNameWithoutExtension(assetPath).Length > 0;
    }

    /// <summary>
    /// Loads the existing library, or makes a new in-memory one (not yet saved) when nothing is at the
    /// path. Refuses to touch a file that holds something else, or one the AssetDatabase can't load,
    /// so an existing GUID is never replaced.
    /// </summary>
    private static FaceExpressionLibrary LoadOrCreate(string assetPath, out bool created)
    {
        created = false;

        Object existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
        if (existing == null && File.Exists(assetPath))
        {
            // On disk but not imported yet (e.g. written while the Editor was closed)
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (existing == null)
            {
                Debug.LogWarning($"[FaceExpressionBaker] '{assetPath}' exists but can't be loaded; " +
                                 "fix or delete it, then bake again.");
                return null;
            }
        }

        if (existing != null)
        {
            if (existing is FaceExpressionLibrary library) return library;
            Debug.LogWarning($"[FaceExpressionBaker] '{assetPath}' is a {existing.GetType().Name}, not a " +
                             "FaceExpressionLibrary; pick another path. Nothing baked.");
            return null;
        }

        string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
        if (!EnsureFolder(folder))
        {
            Debug.LogWarning($"[FaceExpressionBaker] Couldn't create folder '{folder}'. Nothing baked.");
            return null;
        }

        var fresh = ScriptableObject.CreateInstance<FaceExpressionLibrary>();
        fresh.name = Path.GetFileNameWithoutExtension(assetPath);
        created = true;
        return fresh;
    }

    /// <summary>Creates a project folder and any missing parents ("Assets" always exists).</summary>
    private static bool EnsureFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        if (AssetDatabase.IsValidFolder(folder)) return true;

        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        string leaf = Path.GetFileName(folder);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf) || !EnsureFolder(parent))
            return false;

        return !string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, leaf));
    }

    /// <summary>Existing entries for emotions this bake did not produce (hand-authored Thinking, a missing clip...).</summary>
    private static List<FaceExpressionLibrary.Expression> KeepUnbaked(
        List<FaceExpressionLibrary.Expression> existing, HashSet<FaceEmotion> bakedEmotions)
    {
        var kept = new List<FaceExpressionLibrary.Expression>();
        if (existing == null) return kept;

        foreach (FaceExpressionLibrary.Expression expression in existing)
        {
            if (expression != null && !bakedEmotions.Contains(expression.emotion))
                kept.Add(expression);
        }
        return kept;
    }

    /// <summary>Baked + kept entries in FaceEmotion order, so the Inspector and asset diffs stay stable.</summary>
    private static List<FaceExpressionLibrary.Expression> Merge(
        List<FaceExpressionLibrary.Expression> baked, List<FaceExpressionLibrary.Expression> kept)
    {
        var merged = new List<FaceExpressionLibrary.Expression>(baked.Count + kept.Count);
        foreach (FaceEmotion emotion in (FaceEmotion[])Enum.GetValues(typeof(FaceEmotion)))
        {
            foreach (FaceExpressionLibrary.Expression expression in baked)
                if (expression.emotion == emotion) merged.Add(expression);
            foreach (FaceExpressionLibrary.Expression expression in kept)
                if (expression.emotion == emotion) merged.Add(expression);
        }

        // Entries whose stored enum value no longer exists: keep them rather than silently dropping data
        foreach (FaceExpressionLibrary.Expression expression in kept)
        {
            if (!Enum.IsDefined(typeof(FaceEmotion), expression.emotion))
                merged.Add(expression);
        }
        return merged;
    }

    private static string Summary(FaceExpressionLibrary.Expression expression)
    {
        var line = new StringBuilder();
        line.Append("[FaceExpressionBaker] ").Append(expression.emotion)
            .Append(" <- ").Append(expression.sourceClip)
            .Append(": ").Append(expression.weights.Count).Append(" shape(s)");

        if (expression.weights.Count == 0)
        {
            line.Append(" (rest face)");
            return line.ToString();
        }

        line.Append(" [");
        int count = Mathf.Min(SummaryShapeCount, expression.weights.Count);
        for (int i = 0; i < count; i++)
        {
            FaceExpressionLibrary.ShapeWeight w = expression.weights[i];
            if (i > 0) line.Append(", ");
            line.Append(w.shape).Append(' ').Append(w.weight.ToString("0.00"));
        }
        if (expression.weights.Count > count) line.Append(", ...");
        line.Append(']');
        return line.ToString();
    }
}
