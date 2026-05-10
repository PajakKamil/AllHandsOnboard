using System.Collections.Generic;
using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Per-ship-type rower configuration. Oar count and geometry parameters come from the prefab
    /// name; the actual mount positions are computed from the hull bounds, so this still produces
    /// sensible results even for modded ships.
    /// </summary>
    public static class ShipTypeConfig
    {
        public class TypeData
        {
            public string Label;
            public int RowerCount;
            public float OarLength;
            public float ShaftRadius;
            public float BladeWidth;
            public float BladeLength;
            public Vector3[] MountPoints;
        }

        // Cache keyed by Ship reference - ZNetView.GetZDO isn't always available right after
        // Awake, so we use a weaker key (instance id) instead.
        private static readonly Dictionary<int, TypeData> _cache
            = new Dictionary<int, TypeData>();

        public static TypeData GetFor(Ship ship)
        {
            if (ship == null) return Fallback();

            int id = ship.GetInstanceID();
            if (_cache.TryGetValue(id, out var cached)) return cached;

            var data = Build(ship);
            _cache[id] = data;
            return data;
        }

        public static void Invalidate(Ship ship)
        {
            if (ship == null) return;
            _cache.Remove(ship.GetInstanceID());
        }

        public static void ClearAll() => _cache.Clear();

        private static TypeData Build(Ship ship)
        {
            string name = (ship.gameObject.name ?? "").ToLowerInvariant();
            Bounds b = EstimateHullBounds(ship);

            TypeData result;
            if (name.Contains("raft"))
                result = None("Raft");
            else if (name.Contains("karve"))
                result = MakeData("Karve", rowerCount: 2, hull: b,
                    oarLen: 2.6f, lengthSpread: 0.0f);
            else if (name.Contains("drakkar") || name.Contains("ashland"))
                result = MakeData("Drakkar", rowerCount: 6, hull: b,
                    oarLen: 4.2f, lengthSpread: 0.65f);
            else
                result = MakeData("Longship", rowerCount: 4, hull: b,
                    oarLen: 3.5f, lengthSpread: 0.45f);

            Plugin.Log.LogDebug(
                $"[ShipTypeConfig] Built '{ship.gameObject.name}' -> type={result.Label}, rowers={result.RowerCount}, hull bounds(local)={b.size}");
            return result;
        }

        /// <summary>
        /// Bounds in the ship's local space - otherwise steering would slide the oars around.
        /// The Ship's float collider is the simplest source of hull dimensions.
        /// </summary>
        private static Bounds EstimateHullBounds(Ship ship)
        {
            var floatCol = ShipAccess.GetFloatCollider(ship);
            if (floatCol != null)
            {
                Bounds wb = floatCol.bounds;
                Vector3 lc = ship.transform.InverseTransformPoint(wb.center);
                Vector3 ls = ship.transform.InverseTransformVector(wb.size);
                ls = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                return new Bounds(lc, ls);
            }

            // Fallback: union of all non-trigger colliders.
            Collider[] cols = ship.GetComponentsInChildren<Collider>();
            bool first = true;
            Bounds combined = new Bounds(ship.transform.position, Vector3.zero);

            foreach (var c in cols)
            {
                if (c == null || c.isTrigger) continue;
                if (c.name.StartsWith("RowingOar")) continue;

                if (first)
                {
                    combined = c.bounds;
                    first = false;
                }
                else combined.Encapsulate(c.bounds);
            }

            if (first)
                return new Bounds(Vector3.zero, new Vector3(2.5f, 1.5f, 8f));

            Vector3 localCenter = ship.transform.InverseTransformPoint(combined.center);
            Vector3 localSize = ship.transform.InverseTransformVector(combined.size);
            localSize = new Vector3(
                Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z));
            return new Bounds(localCenter, localSize);
        }

        private static TypeData MakeData(string label, int rowerCount, Bounds hull,
            float oarLen, float lengthSpread)
        {
            int pairs = Mathf.Max(1, rowerCount / 2);
            var mounts = new Vector3[rowerCount];

            // sideOffsetMul = 0.62 nudges mounts just past the gunwale so oars sprout from
            // the deck edge, not the centre of the hull.
            // yOffset = +50% of hull height places them roughly along the gunwale line.
            float xOffset = hull.size.x * 0.62f;
            float yOffset = hull.center.y + hull.size.y * 0.5f;
            float zHalfRange = hull.size.z * 0.5f * lengthSpread;

            for (int p = 0; p < pairs; p++)
            {
                float t = pairs == 1 ? 0.5f : (float)p / (pairs - 1);
                float z = hull.center.z + Mathf.Lerp(-zHalfRange, zHalfRange, t);

                int leftIdx = p * 2;
                int rightIdx = p * 2 + 1;
                if (leftIdx < rowerCount)
                    mounts[leftIdx] = new Vector3(-xOffset, yOffset, z);
                if (rightIdx < rowerCount)
                    mounts[rightIdx] = new Vector3(xOffset, yOffset, z);
            }

            return new TypeData
            {
                Label = label,
                RowerCount = rowerCount,
                OarLength = oarLen,
                ShaftRadius = 0.06f,
                BladeWidth = 0.35f,
                BladeLength = 0.7f,
                MountPoints = mounts,
            };
        }

        private static TypeData None(string label) => new TypeData
        {
            Label = label,
            RowerCount = 0,
            MountPoints = new Vector3[0],
        };

        private static TypeData Fallback() => None("Unknown");
    }
}