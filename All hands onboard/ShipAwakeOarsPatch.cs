using HarmonyLib;

namespace AllHandsOnboard
{
    /// <summary>
    /// Attaches ShipOarsVisual to a ship on creation.
    /// Low priority so it runs after ShipAwakeRpcPatch (RPCs first).
    /// </summary>
    [HarmonyPatch(typeof(Ship), "Awake")]
    [HarmonyPriority(Priority.Low)]
    public static class ShipAwakeOarsPatch
    {
        private static void Postfix(Ship __instance)
        {
            if (__instance == null) return;
            if (__instance.GetComponent<ShipOarsVisual>() != null) return;

            var visual = __instance.gameObject.AddComponent<ShipOarsVisual>();
            visual.Init(__instance);
        }
    }
}