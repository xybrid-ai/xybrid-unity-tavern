#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Tavern.Telemetry
{
    /// <summary>
    /// Editor-only storage for the telemetry API key, in Unity EditorPrefs.
    /// EditorPrefs live in the user's Unity preferences (on macOS a plist under
    /// ~/Library/Preferences), outside the project, so the key never reaches
    /// scenes, prefabs, assets, Git or builds. The preference name is scoped by
    /// a hash of the project path, so other projects and other checkouts of this
    /// one do not share it. EditorPrefs are plain local preferences, not
    /// encrypted secret storage.
    /// </summary>
    public static class TelemetryEditorPreferences
    {
        private const string PreferencePrefix = "XybridTavern.Telemetry.ApiKey.";

        /// <summary>Root folder of the open project (the parent of Assets).</summary>
        public static string CurrentProjectRoot => Path.GetDirectoryName(Application.dataPath);

        public static string Load() => Load(CurrentProjectRoot);

        public static bool HasKey() => HasKey(CurrentProjectRoot);

        public static void Save(string apiKey) => Save(CurrentProjectRoot, apiKey);

        public static void Clear() => Clear(CurrentProjectRoot);

        public static string Load(string projectRoot)
        {
            string value = EditorPrefs.GetString(PreferenceNameFor(projectRoot), string.Empty);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public static bool HasKey(string projectRoot) => Load(projectRoot) != null;

        /// <summary>Saves the key for a project; a blank key clears it.</summary>
        public static void Save(string projectRoot, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Clear(projectRoot);
                return;
            }
            EditorPrefs.SetString(PreferenceNameFor(projectRoot), apiKey.Trim());
        }

        public static void Clear(string projectRoot) => EditorPrefs.DeleteKey(PreferenceNameFor(projectRoot));

        /// <summary>EditorPrefs name for a project root. Contains no part of the key.</summary>
        public static string PreferenceNameFor(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException("Project root is required.", nameof(projectRoot));

            string normalized = Path.GetFullPath(projectRoot).Replace('\\', '/').TrimEnd('/');
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var hex = new StringBuilder(PreferencePrefix.Length + 32).Append(PreferencePrefix);
                for (int i = 0; i < 16; i++) hex.Append(hash[i].ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
#endif
