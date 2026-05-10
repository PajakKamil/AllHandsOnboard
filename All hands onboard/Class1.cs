using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AllHandsOnboard
{
    [BepInPlugin(ModId, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        private const string ModId = "com.allhands.onboard";
        private const string ModName = "All hands onboard";
        private const string ModVersion = "1.0.0";

        public const int MaxRowers = 6;

        public static ManualLogSource Log;

        // Rowing core (speed, decay, sync)
        public static ConfigEntry<float> MaxMultiplierPerRower;
        public static ConfigEntry<float> MinMultiplier;
        public static ConfigEntry<float> CapRatio;
        public static ConfigEntry<float> DecayPerSecond;
        public static ConfigEntry<float> BoostPerStroke;
        public static ConfigEntry<float> SyncIntervalSeconds;
        public static ConfigEntry<float> SlotTimeoutSeconds;
        public static ConfigEntry<float> TargetStrokeInterval;
        public static ConfigEntry<float> SmoothingRate;

        // Rhythm windows (ratio = gap/target; 1.0 = perfect timing)
        public static ConfigEntry<float> TooFastRatio;
        public static ConfigEntry<float> PerfectMinRatio;
        public static ConfigEntry<float> PerfectMaxRatio;
        public static ConfigEntry<float> SlowRatio;

        // Bonus per verdict (multiplier on BoostPerStroke)
        public static ConfigEntry<float> PerfectBonusMul;
        public static ConfigEntry<float> GoodBonusMul;
        public static ConfigEntry<float> SlowBonusMul;
        public static ConfigEntry<float> TooFastPenaltyMul;

        // Streak (PERFECT/GOOD chain bonus)
        public static ConfigEntry<int> StreakMinForBonus;
        public static ConfigEntry<float> StreakBonusMul;

        // Mastery (long perfect streak -> ship-wide speed)
        public static ConfigEntry<int> MasteryStreakMin;
        public static ConfigEntry<int> MasteryStreakMax;
        public static ConfigEntry<float> MasteryFactorAtMin;
        public static ConfigEntry<float> MasteryFactorAtMax;

        // Crew sync (crew rowing in phase)
        public static ConfigEntry<float> CrewSyncWindowRatio;
        public static ConfigEntry<float> CrewSync2;
        public static ConfigEntry<float> CrewSync3;
        public static ConfigEntry<float> CrewSync4Plus;

        // Wrong-side handling (same key twice in a row - always penalty)
        public static ConfigEntry<float> WrongSidePenaltyMul;

        public static ConfigEntry<bool> ShowHud;
        public static ConfigEntry<bool> ShowMetronome;
        public static ConfigEntry<bool> MetronomeSound;
        public static ConfigEntry<float> MetronomeVolume;

        public static ConfigEntry<bool> VerboseLogs;

        public static ConfigEntry<RowingInput.InputMode> InputModePref;
        public static ConfigEntry<KeyCode> LeftKey;
        public static ConfigEntry<KeyCode> RightKey;
        public static ConfigEntry<string> LeftPadAxis;
        public static ConfigEntry<string> RightPadAxis;

        private readonly Harmony _harmony = new Harmony(ModId);

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"=== {ModName} v{ModVersion} starting ===");

            MaxMultiplierPerRower = Config.Bind("Rowing", "MaxMultiplierPerRower", 1.0f,
                "Max speed multiplier each rower can contribute (at tempo=1.0)");
            MinMultiplier = Config.Bind("Rowing", "MinMultiplier", 0.3f,
                "Base ship speed multiplier even with no rowers (vanilla = 1.0)");
            CapRatio = Config.Bind("Rowing", "CapRatio", 0.85f,
                "Fraction of theoretical max sum that the speed bonus can reach");
            DecayPerSecond = Config.Bind("Rowing", "DecayPerSecond", 0.6f,
                "How fast tempo bleeds when not rowing (per second, after grace window)");
            BoostPerStroke = Config.Bind("Rowing", "BoostPerStroke", 0.25f,
                "Base tempo boost per stroke (multiplied by verdict bonus and streak)");
            SyncIntervalSeconds = Config.Bind("Rowing", "SyncIntervalSeconds", 0.2f,
                "How often local tempo is replicated to other clients via ZDO");
            SlotTimeoutSeconds = Config.Bind("Rowing", "SlotTimeoutSeconds", 3.0f,
                "After this many seconds with no sync, slot is considered free");
            TargetStrokeInterval = Config.Bind("Rowing", "TargetStrokeInterval", 0.4f,
                "Target time between L/R strokes (sec). PERFECT window centered on this. Metronome alternates L/R at this rate.");
            SmoothingRate = Config.Bind("Rowing", "SmoothingRate", 5.0f,
                "Speed multiplier smoothing rate (anti-bouncing). Higher = faster response, lower = smoother. ~63%% of target reached in 1/rate seconds.");

            TooFastRatio = Config.Bind("Rhythm", "TooFastRatio", 0.4f,
                "Strokes faster than this ratio of target = TOO FAST (spam, penalty)");
            PerfectMinRatio = Config.Bind("Rhythm", "PerfectMinRatio", 0.84f,
                "Lower bound of PERFECT window (ratio of target). 0.84 + 1.16 = ~125ms wide PERFECT at 0.4s target.");
            PerfectMaxRatio = Config.Bind("Rhythm", "PerfectMaxRatio", 1.16f,
                "Upper bound of PERFECT window (ratio of target).");
            SlowRatio = Config.Bind("Rhythm", "SlowRatio", 1.6f,
                "Strokes slower than this ratio of target = SLOW (no bonus, streak reset). Between PerfectMax and this = GOOD.");

            PerfectBonusMul = Config.Bind("Rhythm", "PerfectBonusMul", 1.8f,
                "Multiplier on BoostPerStroke for PERFECT verdict");
            GoodBonusMul = Config.Bind("Rhythm", "GoodBonusMul", 1.4f, "Multiplier on BoostPerStroke for GOOD verdict");
            SlowBonusMul = Config.Bind("Rhythm", "SlowBonusMul", 1.0f,
                "Multiplier on BoostPerStroke for SLOW verdict (no penalty, no reward)");
            TooFastPenaltyMul = Config.Bind("Rhythm", "TooFastPenaltyMul", 0.3f,
                "Multiplier on BoostPerStroke for TOO FAST verdict (penalty)");

            StreakMinForBonus = Config.Bind("Streak", "StreakMinForBonus", 3,
                "Min streak count to start getting per-stroke streak bonus");
            StreakBonusMul = Config.Bind("Streak", "StreakBonusMul", 1.5f,
                "Extra multiplier on stroke boost when streak >= MinForBonus");

            MasteryStreakMin = Config.Bind("Streak", "MasteryStreakMin", 20,
                "Streak required to start applying mastery factor to ship speed");
            MasteryStreakMax = Config.Bind("Streak", "MasteryStreakMax", 50,
                "Streak at which mastery factor reaches maximum (plateau above)");
            MasteryFactorAtMin = Config.Bind("Streak", "MasteryFactorAtMin", 1.15f,
                "Ship speed multiplier when highest streak == MasteryStreakMin (1.0 = no bonus)");
            MasteryFactorAtMax = Config.Bind("Streak", "MasteryFactorAtMax", 1.30f,
                "Ship speed multiplier when highest streak >= MasteryStreakMax");

            CrewSyncWindowRatio = Config.Bind("CrewSync", "WindowRatio", 0.4f,
                "How tight (relative to target interval) other rowers' strokes must cluster to count as 'in sync'");
            CrewSync2 = Config.Bind("CrewSync", "BonusFor2", 1.15f, "Speed multiplier when 2 rowers are in sync");
            CrewSync3 = Config.Bind("CrewSync", "BonusFor3", 1.30f, "Speed multiplier when 3 rowers are in sync");
            CrewSync4Plus = Config.Bind("CrewSync", "BonusFor4Plus", 1.45f,
                "Speed multiplier when 4+ rowers are in sync");

            WrongSidePenaltyMul = Config.Bind("Rhythm", "WrongSidePenaltyMul", 0.5f,
                "Multiplier on BoostPerStroke subtracted from tempo when same key pressed twice in a row (WRONG SIDE). Always applied - rowing requires alternation.");

            ShowHud = Config.Bind("Hud", "ShowHud", true, "Show the rowing HUD widget");
            ShowMetronome = Config.Bind("Hud", "ShowMetronome", true,
                "Show L/R indicator boxes (flash on player strokes)");
            MetronomeSound = Config.Bind("Hud", "MetronomeSound", true, "Play short tick sound on each metronome beat");
            MetronomeVolume = Config.Bind("Hud", "MetronomeVolume", 0.25f, "Volume of metronome tick (0.0-1.0)");
            VerboseLogs = Config.Bind("Debug", "VerboseLogs", false,
                "Log per-frame events (input, ticks). Spammy - turn off when not debugging.");

            InputModePref = Config.Bind("Input", "Mode", RowingInput.InputMode.Auto, "Input mode");
            LeftKey = Config.Bind("Input", "LeftKey", KeyCode.LeftArrow, "Keyboard: left oar");
            RightKey = Config.Bind("Input", "RightKey", KeyCode.RightArrow, "Keyboard: right oar");
            LeftPadAxis = Config.Bind("Input", "LeftPadAxis", "JoyAxis 9", "Pad: LT axis");
            RightPadAxis = Config.Bind("Input", "RightPadAxis", "JoyAxis 10", "Pad: RT axis");

            Log.LogInfo($"Configs bound. LeftKey={LeftKey.Value}, RightKey={RightKey.Value}, MaxRowers={MaxRowers}");

            _harmony.PatchAll();
            int patchCount = 0;
            foreach (var m in _harmony.GetPatchedMethods()) patchCount++;
            Log.LogInfo($"Harmony PatchAll done - {patchCount} method(s) patched");

            ShipAccess.SelfTest();

            Log.LogInfo($"=== {ModName} ready ===");
        }

        public static void DebugLog(string msg)
        {
            if (VerboseLogs != null && VerboseLogs.Value && Log != null)
                Log.LogDebug(msg);
        }

        private void OnDestroy()
        {
            // ScriptEngine reload may invoke OnDestroy even when Awake didn't run
            // (e.g. assembly load failure), so everything must be null-safe.
            Log?.LogInfo("Mod cleanup starting");

            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (System.Exception e)
            {
                Log?.LogWarning($"UnpatchSelf failed: {e.Message}");
            }

            try
            {
                foreach (var oars in UnityEngine.Object.FindObjectsOfType<ShipOarsVisual>())
                {
                    if (oars != null) UnityEngine.Object.Destroy(oars);
                }
            }
            catch (System.Exception e)
            {
                Log?.LogWarning($"Cleanup oars failed: {e.Message}");
            }

            try
            {
                var hudRoot = GameObject.Find("RowingTempoHud");
                if (hudRoot != null) UnityEngine.Object.Destroy(hudRoot);
            }
            catch (System.Exception e)
            {
                Log?.LogWarning($"Cleanup HUD failed: {e.Message}");
            }

            try
            {
                ShipTypeConfig.ClearAll();
            }
            catch
            {
            }

            try
            {
                ShipRowingManager.ClearAllCache();
            }
            catch
            {
            }

            Log?.LogInfo("Mod cleanup complete");
        }
    }
}