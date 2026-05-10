using HarmonyLib;
using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Clears per-ship caches when a ship leaves the scene.
    /// Without this, Dictionary entries grow unbounded over long sessions
    /// (e.g. a player building and destroying many ships, or a long-running server).
    /// </summary>
    [HarmonyPatch(typeof(Ship), "OnDestroyed")]
    public static class ShipDestroyedPatch
    {
        private static void Prefix(Ship __instance)
        {
            if (__instance == null) return;

            ShipTypeConfig.Invalidate(__instance);
            ShipRowingManager.InvalidateCache(__instance);

            // Tear down the oars visual in case it survived Destroyed.
            var visual = __instance.GetComponent<ShipOarsVisual>();
            if (visual != null) Object.Destroy(visual);
        }
    }
}