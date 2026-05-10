using HarmonyLib;
using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Scales ship push force while in rowing (Slow) mode.
    /// </summary>
    [HarmonyPatch(typeof(Ship), "GetSailForce")]
    public static class GetSailForcePatch
    {
        private static int _logCounter;

        private static void Postfix(Ship __instance, ref Vector3 __result)
        {
            if (__instance.GetSpeedSetting() != Ship.Speed.Slow) return;

            // Smoothed value is ticked in ShipFixedUpdatePatch; here we only read it.
            float multiplier = ShipRowingManager.GetSmoothedMultiplier(__instance);
            Vector3 before = __result;
            __result *= multiplier;

            if (++_logCounter % 60 == 0)
            {
                Plugin.DebugLog(
                    $"[GetSailForce] Slow mode on '{__instance.gameObject.name}', mult={multiplier:F2}, force={before.magnitude:F1}->{__result.magnitude:F1}");
            }
        }
    }
}