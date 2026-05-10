using HarmonyLib;

namespace AllHandsOnboard
{
    /// <summary>
    /// Main mod control loop - per ship, per fixed tick. Does three things:
    ///   1. Holds the rower slot (claim/release based on being seated on the ship in Slow mode).
    ///   2. Ticks LocalRowingState (input -> tempo -> ZDO sync).
    ///   3. Scales the ship's m_force / m_backwardForce by the tempo multiplier, because in
    ///      Slow mode the oar drive is applied directly by Rigidbody.AddForce inside
    ///      CustomFixedUpdate (NOT through GetSailForce). The scaling must be short-lived
    ///      (Prefix swaps, Postfix restores) so other systems reading m_force still see vanilla values.
    /// </summary>
    [HarmonyPatch(typeof(Ship), "CustomFixedUpdate", new[] { typeof(float) })]
    public static class ShipFixedUpdatePatch
    {
        public class State
        {
            public float SavedForce;
            public float SavedBackwardForce;
            public bool Modified;
        }

        private static int _logCounter;

        private static void Prefix(Ship __instance, float fixedDeltaTime, out State __state)
        {
            __state = new State();

            Player local = Player.m_localPlayer;
            if (local == null || ZNet.instance == null) return;

            bool onThisShip = __instance.IsPlayerInBoat(local);
            bool seated = ShipAccess.IsPlayerSeatedOnShip(local, __instance);
            bool rowingMode = __instance.GetSpeedSetting() == Ship.Speed.Slow;

            if (++_logCounter % 50 == 0)
            {
                Plugin.DebugLog(
                    $"[ShipTick] '{__instance.gameObject.name}' onShip={onThisShip}, seated={seated}, rowingMode={rowingMode}, mySlot={LocalRowingState.CurrentSlot}");
            }

            // Scale the rowing force for every ship in Slow mode, regardless of whether the
            // local player is aboard - someone else may be the one rowing.
            if (rowingMode)
            {
                // Tick the smoothing once per fixed update per ship; everywhere else
                // (GetSailForce, HUD) just reads the smoothed value.
                ShipRowingManager.TickSmoothing(__instance, fixedDeltaTime);
                float mult = ShipRowingManager.GetSmoothedMultiplier(__instance);
                __state.SavedForce = ShipAccess.GetForce(__instance);
                __state.SavedBackwardForce = ShipAccess.GetBackwardForce(__instance);
                __state.Modified = true;
                ShipAccess.SetForce(__instance, __state.SavedForce * mult);
                ShipAccess.SetBackwardForce(__instance, __state.SavedBackwardForce * mult);

                if (_logCounter % 50 == 0)
                {
                    float raw = ShipRowingManager.GetTotalSpeedMultiplier(__instance);
                    Plugin.DebugLog(
                        $"[ShipTick] Force scaled: {__state.SavedForce:F1} -> {__state.SavedForce * mult:F1} (smoothed={mult:F2}, raw={raw:F2})");
                }
            }

            // Slot management runs only when the player is physically SEATED on the ship in Slow mode.
            // Standing on the deck isn't enough - they must occupy a stool/bench (Player.IsAttached + attachPoint).
            if (!onThisShip || !rowingMode || !seated)
            {
                if (LocalRowingState.CurrentSlot != -1 && LocalRowingState.CurrentShip == __instance)
                {
                    Plugin.Log.LogDebug(
                        $"[FixedUpdate] Releasing slot {LocalRowingState.CurrentSlot} on '{__instance.gameObject.name}' (onShip={onThisShip}, seated={seated}, rowing={rowingMode})");
                    ShipRowingManager.ReleaseSlot(__instance, LocalRowingState.CurrentSlot, local.GetPlayerID());
                    LocalRowingState.SetSlot(-1, null);
                }

                return;
            }

            if (LocalRowingState.CurrentSlot == -1)
            {
                int slot = ShipRowingManager.ClaimSlot(__instance, local.GetPlayerID());
                Plugin.Log.LogDebug(
                    $"[FixedUpdate] Player {local.GetPlayerID()} on '{__instance.gameObject.name}' in Slow mode -> ClaimSlot returned {slot}");
                LocalRowingState.SetSlot(slot, __instance);
            }

            LocalRowingState.Tick(fixedDeltaTime, __instance);
        }

        private static void Postfix(Ship __instance, State __state)
        {
            if (__state == null || !__state.Modified) return;
            // Restore originals so other systems (GUI, network sync) still see vanilla values.
            ShipAccess.SetForce(__instance, __state.SavedForce);
            ShipAccess.SetBackwardForce(__instance, __state.SavedBackwardForce);
        }
    }
}