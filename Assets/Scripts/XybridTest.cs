using UnityEngine;
using Xybrid;

/// <summary>
/// Simple test to verify Xybrid SDK is working.
/// Attach to any GameObject and check the Console on Play.
/// </summary>
public class XybridDirectTest : MonoBehaviour
{
    [SerializeField] private string modelId = "gemma-3-1b";
    [SerializeField] private string testPrompt = "Say hello in one sentence.";

    private void Start()
    {
        TestXybridDirect();
    }

    [ContextMenu("Test Xybrid")]
    public void TestXybridDirect()
    {
        Debug.Log("=== XYBRID DIRECT TEST ===");

        try
        {
            // Initialize
            Debug.Log("Initializing XybridClient...");
            XybridClient.Initialize();
            Debug.Log($"SDK Version: {XybridClient.Version}");

            // Load model
            Debug.Log($"Loading model: {modelId}");
            using (var model = XybridClient.LoadModel(modelId))
            {
                Debug.Log($"Model loaded: {model.ModelId}");

                // Test 1: Simple RunText
                Debug.Log($"Running inference with prompt: '{testPrompt}'");
                try
                {
                    string result = model.RunText(testPrompt);
                    Debug.Log($"SUCCESS! Result: '{result}'");
                    Debug.Log($"Result length: {result?.Length ?? 0}");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"RunText failed: {ex.GetType().Name}: {ex.Message}");
                }

                // Test 2: Manual envelope
                Debug.Log("Testing with manual envelope...");
                try
                {
                    using (var envelope = Envelope.Text(testPrompt))
                    {
                        Debug.Log("Envelope created");
                        using (var inferenceResult = model.Run(envelope))
                        {
                            Debug.Log($"Run completed");
                            Debug.Log($"  Success: {inferenceResult.Success}");
                            Debug.Log($"  LatencyMs: {inferenceResult.LatencyMs}");
                            Debug.Log($"  Text: '{inferenceResult.Text}'");
                            Debug.Log($"  Text is null: {inferenceResult.Text == null}");
                            Debug.Log($"  Text is empty: {inferenceResult.Text == ""}");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"Manual envelope test failed: {ex.GetType().Name}: {ex.Message}");
                    Debug.LogError($"Stack: {ex.StackTrace}");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Test failed: {ex.GetType().Name}: {ex.Message}");
            Debug.LogError($"Stack: {ex.StackTrace}");
        }

        Debug.Log("=== TEST COMPLETE ===");
    }
}