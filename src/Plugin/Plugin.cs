using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SilksongPS5Haptics
{
    [BepInPlugin("com.will.silksong.ps5haptics", "Silksong PS5 Haptics", "0.2.0")]
    public class PS5HapticsPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> KeepRumble;
        internal static ConfigEntry<float> MasterGain;
        internal static ConfigEntry<int> BridgePort;
        internal static ConfigEntry<string> ClipsPath;
        internal static ConfigEntry<bool> AutoStartBridge;
        internal static ConfigEntry<bool> SuspendInputWhenUnfocused;

        private void Awake()
        {
            Log = Logger;

            KeepRumble = Config.Bind("General", "KeepRumble", false,
                "Also send the normal dual-motor rumble while a PS5 haptic clip plays (may feel doubled through DSX).");
            MasterGain = Config.Bind("General", "MasterGain", 1.0f,
                "Overall gain applied to the haptic waveform (0.0 - 2.0).");
            BridgePort = Config.Bind("General", "BridgePort", 48111,
                "Localhost TCP port the HapticsBridge app listens on.");
            ClipsPath = Config.Bind("General", "ClipsPath", "",
                "Folder containing the extracted PS5 haptic WAVs. Empty = <plugin folder>\\clips.");

            AutoStartBridge = Config.Bind("General", "AutoStartBridge", true,
                "Launch HapticsBridge.exe (system tray) automatically when the game starts.");
            SuspendInputWhenUnfocused = Config.Bind("Multi-instance", "SuspendInputWhenUnfocused", false,
                "Ignore controller input while this game window is not focused. Enable on BOTH installs when running two instances (e.g. coop host+client) on one PC with one controller: whichever window you click gets the pad. Leave off for normal play or local two-player with two controllers.");

            string clips = ClipsPath.Value;
            if (string.IsNullOrEmpty(clips))
                clips = Path.Combine(Path.GetDirectoryName(Info.Location), "clips");

            if (AutoStartBridge.Value)
                TryStartBridge();

            HapticEngine.Start(clips, BridgePort.Value);
            new Harmony("com.will.silksong.ps5haptics").PatchAll(typeof(MixerPatch));
            Log.LogInfo($"PS5 haptics plugin loaded. Clips: {clips}");
        }

        // Unity main-thread state, cached each frame for the audio thread.
        private void Update()
        {
            try
            {
                HapticEngine.CachedTimeScale = Time.timeScale;
                HapticEngine.CachedStrengthMultiplier = VibrationManager.StrengthMultiplier;
                HapticEngine.CachedMasterGain = MasterGain.Value;

                // Re-assert every frame: the game's InControlManager writes its
                // own (false) value on setup, order-dependent with plugin load.
                if (SuspendInputWhenUnfocused.Value && InControl.InputManager.IsSetup)
                    InControl.InputManager.SuspendInBackground = true;
            }
            catch
            {
                // ConfigManager may not be ready during early boot; keep last values.
            }
        }

        private void OnDestroy()
        {
            HapticEngine.Shutdown();
        }

        // The bridge is single-instance (mutex), so launching when one is
        // already running is a harmless no-op.
        private void TryStartBridge()
        {
            try
            {
                string exe = Path.Combine(Path.GetDirectoryName(Info.Location), "HapticsBridge", "HapticsBridge.exe");
                if (!File.Exists(exe))
                {
                    // Legacy layout: bridge folder in the game root.
                    exe = Path.Combine(Paths.GameRootPath, "HapticsBridge", "HapticsBridge.exe");
                }
                if (File.Exists(exe))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(exe),
                    });
                    Log.LogInfo($"Launched bridge: {exe}");
                }
                else
                {
                    Log.LogWarning("HapticsBridge.exe not found; start it manually or set AutoStartBridge=false");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"Could not auto-start bridge: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(VibrationManager), nameof(VibrationManager.GetMixer))]
    internal static class MixerPatch
    {
        private static void Postfix(ref VibrationMixer __result)
        {
            if (__result == null || __result is HapticMixerWrapper)
                return;
            __result = HapticMixerWrapper.GetOrCreate(__result);
        }
    }
}
