using System.Collections.Generic;

namespace SilksongPS5Haptics
{
    /// <summary>
    /// Wraps the game's own VibrationMixer. Emissions that carry a PS5 haptic
    /// AudioClip are played as waveforms through the HapticEngine; everything
    /// else (and everything while the bridge is disconnected) falls through to
    /// the original dual-motor rumble mixer.
    /// </summary>
    public sealed class HapticMixerWrapper : VibrationMixer
    {
        private static readonly Dictionary<VibrationMixer, HapticMixerWrapper> cache =
            new Dictionary<VibrationMixer, HapticMixerWrapper>();

        private readonly VibrationMixer inner;
        private readonly List<PS5HapticEmission> emissions = new List<PS5HapticEmission>();
        private readonly object gate = new object();

        private HapticMixerWrapper(VibrationMixer inner)
        {
            this.inner = inner;
        }

        public static HapticMixerWrapper GetOrCreate(VibrationMixer inner)
        {
            lock (cache)
            {
                if (!cache.TryGetValue(inner, out var wrapper))
                {
                    wrapper = new HapticMixerWrapper(inner);
                    cache.Add(inner, wrapper);
                }
                return wrapper;
            }
        }

        public override bool IsPaused
        {
            get => inner.IsPaused;
            set
            {
                inner.IsPaused = value;
                HapticEngine.CachedMixerPaused = value;
            }
        }

        public override int PlayingEmissionCount
        {
            get
            {
                lock (gate) { Prune(); return emissions.Count + inner.PlayingEmissionCount; }
            }
        }

        public override VibrationEmission GetPlayingEmission(int index)
        {
            lock (gate)
            {
                Prune();
                if (index < emissions.Count)
                    return emissions[index];
                return inner.GetPlayingEmission(index - emissions.Count);
            }
        }

        private static int playsLogged;
        private static bool fallbackWarned;

        public override VibrationEmission PlayEmission(VibrationData vibrationData, VibrationTarget vibrationTarget, bool isLooping, string tag, bool isRealtime)
        {
            UnityEngine.AudioClip clip = vibrationData.GetPS5Vibration();
            if (clip != null && !HapticEngine.Connected && !fallbackWarned)
            {
                fallbackWarned = true;
                PS5HapticsPlugin.Log.LogWarning($"PS5 haptic event '{clip.name}' arrived but bridge is not connected - using rumble fallback (is HapticsBridge.exe running?)");
            }
            if (clip != null && HapticEngine.Connected && HapticEngine.TryGetPcm(clip.name, out var pcm))
            {
                if (playsLogged < 8)
                {
                    playsLogged++;
                    PS5HapticsPlugin.Log.LogInfo($"Haptic play: '{clip.name}' (loop={isLooping}, tag='{tag}')");
                }
                var emission = new PS5HapticEmission(pcm, vibrationData.Strength, isLooping, tag, vibrationTarget, isRealtime);
                lock (gate) { emissions.Add(emission); }
                HapticEngine.Register(emission);

                if (PS5HapticsPlugin.KeepRumble.Value)
                    inner.PlayEmission(vibrationData, vibrationTarget, isLooping, tag, isRealtime);
                return emission;
            }
            return inner.PlayEmission(vibrationData, vibrationTarget, isLooping, tag, isRealtime);
        }

        public override VibrationEmission PlayEmission(VibrationEmission emission)
        {
            if (emission is PS5HapticEmission ps5)
            {
                ps5.Play();
                lock (gate)
                {
                    if (!emissions.Contains(ps5))
                        emissions.Add(ps5);
                }
                HapticEngine.Register(ps5);
                return ps5;
            }
            return inner.PlayEmission(emission);
        }

        public override void StopAllEmissions()
        {
            lock (gate)
            {
                foreach (var e in emissions) e.Stop();
                emissions.Clear();
            }
            inner.StopAllEmissions();
        }

        public override void StopAllEmissionsWithTag(string tag)
        {
            lock (gate)
            {
                foreach (var e in emissions)
                    if (e.Tag == tag) e.Stop();
                Prune();
            }
            inner.StopAllEmissionsWithTag(tag);
        }

        private void Prune()
        {
            emissions.RemoveAll(e => !e.IsPlaying);
        }
    }
}
