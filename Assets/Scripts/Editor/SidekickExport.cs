using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

/// <summary>
/// Exports a saved Sidekick character (.sk) to a Humanoid FBX + prefab without the Sidekick Character
/// Tool's file dialogs, so it can run from the Unity MCP bridge or other automation.
/// It loads the file through the tool's public ModularCharacterWindow.OpenAndLoadCharacter, waits for the
/// preview to settle, then replays ModularCharacterWindow.CreateCharacterPrefab (Sidekick 1.2.7) with a
/// fixed output path. Synty packs are local-only dependencies, so everything binds by type name and the
/// helper fails soft when the pack is missing.
/// </summary>
public static class SidekickExport
{
    private const string WindowTypeName = "Synty.SidekickCharacters.ModularCharacterWindow";
    private const string RigMovementTypeName = "Synty.SidekickCharacters.Database.DTO.SidekickBlendShapeRigMovement";
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    private const double MinLoadSeconds = 2.0;      // The tool debounces its preview rebuild; never export earlier
    private const double SettleSeconds = 1.0;       // Tool state must stay idle this long before exporting
    private const double LoadTimeoutSeconds = 120.0;
    private const double TextureTimeoutSeconds = 15.0;

    /// <summary>Human-readable progress: "idle", "loading ...", "done: &lt;prefab path&gt;" or "error: ...".</summary>
    public static string Status { get; private set; } = "idle";

    private static Type _windowType;
    private static string _fbxPath;
    private static double _startTime;
    private static double _idleSince;

    /// <summary>
    /// Starts the export. <paramref name="fbxAssetPath"/> follows the tool's convention: for
    /// "Assets/Characters/Sidekick/Bob.fbx" the assets land in "Assets/Characters/Sidekick/Bob/".
    /// Progress is reported through <see cref="Status"/>.
    /// </summary>
    public static void Export(string skFilePath, string fbxAssetPath)
    {
        _windowType = FindType(WindowTypeName);
        if (_windowType == null)
        {
            Fail("Sidekick Characters package not found");
            return;
        }
        if (!File.Exists(skFilePath))
        {
            Fail($"no .sk file at {skFilePath}");
            return;
        }

        MethodInfo open = _windowType.GetMethod("OpenAndLoadCharacter", Static);
        if (open == null)
        {
            Fail("ModularCharacterWindow.OpenAndLoadCharacter not found (Sidekick version changed?)");
            return;
        }

        _fbxPath = fbxAssetPath;
        _startTime = EditorApplication.timeSinceStartup;
        _idleSince = -1;
        Status = "loading " + Path.GetFileName(skFilePath);

        open.Invoke(null, new object[] { skFilePath });
        EditorApplication.update -= WaitForLoad;
        EditorApplication.update += WaitForLoad;
    }

    private static void WaitForLoad()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now - _startTime > LoadTimeoutSeconds)
        {
            EditorApplication.update -= WaitForLoad;
            Fail("timed out waiting for the Sidekick tool to load the character");
            return;
        }

        Object window = Resources.FindObjectsOfTypeAll(_windowType).FirstOrDefault();
        bool idle;
        try
        {
            idle = window != null && IsToolIdle(window);
        }
        catch (Exception ex)
        {
            EditorApplication.update -= WaitForLoad;
            Fail(ex.Message);
            return;
        }
        if (!idle)
        {
            _idleSince = -1;
            return;
        }

        if (_idleSince < 0) _idleSince = now;
        if (now - _startTime < MinLoadSeconds || now - _idleSince < SettleSeconds) return;

        EditorApplication.update -= WaitForLoad;
        try
        {
            string prefabPath = ExportLoadedCharacter(window, _fbxPath);
            Status = "done: " + prefabPath;
            Debug.Log("[SidekickExport] " + Status);
        }
        catch (Exception ex)
        {
            Fail((ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex).Message);
        }
    }

    private static bool IsToolIdle(Object window)
    {
        if (Get<bool>(window, "_loadingContent")) return false;
        if (Get<bool>(window, "_loadingCharacter")) return false;
        if (Get<bool>(window, "_applyingPreset")) return false;
        if (Get<bool>(window, "_regenerateQueued")) return false;
        if (_windowType.GetField("_pendingLoadFilePath", Static)?.GetValue(null) != null) return false;
        if (_windowType.GetField("_callbackQueue", Static)?.GetValue(null) is System.Collections.ICollection queue && queue.Count > 0)
            return false;
        return true;
    }

    /// <summary>Mirror of ModularCharacterWindow.CreateCharacterPrefab with the save dialog replaced by a path.</summary>
    private static string ExportLoadedCharacter(Object window, string fbxAssetPath)
    {
        string baseFilename = Path.GetFileNameWithoutExtension(fbxAssetPath);
        string directory = Path.Combine(Path.GetDirectoryName(fbxAssetPath) ?? string.Empty, baseFilename);
        string textureDirectory = Path.Combine(directory, "Textures");
        string materialDirectory = Path.Combine(directory, "Materials");
        string meshDirectory = Path.Combine(directory, "Meshes");
        string fbxPath = Path.Combine(meshDirectory, baseFilename + ".fbx");
        string prefabPath = Path.Combine(directory, baseFilename + ".prefab");

        Directory.CreateDirectory(materialDirectory);
        Directory.CreateDirectory(meshDirectory);

        Call(window, "SaveCharacter", new[] { typeof(string) }, Path.Combine(directory, baseFilename + ".sk"));
        Call(window, "SaveTexturesToDisk", new[] { typeof(string), typeof(string) }, textureDirectory, baseFilename);

        // The tool's material step spins until its texture imports, so make sure they exist before going on
        var wait = Stopwatch.StartNew();
        while (!Directory.Exists(textureDirectory) || Directory.GetFiles(textureDirectory, "*.png").Length == 0)
        {
            if (wait.Elapsed.TotalSeconds > TextureTimeoutSeconds)
                throw new InvalidOperationException("character textures were not written to " + textureDirectory);
            System.Threading.Thread.Sleep(50);
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        bool combineMeshes = Get<bool>(window, "_combineMeshes");
        var clonedModel = (GameObject)Call(window, "GenerateCharacter", new[] { typeof(bool), typeof(bool) }, combineMeshes, false);
        try
        {
            List<SkinnedMeshRenderer> renderers = clonedModel.GetComponentsInChildren<SkinnedMeshRenderer>().ToList();
            if (renderers.Count == 0)
                throw new InvalidOperationException("generated character has no SkinnedMeshRenderer");

            // Blend shapes were baked during generation; apply the matching bone movements like the tool does
            object runtime = Get<object>(window, "_sidekickRuntime");
            object dbManager = Get<object>(window, "_dbManager");
            Type rigMovementType = FindType(RigMovementTypeName);
            object offsetLibrary = rigMovementType.GetMethod("GetAllForProcessing", Static).Invoke(null, new[] { dbManager });
            runtime.GetType().GetMethod("ProcessRigMovementOnBlendShapeChange").Invoke(runtime, new[] { offsetLibrary });
            runtime.GetType().GetMethod("ProcessBoneMovement").Invoke(runtime, new object[] { clonedModel });

            var material = (Material)Call(window, "CreateNewMaterialAssetFromSource",
                new[] { typeof(Material), typeof(string), typeof(string), typeof(string), typeof(string) },
                renderers[0].sharedMaterial, textureDirectory, materialDirectory, baseFilename, baseFilename);
            foreach (SkinnedMeshRenderer renderer in renderers)
                renderer.sharedMaterial = material;

            Animator animator = clonedModel.GetComponentInChildren<Animator>();
            Avatar referenceAvatar = animator != null ? animator.avatar : null;
            Call(window, "ExportCharacterModel",
                new[] { typeof(GameObject), typeof(string), typeof(string), typeof(Material), typeof(Avatar) },
                clonedModel, fbxPath, prefabPath, material, referenceAvatar);
        }
        finally
        {
            Object.DestroyImmediate(clonedModel);
        }

        AssetDatabase.SaveAssets();
        return prefabPath;
    }

    // ─── Reflection helpers ──────────────────────────────────────────────

    private static object Call(object target, string method, Type[] signature, params object[] args)
    {
        MethodInfo info = target.GetType().GetMethod(method, Instance, null, signature, null);
        if (info == null)
            throw new MissingMethodException(target.GetType().Name, method);
        return info.Invoke(target, args);
    }

    private static T Get<T>(object target, string field)
    {
        FieldInfo info = target.GetType().GetField(field, Instance);
        if (info == null)
            throw new MissingFieldException(target.GetType().Name, field);
        return (T)info.GetValue(target);
    }

    private static Type FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(fullName);
            if (type != null) return type;
        }
        return null;
    }

    private static void Fail(string message)
    {
        Status = "error: " + message;
        Debug.LogError("[SidekickExport] " + message);
    }
}
