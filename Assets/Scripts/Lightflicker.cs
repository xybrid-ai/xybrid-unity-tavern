using UnityEngine;

/// <summary>
/// Makes a light flicker like fire or candlelight.
/// Attach to any GameObject with a Light component.
/// </summary>
public class LightFlicker : MonoBehaviour
{
    [Header("Flicker Settings")]
    [SerializeField] private float minIntensity = 0.8f;
    [SerializeField] private float maxIntensity = 1.2f;
    [SerializeField] private float flickerSpeed = 0.1f;

    [Header("Optional Color Shift")]
    [SerializeField] private bool flickerColor = false;
    [SerializeField] private Color colorA = new Color(1f, 0.6f, 0.2f);  // Orange
    [SerializeField] private Color colorB = new Color(1f, 0.4f, 0.1f);  // Deeper orange

    private Light _light;
    private float _baseIntensity;
    private float _targetIntensity;
    private float _currentVelocity;

    private void Start()
    {
        _light = GetComponent<Light>();
        if (_light == null)
        {
            Debug.LogWarning("[LightFlicker] No Light component found!");
            enabled = false;
            return;
        }

        _baseIntensity = _light.intensity;
        _targetIntensity = _baseIntensity;
    }

    private void Update()
    {
        // Pick new target randomly
        if (Random.value < flickerSpeed)
        {
            _targetIntensity = Random.Range(minIntensity, maxIntensity) * _baseIntensity;
        }

        // Smooth toward target
        _light.intensity = Mathf.SmoothDamp(
            _light.intensity,
            _targetIntensity,
            ref _currentVelocity,
            flickerSpeed
        );

        // Optional color flicker
        if (flickerColor)
        {
            float t = (_light.intensity - minIntensity * _baseIntensity) / 
                      ((maxIntensity - minIntensity) * _baseIntensity);
            _light.color = Color.Lerp(colorA, colorB, t);
        }
    }
}