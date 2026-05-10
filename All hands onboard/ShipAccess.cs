using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Helper for grabbing private/internal Ship fields via reflection.
    /// AccessTools caches FieldInfo so the per-call cost is ~one indirection.
    /// Centralised here so future Valheim renames need fixing in one spot.
    /// </summary>
    internal static class ShipAccess
    {
        private static readonly FieldInfo NviewField =
            AccessTools.Field(typeof(Ship), "m_nview");

        private static readonly FieldInfo FloatColliderField =
            AccessTools.Field(typeof(Ship), "m_floatCollider");

        private static readonly FieldInfo CurrentShipsField =
            AccessTools.Field(typeof(Ship), "s_currentShips");

        private static readonly FieldInfo ForceField =
            AccessTools.Field(typeof(Ship), "m_force");

        private static readonly FieldInfo BackwardForceField =
            AccessTools.Field(typeof(Ship), "m_backwardForce");

        private static readonly FieldInfo PlayerAttachPointField =
            AccessTools.Field(typeof(Player), "m_attachPoint");

        public static float GetForce(Ship ship) =>
            ship == null || ForceField == null ? 0f : (float)ForceField.GetValue(ship);

        public static void SetForce(Ship ship, float v) =>
            ForceField?.SetValue(ship, v);

        public static float GetBackwardForce(Ship ship) =>
            ship == null || BackwardForceField == null ? 0f : (float)BackwardForceField.GetValue(ship);

        public static void SetBackwardForce(Ship ship, float v) =>
            BackwardForceField?.SetValue(ship, v);

        public static ZNetView GetNView(Ship ship)
        {
            if (ship == null) return null;
            // GetComponent fallback in case the field is stripped in some version.
            var v = NviewField != null ? (ZNetView)NviewField.GetValue(ship) : null;
            return v != null ? v : ship.GetComponent<ZNetView>();
        }

        public static BoxCollider GetFloatCollider(Ship ship)
        {
            if (ship == null || FloatColliderField == null) return null;
            return (BoxCollider)FloatColliderField.GetValue(ship);
        }

        public static List<Ship> GetCurrentShips()
        {
            if (CurrentShipsField == null) return null;
            return (List<Ship>)CurrentShipsField.GetValue(null);
        }

        /// <summary>
        /// Whether the player is physically seated on something on THIS ship (chair/bench/attach point).
        /// Standing on the deck does not count - only taking a rower position.
        /// Checks IsAttached() (sit state) plus attachPoint in the ship.transform hierarchy.
        /// </summary>
        public static bool IsPlayerSeatedOnShip(Player p, Ship ship)
        {
            if (p == null || ship == null) return false;
            if (!p.IsAttached()) return false;

            Transform attachPoint = PlayerAttachPointField?.GetValue(p) as Transform;
            if (attachPoint == null) return false;

            Transform shipT = ship.transform;
            Transform t = attachPoint;
            // Walk up the parent chain - the attach point may be nested under a Chair/Bench/etc in the ship hierarchy.
            for (int i = 0; i < 16 && t != null; i++)
            {
                if (t == shipT) return true;
                t = t.parent;
            }

            return false;
        }

        public static void SelfTest()
        {
            Plugin.Log.LogDebug($"[ShipAccess] SelfTest:");
            Plugin.Log.LogDebug(
                $"  Ship.m_nview field        : {(NviewField != null ? "FOUND" : "NULL - reflection failed!")}");
            Plugin.Log.LogDebug($"  Ship.m_floatCollider field: {(FloatColliderField != null ? "FOUND" : "NULL")}");
            Plugin.Log.LogDebug(
                $"  Ship.s_currentShips field : {(CurrentShipsField != null ? "FOUND" : "NULL - HUD will fall back to FindObjectsOfType")}");
            Plugin.Log.LogDebug(
                $"  Player.m_attachPoint field: {(PlayerAttachPointField != null ? "FOUND" : "NULL - seated check will return false!")}");

            // Dump Ship fields and methods for diagnostics - names can change between game versions.
            var shipFields = AccessTools.GetDeclaredFields(typeof(Ship));
            Plugin.Log.LogDebug($"  Ship has {shipFields.Count} declared fields:");
            foreach (var f in shipFields)
            {
                Plugin.Log.LogDebug($"    {f.FieldType.Name} {f.Name}");
            }

            var shipMethods = AccessTools.GetDeclaredMethods(typeof(Ship));
            Plugin.Log.LogDebug(
                $"  Ship has {shipMethods.Count} declared methods (looking for Update/FixedUpdate variants):");
            foreach (var m in shipMethods)
            {
                string name = m.Name.ToLowerInvariant();
                if (name.Contains("update") || name.Contains("fixed") || name.Contains("tick"))
                {
                    var pars = string.Join(",",
                        System.Linq.Enumerable.Select(m.GetParameters(), p => p.ParameterType.Name));
                    Plugin.Log.LogDebug($"    {m.ReturnType.Name} {m.Name}({pars})");
                }
            }
        }
    }
}