using System;
using UnityEngine;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Plays TTS audio from raw PCM bytes (16-bit signed LE, 24kHz, mono).
    /// Attach to a GameObject with an AudioSource, or one will be auto-created.
    /// </summary>
    public class TTSAudioPlayer : MonoBehaviour
    {
        [SerializeField] private AudioSource _audioSource;

        private const int SampleRate = 24000;
        private const int Channels = 1;

        private void Awake()
        {
            if (_audioSource == null)
                _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
                _audioSource = gameObject.AddComponent<AudioSource>();
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
        /// </summary>
        public void PlayPCM(byte[] pcmBytes)
        {
            if (pcmBytes == null || pcmBytes.Length == 0) return;

            Stop();

            var clip = PCMToAudioClip(pcmBytes, "npc_tts");
            if (clip != null)
            {
                _audioSource.clip = clip;
                _audioSource.Play();
                Debug.Log($"[TTSAudioPlayer] Playing {clip.length:F1}s of TTS audio ({pcmBytes.Length} bytes)");
            }
        }

        /// <summary>
        /// Stop playback and clean up the current clip to free memory.
        /// </summary>
        public void Stop()
        {
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
        /// Whether TTS audio is currently playing.
        /// </summary>
        public bool IsPlaying => _audioSource != null && _audioSource.isPlaying;
    }
}
