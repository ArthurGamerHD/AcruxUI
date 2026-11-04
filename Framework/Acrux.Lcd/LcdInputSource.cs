using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Input;
using Sandbox.ModAPI;
using VRage.Input;
using VRageMath;

namespace Acrux.Lcd
{
    /// <summary>
    /// Mutable input snapshot for eye-tracked LCD hosts. The host supplies surface-space
    /// pointer/button/wheel events; keyboard state continues to come from the game input API.
    /// </summary>
    public sealed class LcdInputSource : IInputSource
    {
        static readonly char[] EmptyTextInput = new char[0];
        readonly List<MyKeys> _nativeKeys = new List<MyKeys>();
        readonly List<MyKeys> _suppressed = new List<MyKeys>();
        Vector2 _pointer;
        bool _hasPointer;
        bool _primaryPressed;
        bool _newPrimaryPressed;
        bool _newPrimaryReleased;
        bool _middlePressed;
        bool _newMiddlePressed;
        bool _newMiddleReleased;
        bool _secondaryPressed;
        bool _newSecondaryPressed;
        bool _newSecondaryReleased;
        int _wheelDelta;

        /// <summary>
        /// False while keyboard input is routed elsewhere (a native text-entry screen), so pointer passes do not
        /// deliver the same keystrokes again.
        /// </summary>
        public bool KeyboardEnabled { get; set; } = true;

        /// <summary>
        /// Characters to deliver instead of the game's text input, which stays empty unless a native text control
        /// has IME focus.
        /// </summary>
        public IEnumerable<char> TextInputOverride { get; set; }

        public IEnumerable<char> TextInput
        {
            get
            {
                if (!KeyboardEnabled)
                    return EmptyTextInput;
                if (TextInputOverride != null)
                    return TextInputOverride;
                return MyAPIGateway.Input.TextInput;
            }
        }

        public void SetPointer(Vector2 position)
        {
            _pointer = position;
            _hasPointer = !float.IsNaN(position.X) && !float.IsNaN(position.Y);
        }

        public void ClearPointer()
        {
            _hasPointer = false;
        }

        public void SetPrimaryButton(bool pressed)
        {
            _newPrimaryPressed |= pressed && !_primaryPressed;
            _newPrimaryReleased |= !pressed && _primaryPressed;
            _primaryPressed = pressed;
        }

        public void SetMiddleButton(bool pressed)
        {
            _newMiddlePressed |= pressed && !_middlePressed;
            _newMiddleReleased |= !pressed && _middlePressed;
            _middlePressed = pressed;
        }

        public void SetSecondaryButton(bool pressed)
        {
            _newSecondaryPressed |= pressed && !_secondaryPressed;
            _newSecondaryReleased |= !pressed && _secondaryPressed;
            _secondaryPressed = pressed;
        }

        public void Scroll(int delta)
        {
            _wheelDelta += delta;
        }

        public void EndInputPass()
        {
            _newPrimaryPressed = false;
            _newPrimaryReleased = false;
            _newMiddlePressed = false;
            _newMiddleReleased = false;
            _newSecondaryPressed = false;
            _newSecondaryReleased = false;
            _wheelDelta = 0;
        }

        public bool TryGetPointerPosition(out Vector2 position)
        {
            position = _pointer;
            return _hasPointer;
        }

        public int DeltaMouseScrollWheelValue() => _wheelDelta;
        public bool IsPrimaryButtonPressed() => _primaryPressed;
        public bool IsNewPrimaryButtonPressed() => _newPrimaryPressed;
        public bool IsNewPrimaryButtonReleased() => _newPrimaryReleased;
        public bool IsMiddleButtonPressed() => _middlePressed;
        public bool IsNewMiddleButtonPressed() => _newMiddlePressed;
        public bool IsNewMiddleButtonReleased() => _newMiddleReleased;
        public bool IsSecondaryButtonPressed() => _secondaryPressed;
        public bool IsNewSecondaryButtonPressed() => _newSecondaryPressed;
        public bool IsNewSecondaryButtonReleased() => _newSecondaryReleased;
        public bool IsKeyPressed(Key key)
        {
            MyKeys nativeKey;
            return KeyboardEnabled && TryToNativeKey(key, out nativeKey) &&
                   !IsSuppressed(nativeKey) && MyAPIGateway.Input.IsKeyPress(nativeKey);
        }

        public bool IsNewKeyPressed(Key key)
        {
            MyKeys nativeKey;
            return KeyboardEnabled && TryToNativeKey(key, out nativeKey) &&
                   !IsSuppressed(nativeKey) && MyAPIGateway.Input.IsNewKeyPressed(nativeKey);
        }

        /// <summary>
        /// Hides a key that something else already acted on (a text-entry screen ending on Escape) until it is
        /// released, so the same press does not also reach the LCD (closing the dialog behind it).
        /// </summary>
        public void SuppressUntilReleased(Key key)
        {
            MyKeys nativeKey;
            if (TryToNativeKey(key, out nativeKey) && !_suppressed.Contains(nativeKey))
                _suppressed.Add(nativeKey);
        }

        bool IsSuppressed(MyKeys key)
        {
            if (!_suppressed.Contains(key))
                return false;
            if (MyAPIGateway.Input.IsKeyPress(key))
                return true;
            _suppressed.Remove(key);
            return false;
        }

        public void GetPressedKeys(List<Key> keys)
        {
            if (keys == null)
                throw new ArgumentNullException(nameof(keys));
            if (!KeyboardEnabled)
                return;

            _nativeKeys.Clear();
            MyAPIGateway.Input.GetPressedKeys(_nativeKeys);
            for (int i = 0; i < _nativeKeys.Count; i++)
            {
                Key key;
                if (!IsSuppressed(_nativeKeys[i]) && Enum.TryParse(_nativeKeys[i].ToString(), true, out key))
                    keys.Add(key);
            }
        }

        static bool TryToNativeKey(Key key, out MyKeys nativeKey)
        {
            return Enum.TryParse(key.ToString(), true, out nativeKey);
        }
    }
}
