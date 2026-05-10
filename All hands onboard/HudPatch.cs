using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace AllHandsOnboard
{
    /// <summary>
    /// Minimal rowing HUD - compact widget in the lower-left corner.
    /// Shows L/R stroke indicators, streak counter, current ship multiplier, and
    /// a verdict popup (PERFECT/GOOD/...) that flashes above the widget for 0.6s.
    /// </summary>
    [HarmonyPatch(typeof(Hud), "Update")]
    public static class HudPatch
    {
        private static GameObject _hudRoot;
        private static Image _metronomeLeft;
        private static Image _metronomeRight;
        private static Text _streakLabel;
        private static Text _multiplierLabel;
        private static Text _verdictLabel;
        private static bool _built;
        private static float _lastMetronomePhase = -1f;

        private static void Postfix(Hud __instance)
        {
            // CRITICAL: capture input edges on the Update frame (Hud.Update runs in Update).
            // The rowing tick runs in FixedUpdate (50Hz != frames) - without this buffering
            // Input.GetKeyDown drops keys.
            RowingInput.PollEdges();

            if (!Plugin.ShowHud.Value)
            {
                if (_hudRoot != null) _hudRoot.SetActive(false);
                return;
            }

            Player local = Player.m_localPlayer;
            Ship onBoard = local != null ? GetShipPlayerIsOn(local) : null;

            var typeData = onBoard != null ? ShipTypeConfig.GetFor(onBoard) : null;
            int activeSlots = typeData != null ? typeData.RowerCount : 0;
            bool show = onBoard != null
                        && onBoard.GetSpeedSetting() == Ship.Speed.Slow
                        && activeSlots > 0;

            if (!_built) BuildHud(__instance);
            if (_hudRoot == null) return;
            _hudRoot.SetActive(show);
            if (!show) return;

            UpdateStreakAndMultiplier(onBoard, local);
            UpdateVerdictLabel();
            UpdateMetronome();
        }

        private static void UpdateStreakAndMultiplier(Ship ship, Player local)
        {
            if (ship == null || local == null) return;

            // HUD is ego-centric: show the local player's streak.
            int streak = LocalRowingState.Streak;
            if (_streakLabel != null)
            {
                if (streak >= 1)
                {
                    _streakLabel.text = $"STREAK x{streak}";
                    Color c = streak >= 20 ? new Color(1f, 0.85f, 0.4f, 1f)
                        : streak >= 5 ? new Color(0.6f, 1f, 0.5f, 1f)
                        : new Color(0.85f, 0.85f, 0.85f, 1f);
                    _streakLabel.color = c;
                }
                else
                {
                    _streakLabel.text = "";
                }
            }

            if (_multiplierLabel != null)
            {
                float mult = ShipRowingManager.GetSmoothedMultiplier(ship);
                float crew = ShipRowingManager.GetCrewSyncFactor(ship);
                float mast = ShipRowingManager.GetMasteryFactor(ship);
                string suffix = "";
                if (crew > 1.01f) suffix += $" SYNC";
                if (mast > 1.01f) suffix += $" MASTER";
                _multiplierLabel.text = $"{mult:F2}x{suffix}";
                _multiplierLabel.color = (crew > 1.01f || mast > 1.01f)
                    ? new Color(1f, 0.9f, 0.4f, 1f)
                    : Color.white;
            }
        }

        private static void UpdateVerdictLabel()
        {
            if (_verdictLabel == null) return;

            var verdict = LocalRowingState.LastVerdict;
            float age = LocalRowingState.LastVerdictAge;
            const float fadeTime = 0.6f;

            if (verdict == LocalRowingState.RhythmVerdict.None || age > fadeTime)
            {
                _verdictLabel.text = "";
                return;
            }

            float alpha = Mathf.Clamp01(1f - (age / fadeTime));
            string text;
            Color baseCol;
            switch (verdict)
            {
                case LocalRowingState.RhythmVerdict.Perfect:
                    text = $"PERFECT x{LocalRowingState.Streak}";
                    baseCol = new Color(0.4f, 1f, 0.4f);
                    break;
                case LocalRowingState.RhythmVerdict.Good:
                    text = "GOOD";
                    baseCol = new Color(0.6f, 1f, 0.5f);
                    break;
                case LocalRowingState.RhythmVerdict.Slow:
                    text = "SLOW";
                    baseCol = new Color(1f, 0.85f, 0.4f);
                    break;
                case LocalRowingState.RhythmVerdict.TooFast:
                    text = "TOO FAST";
                    baseCol = new Color(1f, 0.4f, 0.4f);
                    break;
                case LocalRowingState.RhythmVerdict.WrongSide:
                    text = "WRONG SIDE";
                    baseCol = new Color(1f, 0.3f, 0.9f);
                    break;
                case LocalRowingState.RhythmVerdict.First:
                    text = "START";
                    baseCol = Color.white;
                    break;
                default:
                    text = "";
                    baseCol = Color.white;
                    break;
            }

            // Streak >= 20 gets a pulsing yellow tint as a reward for rhythm masters.
            if (verdict == LocalRowingState.RhythmVerdict.Perfect && LocalRowingState.Streak >= 20)
            {
                float pulse = 0.7f + 0.3f * Mathf.Sin(Time.time * 12f);
                baseCol = Color.Lerp(baseCol, new Color(1f, 0.9f, 0.4f), pulse * 0.6f);
            }

            baseCol.a = alpha;
            _verdictLabel.text = text;
            _verdictLabel.color = baseCol;
        }

        private static void UpdateMetronome()
        {
            // Audio tick is a constant tempo guide - input-independent, always plays in Slow mode.
            if (Plugin.ShowMetronome.Value)
            {
                float interval = Mathf.Max(0.05f, Plugin.TargetStrokeInterval.Value);
                float phase = (Time.time / (interval * 2f)) % 1f;
                if (_lastMetronomePhase >= 0f)
                {
                    bool wrappedToLeft = _lastMetronomePhase > 0.5f && phase < 0.5f;
                    bool crossedToRight = _lastMetronomePhase < 0.5f && phase >= 0.5f;
                    if (wrappedToLeft) MetronomeAudio.PlayTick(true);
                    else if (crossedToRight) MetronomeAudio.PlayTick(false);
                }

                _lastMetronomePhase = phase;
            }

            // L/R visuals flash only on the player's actual key press.
            const float flashDuration = 0.22f;
            Color inactive = new Color(0.18f, 0.18f, 0.18f, 0.85f);
            Color flashCol = new Color(0.5f, 1f, 0.5f, 1f);

            if (_metronomeLeft != null)
                _metronomeLeft.color =
                    FlashColor(LocalRowingState.LastLeftStrokeTime, flashDuration, inactive, flashCol);
            if (_metronomeRight != null)
                _metronomeRight.color =
                    FlashColor(LocalRowingState.LastRightStrokeTime, flashDuration, inactive, flashCol);
        }

        private static Color FlashColor(float lastStrokeTime, float duration, Color inactive, Color flashCol)
        {
            float age = Time.time - lastStrokeTime;
            if (age < 0f || age > duration) return inactive;
            float k = 1f - (age / duration);
            return Color.Lerp(inactive, flashCol, k);
        }

        private static Ship GetShipPlayerIsOn(Player p)
        {
            Ship controlled = p.GetControlledShip();
            if (controlled != null) return controlled;

            var ships = ShipAccess.GetCurrentShips();
            if (ships != null)
            {
                foreach (Ship s in ships)
                    if (s != null && s.IsPlayerInBoat(p))
                        return s;
            }
            else
            {
                foreach (Ship s in Object.FindObjectsOfType<Ship>())
                    if (s != null && s.IsPlayerInBoat(p))
                        return s;
            }

            return null;
        }

        private static void BuildHud(Hud hud)
        {
            _built = true;

            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            _hudRoot = new GameObject("RowingTempoHud");
            _hudRoot.transform.SetParent(hud.transform, false);
            var bg = _hudRoot.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.55f);

            var rt = _hudRoot.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(16f, 240f); // 240px from bottom clears quickslots + HP/stamina + food HUD
            rt.sizeDelta = new Vector2(190f, 54f);

            _metronomeLeft = CreateBox(_hudRoot.transform, "L_Box", "L", font, x: 6f, y: 26f, size: 22f);
            _metronomeRight = CreateBox(_hudRoot.transform, "R_Box", "R", font, x: 32f, y: 26f, size: 22f);

            _streakLabel = CreateText(_hudRoot.transform, "Streak", font, fontSize: 13, bold: true,
                anchoredPos: new Vector2(60f, 26f), size: new Vector2(124f, 22f),
                pivot: new Vector2(0f, 0f), align: TextAnchor.MiddleLeft);

            _multiplierLabel = CreateText(_hudRoot.transform, "Multiplier", font, fontSize: 18, bold: true,
                anchoredPos: new Vector2(6f, 2f), size: new Vector2(178f, 22f),
                pivot: new Vector2(0f, 0f), align: TextAnchor.MiddleLeft);

            var verdictGo = new GameObject("Verdict");
            verdictGo.transform.SetParent(_hudRoot.transform, false);
            var verdictText = verdictGo.AddComponent<Text>();
            verdictText.font = font;
            verdictText.fontSize = 22;
            verdictText.fontStyle = FontStyle.Bold;
            verdictText.alignment = TextAnchor.MiddleLeft;
            verdictText.text = "";
            // Outline keeps the text legible against bright backgrounds (water/sky).
            var outline = verdictGo.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            var verdictRt = verdictGo.GetComponent<RectTransform>();
            verdictRt.anchorMin = new Vector2(0f, 1f);
            verdictRt.anchorMax = new Vector2(0f, 1f);
            verdictRt.pivot = new Vector2(0f, 0f);
            verdictRt.anchoredPosition = new Vector2(4f, 6f);
            verdictRt.sizeDelta = new Vector2(220f, 28f);
            _verdictLabel = verdictText;
        }

        private static Image CreateBox(Transform parent, string name, string label, Font font, float x, float y,
            float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.18f, 0.18f, 0.18f, 0.85f);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(size, size);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var t = labelGo.AddComponent<Text>();
            t.font = font;
            t.fontSize = 14;
            t.fontStyle = FontStyle.Bold;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.white;
            t.text = label;
            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;

            return img;
        }

        private static Text CreateText(Transform parent, string name, Font font, int fontSize, bool bold,
            Vector2 anchoredPos, Vector2 size, Vector2 pivot, TextAnchor align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = font;
            t.fontSize = fontSize;
            t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            t.alignment = align;
            t.color = Color.white;
            t.text = "";
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            return t;
        }
    }
}