using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Procedural oar visual - one cylinder (shaft) + cube (blade) per slot.
    /// Oar count and positions come from ShipTypeConfig (per ship type).
    /// Animation is layered:
    ///   - continuous phase: pendulum motion whose speed scales with the slot's tempo.
    ///   - stroke pulse: quick impulse on each new stroke (detected via ZDO LastUpdate change).
    /// </summary>
    public class ShipOarsVisual : MonoBehaviour
    {
        private Ship _ship;
        private GameObject[] _oarRoots;
        private Transform[] _oarShafts;
        private float[] _phases;
        private float[] _lastSeenStrokeTime;
        private float[] _strokePulse;
        private ShipTypeConfig.TypeData _typeData;

        private bool _built;

        // "Wood" material borrowed from one of the ship's existing renderers - avoids Unity's
        // default Standard shader, which the Valheim render pipeline shows as solid magenta.
        private Material _woodMaterialBase;

        public void Init(Ship ship)
        {
            _ship = ship;
        }

        private void TryBuild()
        {
            if (_built || _ship == null) return;

            _typeData = ShipTypeConfig.GetFor(_ship);
            int n = _typeData.RowerCount;
            _built = true;
            if (n == 0) return; // e.g. raft - nothing to build

            _oarRoots = new GameObject[n];
            _oarShafts = new Transform[n];
            _phases = new float[n];
            _lastSeenStrokeTime = new float[n];
            _strokePulse = new float[n];

            for (int i = 0; i < n; i++) CreateOar(i);
        }

        private void CreateOar(int slot)
        {
            var root = new GameObject($"RowingOar_{slot}");
            root.transform.SetParent(transform, false);
            root.transform.localPosition = _typeData.MountPoints[slot];
            _oarRoots[slot] = root;

            var shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            shaft.name = "Shaft";
            // Drop the colliders - we don't want physics on pure VFX.
            var shaftCol = shaft.GetComponent<Collider>();
            if (shaftCol != null) Destroy(shaftCol);

            shaft.transform.SetParent(root.transform, false);
            float L = _typeData.OarLength;
            float r = _typeData.ShaftRadius;
            // Unity's cylinder primitive is 2 units tall (Y axis), so scale.y = L/2.
            shaft.transform.localScale = new Vector3(r * 2f, L * 0.5f, r * 2f);
            shaft.transform.localPosition = new Vector3(0f, -L * 0.5f, 0f);
            ApplyWoodMaterial(shaft, darker: false);
            _oarShafts[slot] = root.transform;

            var blade = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blade.name = "Blade";
            var bladeCol = blade.GetComponent<Collider>();
            if (bladeCol != null) Destroy(bladeCol);

            blade.transform.SetParent(shaft.transform, false);
            // Compensate for the parent scale so the blade's physical dimensions stay constant.
            blade.transform.localScale = new Vector3(
                _typeData.BladeWidth / (r * 2f),
                _typeData.BladeLength / (L * 0.5f),
                0.05f / (r * 2f));
            blade.transform.localPosition = new Vector3(0f, -1f, 0f);
            ApplyWoodMaterial(blade, darker: true);

            // Default pose: oars "stowed" - laid along the gunwale.
            bool leftSide = (slot % 2 == 0);
            root.transform.localRotation = Quaternion.Euler(0f, 0f, leftSide ? 90f : -90f);
        }

        /// <summary>
        /// Fights the magenta-oar bug: Unity primitives (cylinder/cube) ship with the
        /// "Standard" shader, which Valheim's render pipeline doesn't support - so they
        /// render as magenta. Workaround: borrow a material from one of the ship's existing
        /// renderers (the hull is wood, which suits oars fine).
        /// </summary>
        private Material EnsureWoodBase()
        {
            if (_woodMaterialBase != null) return _woodMaterialBase;
            if (_ship == null) return null;

            var renderers = _ship.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (r.gameObject.name.StartsWith("RowingOar")) continue;
                var sm = r.sharedMaterial;
                if (sm == null || sm.shader == null) continue;
                string sn = sm.shader.name;
                // Skip generic / missing / Standard - none of those work here.
                if (sn == "Standard" || sn == "Sprites/Default" ||
                    sn.Contains("Hidden") || sn.Contains("Default-Material"))
                    continue;
                _woodMaterialBase = sm;
                Plugin.DebugLog($"[Oars] Using wood material from '{r.gameObject.name}': shader={sn}");
                return _woodMaterialBase;
            }

            Plugin.Log.LogWarning("[Oars] No usable wood material on ship - oars may render purple");
            return null;
        }

        private void ApplyWoodMaterial(GameObject go, bool darker)
        {
            var rend = go.GetComponent<Renderer>();
            if (rend == null) return;

            var baseMat = EnsureWoodBase();
            // Use an instance copy - never mutate the ship's sharedMaterial.
            // Without baseMat we fall back to the original (may still render magenta, but won't crash).
            var mat = new Material(baseMat != null ? baseMat : rend.sharedMaterial);
            // Optional tint: if the material exposes _Color, lighten the shaft / darken the blade.
            if (mat.HasProperty("_Color"))
            {
                mat.color = darker
                    ? new Color(0.55f, 0.42f, 0.28f, 1f)
                    : new Color(0.78f, 0.62f, 0.42f, 1f);
            }

            rend.material = mat;
        }

        private void Update()
        {
            if (_ship == null) return;
            if (!_built) TryBuild();
            if (_typeData == null || _typeData.RowerCount == 0) return;

            bool rowingMode = _ship.GetSpeedSetting() == Ship.Speed.Slow;
            SetVisible(rowingMode);
            if (!rowingMode) return;

            var slots = ShipRowingManager.GetSlots(_ship);
            float dt = Time.deltaTime;

            for (int i = 0; i < _typeData.RowerCount; i++)
                AnimateOar(i, slots[i], dt);
        }

        private void SetVisible(bool visible)
        {
            if (_oarRoots == null) return;
            for (int i = 0; i < _oarRoots.Length; i++)
            {
                if (_oarRoots[i] != null && _oarRoots[i].activeSelf != visible)
                    _oarRoots[i].SetActive(visible);
            }
        }

        private void AnimateOar(int slot, ShipRowingManager.SlotData data, float dt)
        {
            Transform oar = _oarShafts[slot];
            if (oar == null) return;

            bool active = data.PlayerId != 0L;
            float tempo = active ? data.Tempo : 0f;

            // Detect a stroke when ZDO LastUpdate advances - all clients see it at the same moment.
            float lastStroke = (float)data.LastUpdate;
            if (active && lastStroke > _lastSeenStrokeTime[slot] + 0.05f)
            {
                _strokePulse[slot] = 1f;
                _lastSeenStrokeTime[slot] = lastStroke;

                Vector3 bladePos = oar.position +
                                   oar.TransformDirection(Vector3.down) * _typeData.OarLength;
                SplashAudio.PlayAt(bladePos, 0.3f);
            }

            _strokePulse[slot] = Mathf.Max(0f, _strokePulse[slot] - dt * 4f);

            // Phase speed: slow when idle, faster under tempo.
            float baseSpeed = 0.15f + tempo * 1.4f;
            _phases[slot] += baseSpeed * Mathf.PI * 2f * dt;

            float amp = Mathf.Lerp(8f, 35f, tempo);
            float zRot = Mathf.Sin(_phases[slot]) * amp;

            // X-axis pulse simulates the "dig" of a stroke.
            float xRot = _strokePulse[slot] * 25f * Mathf.Sin(_strokePulse[slot] * Mathf.PI);

            bool leftSide = (slot % 2 == 0);
            float deployAngle = active ? Mathf.Lerp(45f, 75f, tempo) : 90f;
            float yaw = (leftSide ? -1f : 1f) * deployAngle;

            oar.localRotation = Quaternion.Euler(xRot, yaw, leftSide ? zRot : -zRot);
        }

        private void OnDestroy()
        {
            if (_oarRoots == null) return;
            foreach (var go in _oarRoots)
            {
                if (go != null) Destroy(go);
            }
        }
    }
}