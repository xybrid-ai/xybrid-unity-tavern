using System.Collections.Generic;
using Tavern.Telemetry;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Preferences > Xybrid Tavern > Telemetry: a masked field for this project's
/// Xybrid telemetry API key. Editor-only (Editor folder); player builds read
/// the xybrid_api_key environment variable instead.
/// </summary>
internal static class TelemetryPreferencesProvider
{
    private const string FieldControlName = "XybridTavernTelemetryApiKey";

    [SettingsProvider]
    public static SettingsProvider Create()
    {
        return new SettingsProvider("Preferences/Xybrid Tavern/Telemetry", SettingsScope.User)
        {
            label = "Telemetry",
            guiHandler = _ => DrawGui(),
            keywords = new HashSet<string> { "Xybrid", "Telemetry", "API key", TelemetryApiKey.EnvironmentVariable },
        };
    }

    private static void DrawGui()
    {
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Optional. The key is saved in Unity EditorPrefs on this machine, scoped to this project's folder, " +
            "and is never written to scenes, prefabs, assets or Git. EditorPrefs are local configuration, " +
            "not encrypted secret storage.\n\n" +
            "In the Editor a saved key takes precedence over the project .env file and the process environment (" +
            TelemetryApiKey.EnvironmentVariable + "). Player builds never read this key; they use the " +
            TelemetryApiKey.EnvironmentVariable + " environment variable. Without a key, telemetry stays off.",
            MessageType.Info);
        EditorGUILayout.Space();

        string saved = TelemetryEditorPreferences.Load() ?? string.Empty;

        EditorGUI.BeginChangeCheck();
        GUI.SetNextControlName(FieldControlName);
        string edited = EditorGUILayout.PasswordField("API Key", saved);
        if (EditorGUI.EndChangeCheck())
            TelemetryEditorPreferences.Save(edited);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel(" ");
        using (new EditorGUI.DisabledScope(saved.Length == 0))
        {
            if (GUILayout.Button("Clear", GUILayout.Width(80)))
            {
                TelemetryEditorPreferences.Clear();
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Status", saved.Length > 0
            ? "Key saved for this project. Used the next time the model service starts."
            : "No key saved. Falls back to .env or the " + TelemetryApiKey.EnvironmentVariable + " environment variable.",
            EditorStyles.wordWrappedLabel);
    }
}
