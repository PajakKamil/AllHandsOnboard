using HarmonyLib;
using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Registers RPC handlers on a ship's ZNetView at init time.
    /// Only the ZDO owner actually mutates state; other clients receive the RPC and ignore it.
    /// Owner-side validation: a player can only update the slot they currently occupy.
    /// </summary>
    [HarmonyPatch(typeof(Ship), "Awake")]
    public static class ShipAwakeRpcPatch
    {
        private static void Postfix(Ship __instance)
        {
            if (__instance == null) return;
            var nview = ShipAccess.GetNView(__instance);
            if (nview == null)
            {
                Plugin.Log.LogWarning(
                    $"[ShipAwakeRpc] Ship '{__instance.gameObject.name}' has NO ZNetView - RPC not registered!");
                return;
            }

            Plugin.Log.LogDebug(
                $"[ShipAwakeRpc] Registering RPCs on '{__instance.gameObject.name}' (instance {__instance.GetInstanceID()})");

            nview.Register<int, float, long, double>(
                "Rowing_UpdateSlot",
                (sender, slot, tempo, playerId, time) =>
                    OnUpdateSlot(__instance, slot, tempo, playerId, time));

            // Separate RPC just for the stroke event - emitted on actual key press.
            // Kept separate because ZNetView.Register tops out at 4 generic args.
            // Streak rides along (only changes on strokes, so per-stroke sync suffices).
            nview.Register<int, long, double, int>(
                "Rowing_StrokeAt",
                (sender, slot, playerId, atTime, streak) =>
                    OnStrokeAt(__instance, slot, playerId, atTime, streak));

            nview.Register<int, long, double>(
                "Rowing_ClaimSlot",
                (sender, slot, playerId, time) =>
                    OnClaimSlot(__instance, slot, playerId, time));

            nview.Register<int, long>(
                "Rowing_ReleaseSlot",
                (sender, slot, playerId) =>
                    OnReleaseSlot(__instance, slot, playerId));
        }

        private static void OnUpdateSlot(Ship ship,
            int slot, float tempo, long playerId, double time)
        {
            var nview = ShipAccess.GetNView(ship);
            if (nview == null || !nview.IsOwner()) return;
            if (slot < 0 || slot >= Plugin.MaxRowers) return;

            ZDO zdo = nview.GetZDO();
            long currentOwner = zdo.GetLong(RowingZdoKeys.SlotPlayer[slot], 0L);
            if (currentOwner != playerId) return;

            zdo.Set(RowingZdoKeys.SlotTempo[slot], Mathf.Clamp01(tempo));
            zdo.Set(RowingZdoKeys.SlotLastUpdate[slot], (float)time);
        }

        private static void OnStrokeAt(Ship ship, int slot, long playerId, double atTime, int streak)
        {
            var nview = ShipAccess.GetNView(ship);
            if (nview == null || !nview.IsOwner()) return;
            if (slot < 0 || slot >= Plugin.MaxRowers) return;

            ZDO zdo = nview.GetZDO();
            long currentOwner = zdo.GetLong(RowingZdoKeys.SlotPlayer[slot], 0L);
            if (currentOwner != playerId) return;

            zdo.Set(RowingZdoKeys.SlotLastStroke[slot], (float)atTime);
            zdo.Set(RowingZdoKeys.SlotStreak[slot], streak);
        }

        private static void OnClaimSlot(Ship ship,
            int slot, long playerId, double time)
        {
            var nview = ShipAccess.GetNView(ship);
            if (nview == null || !nview.IsOwner())
            {
                Plugin.DebugLog($"[RPC ClaimSlot] Ignored - not owner of '{ship.gameObject.name}'");
                return;
            }

            if (slot < 0 || slot >= Plugin.MaxRowers) return;

            ZDO zdo = nview.GetZDO();
            double now = ZNet.instance.GetTimeSeconds();
            float timeout = Plugin.SlotTimeoutSeconds.Value;

            long current = zdo.GetLong(RowingZdoKeys.SlotPlayer[slot], 0L);
            float lastUpdate = zdo.GetFloat(RowingZdoKeys.SlotLastUpdate[slot], 0f);

            if (current == 0L || current == playerId || (now - lastUpdate) > timeout)
            {
                Plugin.Log.LogDebug(
                    $"[RPC ClaimSlot] Owner accepting: slot {slot} -> player {playerId} on '{ship.gameObject.name}'");
                zdo.Set(RowingZdoKeys.SlotPlayer[slot], playerId);
                zdo.Set(RowingZdoKeys.SlotTempo[slot], 0f);
                zdo.Set(RowingZdoKeys.SlotLastUpdate[slot], (float)now);
            }
            else
            {
                Plugin.Log.LogDebug(
                    $"[RPC ClaimSlot] REJECTED: slot {slot} held by {current}, age={now - lastUpdate:F1}s");
            }
        }

        private static void OnReleaseSlot(Ship ship, int slot, long playerId)
        {
            var nview = ShipAccess.GetNView(ship);
            if (nview == null || !nview.IsOwner()) return;
            if (slot < 0 || slot >= Plugin.MaxRowers) return;

            ZDO zdo = nview.GetZDO();
            long current = zdo.GetLong(RowingZdoKeys.SlotPlayer[slot], 0L);

            if (current == playerId)
            {
                zdo.Set(RowingZdoKeys.SlotPlayer[slot], 0L);
                zdo.Set(RowingZdoKeys.SlotTempo[slot], 0f);
            }
        }
    }
}