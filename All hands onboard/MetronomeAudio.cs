using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Procedurally generated short "tick" - no asset dependencies.
    /// Sine wave with exponential decay. Two tones: higher for LEFT (downbeat accent),
    /// lower for RIGHT (offbeat). Clip is short enough (~80ms) not to mask other game audio.
    /// </summary>
    public static class MetronomeAudio
    {
        private static AudioClip _highClip;
        private static AudioClip _lowClip;

        public static AudioClip GetClip(bool high)
        {
            if (high && _highClip != null) return _highClip;
            if (!high && _lowClip != null) return _lowClip;

            const int sampleRate = 44100;
            const int length = sampleRate / 12; // ~83ms
            float freq = high ? 1320f : 880f;
            float[] samples = new float[length];
            for (int i = 0; i < length; i++)
            {
                float t = (float)i / sampleRate;
                float env = Mathf.Exp(-t * 35f);
                samples[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * env * 0.6f;
            }

            var clip = AudioClip.Create(high ? "MetronomeHigh" : "MetronomeLow",
                length, 1, sampleRate, false);
            clip.SetData(samples, 0);
            if (high) _highClip = clip;
            else _lowClip = clip;
            return clip;
        }

        public static void PlayTick(bool high)
        {
            if (!Plugin.MetronomeSound.Value) return;
            var clip = GetClip(high);
            if (clip == null) return;

            // 2D playback at the listener position - the metronome is a UI helper, not a world effect.
            var listener = Object.FindObjectOfType<AudioListener>();
            Vector3 pos = listener != null ? listener.transform.position : Vector3.zero;
            float vol = Mathf.Clamp01(Plugin.MetronomeVolume.Value);
            AudioSource.PlayClipAtPoint(clip, pos, vol);
        }
    }
}