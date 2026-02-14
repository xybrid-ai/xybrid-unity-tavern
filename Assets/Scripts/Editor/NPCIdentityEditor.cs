using UnityEditor;
using UnityEngine;
using Xybrid.ModelAsset;

[CustomEditor(typeof(NPCIdentity))]
public class NPCIdentityEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // Draw all fields except voiceId (we handle that with a custom dropdown)
        DrawPropertiesExcluding(serializedObject, "voiceId");

        // Custom voice ID field with dropdown
        var voiceIdProp = serializedObject.FindProperty("voiceId");
        DrawVoiceIdField(voiceIdProp);

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawVoiceIdField(SerializedProperty prop)
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Voice (TTS)", EditorStyles.boldLabel);

        // Find TTS model assets in the project
        var ttsAsset = FindTTSModelAsset();

        if (ttsAsset == null || !ttsAsset.HasVoices)
        {
            // Fallback: plain text field
            EditorGUILayout.PropertyField(prop, new GUIContent("Voice ID"));
            EditorGUILayout.HelpBox(
                "No TTS model with voice catalog found in project. Import a TTS .xyb to enable voice dropdown.",
                MessageType.Info);
            return;
        }

        // Build dropdown options
        var voices = ttsAsset.voices;
        var options = new string[voices.Length + 1];
        options[0] = $"(Default: {ttsAsset.defaultVoiceId})";
        int selectedIndex = 0;

        for (int i = 0; i < voices.Length; i++)
        {
            options[i + 1] = $"{voices[i].name} ({voices[i].id}) — {voices[i].gender}, {voices[i].language}";
            if (voices[i].id == prop.stringValue)
                selectedIndex = i + 1;
        }

        int newIndex = EditorGUILayout.Popup("Voice", selectedIndex, options);
        if (newIndex == 0)
            prop.stringValue = ""; // empty = use model default
        else
            prop.stringValue = voices[newIndex - 1].id;
    }

    private static XybridModelAsset FindTTSModelAsset()
    {
        // First: look at the models assigned to XybridModelService in the scene
        var services = Object.FindObjectsByType<Tavern.Dialogue.XybridModelService>(
            FindObjectsSortMode.None);
        foreach (var service in services)
        {
            var so = new SerializedObject(service);
            var modelAssetsProp = so.FindProperty("_modelAssets");
            if (modelAssetsProp == null || !modelAssetsProp.isArray) continue;

            for (int i = 0; i < modelAssetsProp.arraySize; i++)
            {
                var element = modelAssetsProp.GetArrayElementAtIndex(i);
                var asset = element.objectReferenceValue as XybridModelAsset;
                if (asset != null && IsTTSAsset(asset))
                    return asset;
            }
        }

        // Fallback: search entire project
        var guids = AssetDatabase.FindAssets("t:XybridModelAsset");
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<XybridModelAsset>(path);
            if (asset != null && IsTTSAsset(asset))
                return asset;
        }
        return null;
    }

    private static bool IsTTSAsset(XybridModelAsset asset)
    {
        string task = asset.task?.ToLowerInvariant() ?? "";
        return task == "text-to-speech" || task == "tts";
    }
}
