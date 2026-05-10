using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Procedurally generated short "splash" - no external assets.
    /// White noise with an exponential decay envelope, lightly low-passed.
    /// </summary>
    public static class SplashAudio
    {
        private static AudioClip _splashClip;

        public static AudioClip GetClip()
        {
            if (_splashClip != null) return _splashClip;

            const int sampleRate = 44100;
            const int length = sampleRate / 4; // 0.25s
            float[] samples = new float[length];
            var rng = new System.Random(42);

            for (int i = 0; i < length; i++)
            {
                float t = (float)i / length;
                float env = Mathf.Exp(-t * 8f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                samples[i] = noise * env * 0.4f;
            }

            // Cheap low-pass: average of current and previous sample.
            for (int i = 1; i < length; i++)
            {
                samples[i] = (samples[i] + samples[i - 1]) * 0.5f;
            }

            _splashClip = AudioClip.Create("RowingSplash", length, 1, sampleRate, false);
            _splashClip.SetData(samples, 0);
            return _splashClip;
        }

        public static void PlayAt(Vector3 position, float volume = 0.3f)
        {
            var clip = GetClip();
            if (clip == null) return;
            AudioSource.PlayClipAtPoint(clip, position, volume);
        }
    }
}