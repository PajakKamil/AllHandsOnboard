using UnityEngine;

namespace AllHandsOnboard
{
    /// <summary>
    /// Input abstraction - keyboard (KeyCode) and gamepad triggers (analog axes).
    /// Triggers are turned into "GetKeyDown"-style edges via two-threshold hysteresis,
    /// so resting near 0.5 doesn't spam strokes.
    ///
    /// Edge buffering: Input.GetKeyDown is only true on the Update frame where the key was
    /// pressed. Our Tick runs in FixedUpdate (50Hz) which doesn't always line up with Update.
    /// So polling happens in Update (HudPatch), edges go into a buffer, and FixedUpdate
    /// consumes them. Without this, every few strokes a key gets dropped and the system
    /// sees WRONG SIDE in the middle of perfect rhythm.
    /// </summary>
    public static class RowingInput
    {
        private const float TriggerDownThreshold = 0.5f;
        private const float TriggerUpThreshold = 0.3f;

        public enum InputMode
        {
            Auto,
            Keyboard,
            Gamepad
        }

        private static bool _leftTriggerPrev;
        private static bool _rightTriggerPrev;
        private static InputMode _detectedMode = InputMode.Keyboard;
        private static float _lastKeyboardActivity = -10f;

        // Edge buffer: PollEdges (Update) sets, Consume* (FixedUpdate) clears.
        private static bool _bufferedLeft;
        private static bool _bufferedRight;

        public static InputMode CurrentMode
        {
            get
            {
                var configured = Plugin.InputModePref.Value;
                return configured == InputMode.Auto ? _detectedMode : configured;
            }
        }

        /// <summary>
        /// Sticky detection: once the player has used the keyboard we stay in Keyboard mode
        /// until 5s of keyboard silence AND active gamepad input - phantom JoyAxis values
        /// were flipping the mode and blocking arrow keys without this guard.
        /// </summary>
        public static void UpdateDetection()
        {
            bool kbdNow = Input.anyKeyDown && !IsAnyMouseButtonDown();
            if (kbdNow)
            {
                _detectedMode = InputMode.Keyboard;
                _lastKeyboardActivity = Time.time;
            }
            else if (Time.time - _lastKeyboardActivity > 5f && IsAnyGamepadInput())
            {
                _detectedMode = InputMode.Gamepad;
            }
        }

        /// <summary>
        /// MUST be called from Update (not FixedUpdate) - otherwise Input.GetKeyDown drops keys.
        /// Called once per frame from HudPatch.Postfix (Hud.Update is an Update-frame hook).
        /// </summary>
        public static void PollEdges()
        {
            UpdateDetection();

            if (CurrentMode == InputMode.Keyboard)
            {
                if (Input.GetKeyDown(Plugin.LeftKey.Value)) _bufferedLeft = true;
                if (Input.GetKeyDown(Plugin.RightKey.Value)) _bufferedRight = true;
            }
            else
            {
                UpdateGamepadEdges();
            }
        }

        private static void UpdateGamepadEdges()
        {
            float lAxis = SafeGetAxis(Plugin.LeftPadAxis.Value);
            bool lDown = _leftTriggerPrev ? lAxis > TriggerUpThreshold : lAxis > TriggerDownThreshold;
            if (lDown && !_leftTriggerPrev) _bufferedLeft = true;
            _leftTriggerPrev = lDown;

            float rAxis = SafeGetAxis(Plugin.RightPadAxis.Value);
            bool rDown = _rightTriggerPrev ? rAxis > TriggerUpThreshold : rAxis > TriggerDownThreshold;
            if (rDown && !_rightTriggerPrev) _bufferedRight = true;
            _rightTriggerPrev = rDown;
        }

        public static bool ConsumeLeftStroke()
        {
            bool b = _bufferedLeft;
            _bufferedLeft = false;
            return b;
        }

        public static bool ConsumeRightStroke()
        {
            bool b = _bufferedRight;
            _bufferedRight = false;
            return b;
        }

        public static void ClearBuffer()
        {
            _bufferedLeft = false;
            _bufferedRight = false;
        }

        private static float SafeGetAxis(string axisName)
        {
            // Valheim's ZInput has its own aliases but the API shifts between versions,
            // so we go straight through Unity Input. Players can override axis names in
            // the config if their gamepad mapping differs.
            try
            {
                return Input.GetAxisRaw(axisName);
            }
            catch
            {
                return 0f;
            }
        }

        private static bool IsAnyMouseButtonDown()
        {
            return Input.GetMouseButtonDown(0)
                   || Input.GetMouseButtonDown(1)
                   || Input.GetMouseButtonDown(2);
        }

        private static bool IsAnyGamepadInput()
        {
            if (SafeGetAxis(Plugin.LeftPadAxis.Value) > 0.1f) return true;
            if (SafeGetAxis(Plugin.RightPadAxis.Value) > 0.1f) return true;

            for (int i = 0; i < 20; i++)
            {
                if (Input.GetKey(KeyCode.JoystickButton0 + i)) return true;
            }

            return false;
        }

        public static string GetCurrentBindingDescription(bool isLeft)
        {
            if (CurrentMode == InputMode.Keyboard)
            {
                return (isLeft ? Plugin.LeftKey.Value : Plugin.RightKey.Value).ToString();
            }

            return isLeft ? "LT" : "RT";
        }
    }
}