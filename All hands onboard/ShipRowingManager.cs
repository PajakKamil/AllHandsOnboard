using System.Collections.Generic;
using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Manages rower slots on a ship - ZDO writes go via RPC to the owner; reads come straight
    /// from the ZDO (replicated to all clients).
    /// Speed cap scales with the rower count for the ship type.
    /// </summary>
    public static class ShipRowingManager
    {
        public class SlotData
        {
            public long PlayerId;
            public float Tempo;

            public double LastUpdate;

            // ZNet time of the last stroke - used for crew phase sync detection.
            public double LastStrokeAt;

            // Current streak of the player in this slot (>=20 grants the mastery speed bonus).
            public int Streak;
        }

        private static readonly Dictionary<int, SlotData[]> _cachedSlots
            = new Dictionary<int, SlotData[]>();

        // Smoothed multiplier per ship - lerps over time to avoid speed bouncing when tempo
        // jumps (e.g. ZDO replication latency, missing strokes during the grace window).
        private static readonly Dictionary<int, float> _smoothedMultiplier
            = new Dictionary<int, float>();

        public static int GetActiveSlotCount(Ship ship)
        {
            return ShipTypeConfig.GetFor(ship).RowerCount;
        }

        public static SlotData[] GetSlots(Ship ship)
        {
            if (ship == null) return EmptySlots();
            var nview = ShipAccess.GetNView(ship);
            if (nview == null || !nview.IsValid()) return EmptySlots();

            ZDO zdo = nview.GetZDO();
            int id = ship.GetInstanceID();

            if (!_cachedSlots.TryGetValue(id, out var slots))
            {
                slots = EmptySlots();
                _cachedSlots[id] = slots;
            }

            double now = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
            float timeout = Plugin.SlotTimeoutSeconds.Value;
            int activeCount = GetActiveSlotCount(ship);

            for (int i = 0; i < Plugin.MaxRowers; i++)
            {
                if (i >= activeCount)
                {
                    slots[i].PlayerId = 0L;
                    slots[i].Tempo = 0f;
                    slots[i].LastUpdate = 0;
                    slots[i].LastStrokeAt = 0;
                    slots[i].Streak = 0;
                    continue;
                }

                long pid = zdo.GetLong(RowingZdoKeys.SlotPlayer[i], 0L);
                float tempo = zdo.GetFloat(RowingZdoKeys.SlotTempo[i], 0f);
                float lastUpdate = zdo.GetFloat(RowingZdoKeys.SlotLastUpdate[i], 0f);
                float lastStrokeAt = zdo.GetFloat(RowingZdoKeys.SlotLastStroke[i], 0f);
                int streak = zdo.GetInt(RowingZdoKeys.SlotStreak[i], 0);

                if (pid == 0L || (now - lastUpdate) > timeout)
                {
                    slots[i].PlayerId = 0L;
                    slots[i].Tempo = 0f;
                    slots[i].LastUpdate = 0;
                    slots[i].LastStrokeAt = 0;
                    slots[i].Streak = 0;
                }
                else
                {
                    slots[i].PlayerId = pid;
                    slots[i].Tempo = tempo;
                    slots[i].LastUpdate = lastUpdate;
                    slots[i].LastStrokeAt = lastStrokeAt;
                    slots[i].Streak = streak;
                }
            }

            return slots;
        }

        public static float GetTotalSpeedMultiplier(Ship ship)
        {
            int active = GetActiveSlotCount(ship);
            if (active == 0) return 1f;

            var slots = GetSlots(ship);
            float sum = Plugin.MinMultiplier.Value;

            for (int i = 0; i < active; i++)
            {
                if (slots[i].PlayerId != 0L)
                    sum += slots[i].Tempo * Plugin.MaxMultiplierPerRower.Value;
            }

            float dynamicCap = Plugin.MinMultiplier.Value
                               + active * Plugin.MaxMultiplierPerRower.Value * Plugin.CapRatio.Value;
            float baseTotal = Mathf.Min(sum, dynamicCap);

            // Crew phase sync bonus + mastery bonus - both apply off-cap.
            // Crew sync: rowing in phase -> push.
            // Mastery: a long perfect streak -> disciplined rowing beats spam.
            return baseTotal * GetCrewSyncFactor(ship) * GetMasteryFactor(ship);
        }

        /// <summary>
        /// Mastery bonus - scales with the highest streak among active rowers.
        /// Streak < 20  -> 1.0x  (no bonus)
        /// Streak = 20  -> 1.15x (+15%)
        /// Streak = 50  -> 1.30x (+30%)
        /// Streak > 50  -> 1.30x (plateau)
        /// Linear interpolation across 20-50. We take the MAX (not the mean) across slots so a
        /// single disciplined player rewards the whole crew, encouraging a "rhythm leader" role.
        /// Only kicks in at streak >= 20, so 5-10 rhythmic strokes still yield zero bonus - this
        /// guards against rewarding incidental rhythm during spam-tapping.
        /// </summary>
        public static float GetMasteryFactor(Ship ship)
        {
            if (ship == null) return 1f;
            var slots = GetSlots(ship);
            int active = GetActiveSlotCount(ship);
            double now = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
            // The streak ZDO is only updated on a stroke event, so if the player stopped rowing
            // for more than ~2.4s by default, we treat the streak as expired here. The local
            // copy already zeroes out, but this guards against a stale ZDO value seen by others.
            float staleAfter = Plugin.TargetStrokeInterval.Value * 6f;
            int maxStreak = 0;
            for (int i = 0; i < active; i++)
            {
                if (slots[i].PlayerId == 0L) continue;
                if (slots[i].LastStrokeAt <= 0) continue;
                if (now - slots[i].LastStrokeAt > staleAfter) continue;
                if (slots[i].Streak > maxStreak) maxStreak = slots[i].Streak;
            }

            int sMin = Plugin.MasteryStreakMin.Value;
            int sMax = Plugin.MasteryStreakMax.Value;
            float fMin = Plugin.MasteryFactorAtMin.Value;
            float fMax = Plugin.MasteryFactorAtMax.Value;
            if (maxStreak < sMin) return 1f;
            if (maxStreak >= sMax) return fMax;
            float t = (float)(maxStreak - sMin) / Mathf.Max(1, sMax - sMin);
            return Mathf.Lerp(fMin, fMax, t);
        }

        /// <summary>
        /// Detects clusters of strokes on the shared ZNet timeline.
        /// The largest stroke cluster fitting within 2*syncWindow grants a ship-wide multiplier:
        ///   1 (or solo)         -> 1.0  (no bonus)
        ///   2 in sync           -> 1.15
        ///   3                   -> 1.30
        ///   4+                  -> 1.45
        /// Strokes older than 1s are ignored as stale.
        /// </summary>
        public static float GetCrewSyncFactor(Ship ship)
        {
            if (ship == null) return 1f;
            int active = GetActiveSlotCount(ship);
            if (active < 2) return 1f;

            var slots = GetSlots(ship);
            double now = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
            float syncWindow = Plugin.TargetStrokeInterval.Value * Plugin.CrewSyncWindowRatio.Value;

            var times = new List<double>(active);
            for (int i = 0; i < active; i++)
            {
                if (slots[i].PlayerId == 0L) continue;
                if (slots[i].LastStrokeAt <= 0) continue;
                if (now - slots[i].LastStrokeAt > 1.0) continue;
                times.Add(slots[i].LastStrokeAt);
            }

            if (times.Count < 2) return 1f;

            times.Sort();
            int best = 1;
            for (int i = 0; i < times.Count; i++)
            {
                int count = 1;
                for (int j = i + 1; j < times.Count; j++)
                {
                    if (times[j] - times[i] <= syncWindow * 2.0) count++;
                    else break;
                }

                if (count > best) best = count;
            }

            if (best >= 4) return Plugin.CrewSync4Plus.Value;
            if (best == 3) return Plugin.CrewSync3.Value;
            if (best == 2) return Plugin.CrewSync2.Value;
            return 1f;
        }

        /// <summary>
        /// Called ONCE per FixedUpdate per ship - lerps the internal smoothed multiplier
        /// toward the current GetTotalSpeedMultiplier. Smoothing makes the applied force
        /// change gracefully instead of jumping (anti-bouncing).
        /// </summary>
        public static void TickSmoothing(Ship ship, float dt)
        {
            if (ship == null) return;
            int id = ship.GetInstanceID();
            float target = GetTotalSpeedMultiplier(ship);
            if (!_smoothedMultiplier.TryGetValue(id, out var current))
            {
                _smoothedMultiplier[id] = target;
                return;
            }

            // Lerp: ~63% in 1/rate seconds. Configurable via Plugin.SmoothingRate.
            // Default rate=5 -> 63% in 0.2s, 95% in 0.6s - balanced between fast response and
            // no bouncing.
            float lerp = 1f - Mathf.Exp(-dt * Plugin.SmoothingRate.Value);
            _smoothedMultiplier[id] = Mathf.Lerp(current, target, lerp);
        }

        /// <summary>
        /// Read-only - returns the most recent smoothed value. Used by every site that applies
        /// force or displays the multiplier. Only ShipFixedUpdatePatch ever ticks the smoothing.
        /// </summary>
        public static float GetSmoothedMultiplier(Ship ship)
        {
            if (ship == null) return 1f;
            int id = ship.GetInstanceID();
            if (_smoothedMultiplier.TryGetValue(id, out var v)) return v;
            // No cached value - compute once, store, and return.
            float t = GetTotalSpeedMultiplier(ship);
            _smoothedMultiplier[id] = t;
            return t;
        }

        public static int ClaimSlot(Ship ship, long playerId)
        {
            if (ship == null) return -1;
            var nview = ShipAccess.GetNView(ship);
            if (nview == null || !nview.IsValid()) return -1;

            int max = GetActiveSlotCount(ship);
            if (max == 0) return -1;

            ZDO zdo = nview.GetZDO();
            double now = ZNet.instance.GetTimeSeconds();
            float timeout = Plugin.SlotTimeoutSeconds.Value;

            for (int i = 0; i < max; i++)
            {
                if (zdo.GetLong(RowingZdoKeys.SlotPlayer[i], 0L) == playerId)
                    return i;
            }

            for (int i = 0; i < max; i++)
            {
                long pid = zdo.GetLong(RowingZdoKeys.SlotPlayer[i], 0L);
                float lastUpdate = zdo.GetFloat(RowingZdoKeys.SlotLastUpdate[i], 0f);

                if (pid == 0L || (now - lastUpdate) > timeout)
                {
                    Plugin.Log.LogDebug(
                        $"[ClaimSlot] Player {playerId} claiming slot {i} on '{ship.gameObject.name}' (was pid={pid}, age={now - lastUpdate:F1}s)");
                    nview.InvokeRPC(ZNetView.Everybody, "Rowing_ClaimSlot",
                        i, playerId, now);
                    return i;
                }
            }

            Plugin.Log.LogWarning(
                $"[ClaimSlot] Player {playerId} - no free slot on '{ship.gameObject.name}' (max={max})");
            return -1;
        }

        public static void ReleaseSlot(Ship ship, int slot, long playerId)
        {
            if (ship == null) return;
            if (slot < 0 || slot >= Plugin.MaxRowers) return;
            var nview = ShipAccess.GetNView(ship);
            if (nview == null || !nview.IsValid()) return;

            nview.InvokeRPC(ZNetView.Everybody, "Rowing_ReleaseSlot", slot, playerId);
        }

        public static void InvalidateCache(Ship ship)
        {
            if (ship == null) return;
            int id = ship.GetInstanceID();
            _cachedSlots.Remove(id);
            _smoothedMultiplier.Remove(id);
        }

        public static void ClearAllCache()
        {
            _cachedSlots.Clear();
            _smoothedMultiplier.Clear();
        }

        private static SlotData[] EmptySlots()
        {
            var arr = new SlotData[Plugin.MaxRowers];
            for (int i = 0; i < Plugin.MaxRowers; i++) arr[i] = new SlotData();
            return arr;
        }
    }
}