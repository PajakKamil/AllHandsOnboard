using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Local player rowing state. Tracks tempo and periodically syncs it to the ship's ZDO.
    /// Keys are ignored while inventory/menu/chat is open so we don't collide with other UI.
    /// </summary>
    public static class LocalRowingState
    {
        private const int LeftStroke = -1;
        private const int RightStroke = 1;
        private const int NoStroke = 0;

        public enum RhythmVerdict
        {
            None,
            First,
            TooFast,
            Slow,
            Good,
            Perfect,
            WrongSide
        }

        private static float _tempo;
        private static int _lastStroke = NoStroke;

        private static float _lastStrokeTime;

        // Same stroke timestamp in ZNet time (shared across clients) - used for crew phase sync.
        private static double _lastStrokeAbsTime;
        private static float _lastSyncTime;
        private static int _currentSlot = -1;

        // PERFECT strokes in a row. Resets on TOO FAST / WRONG SIDE / long pause.
        private static int _streak;

        // Last LEFT/RIGHT timestamps for the metronome HUD flash.
        private static float _lastLeftStrokeTime = -10f;
        private static float _lastRightStrokeTime = -10f;

        private static RhythmVerdict _lastVerdict = RhythmVerdict.None;
        private static float _lastVerdictTime;

        public static float Tempo => _tempo;
        public static int CurrentSlot => _currentSlot;
        public static Ship CurrentShip { get; private set; }
        public static int Streak => _streak;
        public static RhythmVerdict LastVerdict => _lastVerdict;
        public static float LastVerdictAge => Time.time - _lastVerdictTime;
        public static double LastStrokeAbsTime => _lastStrokeAbsTime;
        public static float LastLeftStrokeTime => _lastLeftStrokeTime;
        public static float LastRightStrokeTime => _lastRightStrokeTime;

        public static void SetSlot(int slot, Ship ship)
        {
            if (_currentSlot != slot || CurrentShip != ship)
            {
                _currentSlot = slot;
                CurrentShip = ship;
                if (slot == -1)
                {
                    _tempo = 0f;
                    _lastStroke = NoStroke;
                    _streak = 0;
                    _lastVerdict = RhythmVerdict.None;
                    RowingInput.ClearBuffer();
                }
            }
        }

        private static int _tickLogCounter;

        public static void Tick(float dt, Ship ship)
        {
            if (_currentSlot == -1 || ship == null)
            {
                _tempo = 0f;
                _lastStroke = NoStroke;
                _streak = 0;
                RowingInput.ClearBuffer();
                return;
            }

            // Grace period: tempo holds steady for 3x TargetStrokeInterval (~1.2s by default)
            // so small mistakes or brief pauses don't bounce ship speed. Decay only kicks in
            // after a much longer absence of strokes.
            float graceWindow = Plugin.TargetStrokeInterval.Value * 3.0f;
            if (Time.time - _lastStrokeTime > graceWindow)
            {
                _tempo = Mathf.Max(0f, _tempo - Plugin.DecayPerSecond.Value * dt);
                if (_streak > 0 && Time.time - _lastStrokeTime > graceWindow * 1.5f)
                    _streak = 0;
            }

            // After a much longer pause clear _lastStroke so a returning player can start
            // on either side without copping a "WRONG SIDE" penalty.
            if (_lastStroke != NoStroke && Time.time - _lastStrokeTime > Plugin.TargetStrokeInterval.Value * 4f)
            {
                _lastStroke = NoStroke;
            }

            if (IsAnyUIBlockingInput())
            {
                if (++_tickLogCounter % 50 == 0)
                    Plugin.DebugLog($"[Tick] UI blocking input - clearing buffer");
                RowingInput.ClearBuffer();
                return;
            }

            // Inputs are captured on the Update frame by RowingInput.PollEdges (called from
            // HudPatch.Postfix). Here we consume the buffer so each press fires exactly one
            // stroke even when FixedUpdate doesn't line up with an Update frame.
            bool leftPressed = RowingInput.ConsumeLeftStroke();
            bool rightPressed = RowingInput.ConsumeRightStroke();

            if (++_tickLogCounter % 50 == 0)
            {
                Plugin.DebugLog(
                    $"[Tick] slot={_currentSlot}, mode={RowingInput.CurrentMode}, leftDown={leftPressed}, rightDown={rightPressed}, lastStroke={_lastStroke}, tempo={_tempo:F2}, streak={_streak}");
            }

            if (leftPressed) Plugin.DebugLog($"[Tick] LEFT consumed (KeyCode={Plugin.LeftKey.Value})");
            if (rightPressed) Plugin.DebugLog($"[Tick] RIGHT consumed (KeyCode={Plugin.RightKey.Value})");

            // Both keys in the same frame: treat as one alternating stroke, picking
            // whichever side differs from the previous one.
            if (leftPressed && rightPressed)
            {
                if (_lastStroke == LeftStroke) leftPressed = false;
                else rightPressed = false;
            }

            if (leftPressed && _lastStroke != LeftStroke)
            {
                RegisterStroke(LeftStroke, "LEFT");
            }
            else if (rightPressed && _lastStroke != RightStroke)
            {
                RegisterStroke(RightStroke, "RIGHT");
            }
            else if ((leftPressed && _lastStroke == LeftStroke) ||
                     (rightPressed && _lastStroke == RightStroke))
            {
                HandleSameKeyTwice(leftPressed);
            }

            if (Time.time - _lastSyncTime >= Plugin.SyncIntervalSeconds.Value)
            {
                SyncToZdo(ship);
                _lastSyncTime = Time.time;
            }
        }

        /// <summary>
        /// Same key twice in a row - always a mistake regardless of timing, since rowing
        /// requires L/R alternation. Penalises tempo, resets streak, sets WRONG SIDE verdict.
        /// _lastStrokeTime is left untouched so the next alternating stroke is judged
        /// against the last LEGITIMATE stroke, letting the player slip back into rhythm.
        /// </summary>
        private static void HandleSameKeyTwice(bool leftPressed)
        {
            float now = Time.time;
            float gap = now - _lastStrokeTime;
            string sideLabel = leftPressed ? "LEFT" : "RIGHT";

            _tempo = Mathf.Max(0f, _tempo - Plugin.BoostPerStroke.Value * Plugin.WrongSidePenaltyMul.Value);
            _streak = 0;
            _lastVerdict = RhythmVerdict.WrongSide;
            _lastVerdictTime = now;
            Plugin.DebugLog(
                $"[Stroke] {sideLabel} WRONG SIDE (same key 2x, gap={gap:F2}s) - streak reset, tempo={_tempo:F2}");
        }

        private static void RegisterStroke(int stroke, string sideLabel)
        {
            float now = Time.time;
            float timeSinceLast = now - _lastStrokeTime;
            float target = Plugin.TargetStrokeInterval.Value;

            // Rhythm tolerance windows, ratio = timeSinceLast / target. All thresholds in [Rhythm] config.
            //   < TooFastRatio                          -> TOO FAST  (spam, TooFastPenaltyMul)
            //   < PerfectMinRatio OR > PerfectMaxRatio  -> GOOD      (GoodBonusMul)
            //   PerfectMin..PerfectMax                   -> PERFECT   (PerfectBonusMul)
            //   > SlowRatio                              -> SLOW      (SlowBonusMul, no penalty, no reward)
            //   FIRST (after reset)                      -> 1.0x, skips judgement
            float ratio = timeSinceLast / Mathf.Max(0.01f, target);
            float bonus;
            RhythmVerdict verdict;

            if (_lastStroke == NoStroke)
            {
                bonus = 1.0f;
                verdict = RhythmVerdict.First;
            }
            else if (ratio < Plugin.TooFastRatio.Value)
            {
                bonus = Plugin.TooFastPenaltyMul.Value;
                verdict = RhythmVerdict.TooFast;
            }
            else if (ratio >= Plugin.PerfectMinRatio.Value && ratio <= Plugin.PerfectMaxRatio.Value)
            {
                bonus = Plugin.PerfectBonusMul.Value;
                verdict = RhythmVerdict.Perfect;
            }
            else if (ratio < Plugin.SlowRatio.Value)
            {
                bonus = Plugin.GoodBonusMul.Value;
                verdict = RhythmVerdict.Good;
            }
            else
            {
                bonus = Plugin.SlowBonusMul.Value;
                verdict = RhythmVerdict.Slow;
            }

            // Streak: only PERFECT increments. GOOD is neutral (no add, no reset) so a slightly
            // off-beat stroke acts as a safe cushion. TOO FAST / SLOW reset, and WRONG SIDE resets
            // via HandleSameKeyTwice. This separates "rhythmic rowing" from "near-rhythm" -
            // mastery (>=20 streak) demands clockwork, not just hovering near the target.
            if (verdict == RhythmVerdict.Perfect)
            {
                _streak++;
            }
            else if (verdict == RhythmVerdict.TooFast || verdict == RhythmVerdict.Slow)
            {
                _streak = 0;
            }
            // Good - intentionally no-op

            float streakMul = _streak >= Plugin.StreakMinForBonus.Value ? Plugin.StreakBonusMul.Value : 1.0f;
            _tempo = Mathf.Min(1f, _tempo + Plugin.BoostPerStroke.Value * bonus * streakMul);
            _lastStroke = stroke;
            _lastStrokeTime = now;
            _lastStrokeAbsTime = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0.0;
            if (stroke == LeftStroke) _lastLeftStrokeTime = now;
            else if (stroke == RightStroke) _lastRightStrokeTime = now;
            _lastVerdict = verdict;
            _lastVerdictTime = now;

            // Notify other clients about this stroke immediately (event-driven, not waiting on
            // cyclic sync) - crew sync compares stroke timestamps across players in real time.
            SendStrokeEvent(CurrentShip);

            string streakNote = _streak >= 3 ? $" STREAK x{_streak}" : "";
            Plugin.DebugLog(
                $"[Stroke] {sideLabel} {verdict} (gap={timeSinceLast:F2}s/target={target:F2}s, bonus={bonus * streakMul:F2}x, tempo={_tempo:F2}{streakNote})");
        }

        private static void SyncToZdo(Ship ship)
        {
            var nview = ShipAccess.GetNView(ship);
            if (nview == null || !nview.IsValid()) return;

            long myId = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            nview.InvokeRPC(ZNetView.Everybody, "Rowing_UpdateSlot",
                _currentSlot, _tempo, myId, ZNet.instance.GetTimeSeconds());
        }

        /// <summary>
        /// Emits the latest-stroke event over a separate RPC (Rowing_UpdateSlot already
        /// uses the max generic-arg count). Called from RegisterStroke for every valid stroke.
        /// </summary>
        private static void SendStrokeEvent(Ship ship)
        {
            if (ship == null) return;
            var nview = ShipAccess.GetNView(ship);
            if (nview == null || !nview.IsValid()) return;
            long myId = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            // Streak rides along with the event - no separate cyclic sync needed since
            // streak only changes on strokes anyway.
            nview.InvokeRPC(ZNetView.Everybody, "Rowing_StrokeAt",
                _currentSlot, myId, _lastStrokeAbsTime, _streak);
        }

        private static bool IsAnyUIBlockingInput()
        {
            try
            {
                if (InventoryGui.IsVisible()) return true;
                if (Menu.IsVisible()) return true;
                if (Chat.instance != null && Chat.instance.HasFocus()) return true;
                if (TextInput.IsVisible()) return true;
                if (StoreGui.IsVisible()) return true;
                if (Minimap.IsOpen()) return true;
                if (TextViewer.instance != null && TextViewer.instance.IsVisible()) return true;
            }
            catch
            {
            }

            return false;
        }
    }
}