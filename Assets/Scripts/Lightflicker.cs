using UnityEngine;

/// <summary>
/// Makes a light flicker like fire or candlelight.
/// Uses smooth, frame-rate-independent Perlin noise (plus a slow "breathing" swell)
/// instead of per-frame random picks, so the flicker looks the same at 30 or 144 fps
/// and every light in the room flickers on its own pattern.
/// Attach to any GameObject with a Light component.
/// </summary>
public class LightFlicker : MonoBehaviour
{
    [Header("Flicker Settings")]
    [SerializeField] private float minIntensity = 0.8f;   // Multiplier of the light's starting intensity
    [SerializeField] private float maxIntensity = 1.2f;   // Multiplier of the light's starting intensity
    [Tooltip("Flicker rate. 0.1 is roughly 3.5 Hz of noise; higher values crackle faster.")]
    [SerializeField] private float flickerSpeed = 0.1f;

    [Header("Optional Color Shift")]
    [SerializeField] private bool flickerColor = false;
    [SerializeField] private Color colorA = new Color(1f, 0.6f, 0.2f);  // Orange (low intensity)
    [SerializeField] private Color colorB = new Color(1f, 0.4f, 0.1f);  // Deeper orange (high intensity)

    // flickerSpeed -> noise frequency. Existing scene values sit around 0.06-0.15,
    // which maps to ~2-5 Hz: lively but not strobing.
    private const float SpeedToHz = 35f;
    private const float MinHz = 0.5f;
    private const float MaxHz = 12f;

    private const float DetailOctaveRatio = 2.3f;  // Second, faster noise octave for crackle
    private const float DetailAmount = 0.3f;       // How much of the crackle octave is mixed in
    private const float SwellRatio = 0.08f;        // Slow swell runs at ~8% of the flicker rate
    private const float SwellAmount = 0.15f;       // Share of the range driven by the swell
    private const float Contrast = 1.6f;           // Perlin clusters around 0.5; stretch so min/max are reached

    private Light _light;
    private float _baseIntensity;
    private Color _baseColor;
    private float _seed;
    private bool _initialized;

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
        _baseColor = _light.color;

        // Per-instance offset into the noise field so lights don't pulse in sync
        _seed = Random.Range(0f, 100f);
        _initialized = true;
    }

    private void Update()
    {
        if (!_light.enabled) return;

        float hz = Mathf.Clamp(flickerSpeed * SpeedToHz, MinHz, MaxHz);
        float time = Time.time;

        // Main flicker + a faster, quieter octave on top (both 0..1, centred on 0.5)
        float noise = Mathf.PerlinNoise(_seed, time * hz);
        float detail = Mathf.PerlinNoise(_seed + 31.7f, time * hz * DetailOctaveRatio);
        float n = Mathf.Lerp(noise, detail, DetailAmount);

        // Slow sine swell so the fire "breathes" underneath the crackle
        float swell = Mathf.Sin((time + _seed) * hz * SwellRatio * 2f * Mathf.PI);

        float t = Mathf.Clamp01(0.5f + (n - 0.5f) * Contrast + swell * SwellAmount);

        _light.intensity = _baseIntensity * Mathf.Lerp(minIntensity, maxIntensity, t);

        // Optional color flicker, driven by the same value so color tracks brightness
        if (flickerColor)
        {
            _light.color = Color.Lerp(colorA, colorB, t);
        }
    }

    private void OnDisable()
    {
        // Leave the light as it was authored when the flicker is switched off
        if (!_initialized || _light == null) return;

        _light.intensity = _baseIntensity;
        if (flickerColor)
            _light.color = _baseColor;
    }
}
