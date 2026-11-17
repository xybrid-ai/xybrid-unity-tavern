#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

/// <summary>
/// Batch-mode entry points driven by scripts/unity.sh.
/// Unity only reaches an -executeMethod target when the editor assemblies
/// already compiled, so these methods report the result and set the exit code
/// explicitly instead of letting batch mode exit 0 on a half-broken domain.
/// </summary>
public static class UnityCli
{
    /// <summary>
    /// Recompiles every player assembly and exits non-zero on the first error.
    /// Invoked by: ./scripts/unity.sh compile
    /// </summary>
    public static void CompileCheck()
    {
        int errorCount = 0;

        foreach (Assembly assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
        {
            string[] missing = assembly.compiledAssemblyReferences
                .Where(reference => !System.IO.File.Exists(reference))
                .ToArray();

            foreach (string reference in missing)
            {
                Debug.LogError($"[UnityCli] {assembly.name}: missing assembly reference '{reference}'");
                errorCount++;
            }
        }

        if (EditorUtility.scriptCompilationFailed)
        {
            Debug.LogError("[UnityCli] Script compilation failed — see 'error CS' lines above.");
            errorCount++;
        }

        if (errorCount > 0)
        {
            Debug.LogError($"[UnityCli] CompileCheck FAILED with {errorCount} error(s).");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log("[UnityCli] CompileCheck OK — all player assemblies compiled.");
        EditorApplication.Exit(0);
    }

    /// <summary>
    /// Forces a full reimport, then exits. Useful after an agent edits scripts
    /// while the Editor is closed.
    /// Invoked by: ./scripts/unity.sh method UnityCli.Refresh
    /// </summary>
    public static void Refresh()
    {
        try
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            Debug.Log("[UnityCli] AssetDatabase refreshed.");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError($"[UnityCli] Refresh failed: {e}");
            EditorApplication.Exit(1);
        }
    }
}
#endif
