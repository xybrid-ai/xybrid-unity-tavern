using System;
using UnityEngine;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Plays TTS audio from raw PCM bytes (16-bit signed LE, 24kHz, mono).
    /// Attach to a GameObject with an AudioSource, or one will be auto-created.
    /// After <see cref="SetSpeaker"/> the source follows that NPC's head as a mostly-3D sound,
    /// so the voice comes from whoever is talking; SetSpeaker(null) restores the original 2D setup.
    /// </summary>
    public class TTSAudioPlayer : MonoBehaviour
    {
        [SerializeField] private AudioSource _audioSource;

        [Header("Speaker Voice (3D)")]
        [Tooltip("Within this distance of the speaking NPC the voice plays at full volume.")]
        [SerializeField] private float speakerMinDistance = 1.5f;
        [Tooltip("Distance at which the voice reaches its quietest.")]
        [SerializeField] private float speakerMaxDistance = 12f;
        [SerializeField] private AudioRolloffMode speakerRolloff = AudioRolloffMode.Linear;

        /// <summary>Sample rate of the PCM given to <see cref="PlayPCM"/> (Phonon TTS output).</summary>
        public const int SampleRate = 24000;
        private const int Channels = 1;

        // Speaker following (SetSpeaker)
        private Transform _speaker;
        private Transform _speakerHead;
        private float _speakerBlend;
        private bool _hasSpeaker;
        private bool _canMoveSource;

        // The source's own settings, captured before the first SetSpeaker and restored by SetSpeaker(null)
        private bool _defaultsCaptured;
        private Vector3 _defaultLocalPosition;
        private float _defaultSpatialBlend;
        private AudioRolloffMode _defaultRolloff;
        private float _defaultMinDistance;
        private float _defaultMaxDistance;
        private float _defaultDopplerLevel;

        /// <summary>The AudioSource TTS clips play on (read its clip/time to sync lips).</summary>
        public AudioSource Source => _audioSource;

        /// <summary>
        /// Whether TTS audio is currently playing.
        /// </summary>
        public bool IsPlaying => _audioSource != null && _audioSource.isPlaying;

        private void Awake()
        {
            EnsureSource();
        }

        private void LateUpdate()
        {
            if (!_hasSpeaker) return;

            // Speaker destroyed mid-line: fall back to the plain 2D voice
            if (_speaker == null)
            {
                SetSpeaker(null);
                return;
            }

            if (_audioSource != null && _audioSource.isPlaying)
                FollowSpeaker();
        }

        /// <summary>
        /// Convert raw PCM bytes (16-bit signed LE, 24kHz mono) to a Unity AudioClip.
        /// </summary>
        public static AudioClip PCMToAudioClip(byte[] pcmBytes, string clipName = "tts_clip")
        {
            if (pcmBytes == null || pcmBytes.Length < 2) return null;

            int sampleCount = pcmBytes.Length / 2; // 16-bit = 2 bytes per sample
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                short sample = (short)(pcmBytes[i * 2] | (pcmBytes[i * 2 + 1] << 8));
                samples[i] = sample / 32768f;
            }

            var clip = AudioClip.Create(clipName, sampleCount, Channels, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Play PCM audio bytes. Stops any currently playing TTS audio first.
        /// Returns the clip now playing (owned by this player, destroyed on the next Stop), or null.
        /// </summary>
        public AudioClip PlayPCM(byte[] pcmBytes)
        {
            if (pcmBytes == null || pcmBytes.Length == 0) return null;
            if (!EnsureSource()) return null;

            Stop();

            var clip = PCMToAudioClip(pcmBytes, "npc_tts");
            if (clip == null) return null;

            // Re-place the voice at the speaker's head every time a line starts
            if (_hasSpeaker)
            {
                if (_speaker == null)
                {
                    SetSpeaker(null);
                }
                else
                {
                    _speakerHead = NPCLookAt.HeadOf(_speaker);
                    ApplySpeakerSettings();
                    FollowSpeaker();
                }
            }

            _audioSource.clip = clip;
            _audioSource.Play();
            Debug.Log($"[TTSAudioPlayer] Playing {clip.length:F1}s of TTS audio ({pcmBytes.Length} bytes)");
            return clip;
        }

        /// <summary>
        /// Stop playback and clean up the current clip to free memory.
        /// </summary>
        public void Stop()
        {
            if (_audioSource == null) return;

            if (_audioSource.isPlaying)
                _audioSource.Stop();

            if (_audioSource.clip != null)
            {
                var oldClip = _audioSource.clip;
                _audioSource.clip = null;
                Destroy(oldClip);
            }
        }

        /// <summary>
        /// Make the voice come from <paramref name="speaker"/>: the AudioSource is placed at the speaker's head
        /// (via <see cref="NPCLookAt.HeadOf"/>) whenever a line plays and follows it while playing, with the
        /// given spatial blend and a 1.5 m / 12 m rolloff. Pass null to restore the original 2D voice.
        /// A source that shares its GameObject with a Camera or AudioListener is never moved and stays 2D.
        /// </summary>
        public void SetSpeaker(Transform speaker, float spatialBlend = 0.75f)
        {
            if (!EnsureSource()) return;

            if (speaker == null)
            {
                ClearSpeaker();
                return;
            }

            if (!_hasSpeaker)
                CaptureDefaults();

            _speaker = speaker;
            _speakerHead = NPCLookAt.HeadOf(speaker);
            _speakerBlend = Mathf.Clamp01(spatialBlend);
            _hasSpeaker = true;

            ApplySpeakerSettings();
            FollowSpeaker();
        }

        // ================================================================
        // Internals
        // ================================================================

        private bool EnsureSource()
        {
            if (_audioSource != null) return true;

            _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
                _audioSource = gameObject.AddComponent<AudioSource>();
            return _audioSource != null;
        }

        private void CaptureDefaults()
        {
            if (_audioSource == null) return;

            // Moving a camera or the listener along with the voice would be far worse than a 2D voice
            GameObject host = _audioSource.gameObject;
            _canMoveSource = !host.TryGetComponent(out Camera _) && !host.TryGetComponent(out AudioListener _);

            _defaultLocalPosition = _audioSource.transform.localPosition;
            _defaultSpatialBlend = _audioSource.spatialBlend;
            _defaultRolloff = _audioSource.rolloffMode;
            _defaultMinDistance = _audioSource.minDistance;
            _defaultMaxDistance = _audioSource.maxDistance;
            _defaultDopplerLevel = _audioSource.dopplerLevel;
            _defaultsCaptured = true;
        }

        private void ApplySpeakerSettings()
        {
            if (_audioSource == null || !_canMoveSource) return;

            float minDistance = Mathf.Max(0.01f, speakerMinDistance);
            _audioSource.spatialBlend = _speakerBlend;
            _audioSource.rolloffMode = speakerRolloff;
            _audioSource.minDistance = minDistance;
            _audioSource.maxDistance = Mathf.Max(minDistance + 0.01f, speakerMaxDistance);
            // The source jumps from NPC to NPC; doppler would bend the pitch on every jump
            _audioSource.dopplerLevel = 0f;
        }

        private void FollowSpeaker()
        {
            if (_audioSource == null || !_canMoveSource) return;

            Transform anchor = _speakerHead != null ? _speakerHead : _speaker;
            if (anchor != null)
                _audioSource.transform.position = anchor.position;
        }

        private void ClearSpeaker()
        {
            bool hadSpeaker = _hasSpeaker;
            _hasSpeaker = false;
            _speaker = null;
            _speakerHead = null;

            if (!hadSpeaker || !_defaultsCaptured || _audioSource == null) return;

            _audioSource.spatialBlend = _defaultSpatialBlend;
            _audioSource.rolloffMode = _defaultRolloff;
            _audioSource.minDistance = _defaultMinDistance;
            _audioSource.maxDistance = _defaultMaxDistance;
            _audioSource.dopplerLevel = _defaultDopplerLevel;
            if (_canMoveSource)
                _audioSource.transform.localPosition = _defaultLocalPosition;
        }
    }
}
