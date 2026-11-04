using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Input;
using Sandbox.ModAPI;
using VRage.Input;
using VRageMath;
using IMyInput = VRage.ModAPI.IMyInput;

namespace Acrux.ScreenSpace
{
    public sealed class ScreenSpaceInputSource : IInputSource
    {
        static readonly char[] EmptyTextInput = Array.Empty<char>();
        readonly IMyInput _input;
        readonly List<MyKeys> _nativeKeys = new List<MyKeys>();
        readonly List<char> _translatedText = new List<char>();
        readonly KeyTextFallback _textFallback = new KeyTextFallback();

        public ScreenSpaceInputSource(IMyInput input)
        {
            _input = input;
        }

        public bool CapsLockActive => _textFallback.CapsLockActive;

        public IEnumerable<char> TextInput
        {
            get
            {
                if (_input == null)
                    return EmptyTextInput;
                return _input.TextInput.Count > 0 ? _input.TextInput : _translatedText;
            }
        }

        /// <summary>Advance held-key state once per screen input frame, including
        /// frames where no text box has focus.</summary>
        public void AdvanceFrame()
        {
            _translatedText.Clear();
            if (_input == null) return;
            _nativeKeys.Clear();
            _input.GetPressedKeys(_nativeKeys);
            _textFallback.Collect(_nativeKeys, _input.IsAnyShiftKeyPressed(),
                _input.IsAnyCtrlKeyPressed(), _translatedText);
        }

        public bool TryGetPointerPosition(out Vector2 position)
        {
            position = Vector2.Zero;
            return _input != null && MyScreenSpace.TryGetCursorPositionPixels(out position);
        }

        public int DeltaMouseScrollWheelValue()
        {
            return _input == null ? 0 : _input.DeltaMouseScrollWheelValue();
        }

        public bool IsPrimaryButtonPressed() =>
            _input != null && _input.IsPrimaryButtonPressed();
        public bool IsNewPrimaryButtonPressed() =>
            _input != null && _input.IsNewPrimaryButtonPressed();
        public bool IsNewPrimaryButtonReleased() =>
            _input != null && _input.IsNewPrimaryButtonReleased();
        public bool IsMiddleButtonPressed() =>
            _input != null && _input.IsMiddleMousePressed();
        public bool IsNewMiddleButtonPressed() =>
            _input != null && _input.IsNewMiddleMousePressed();
        public bool IsNewMiddleButtonReleased() =>
            _input != null && _input.IsNewMiddleMouseReleased();
        public bool IsSecondaryButtonPressed() =>
            _input != null && _input.IsSecondaryButtonPressed();
        public bool IsNewSecondaryButtonPressed() =>
            _input != null && _input.IsNewSecondaryButtonPressed();
        public bool IsNewSecondaryButtonReleased() =>
            _input != null && _input.IsNewSecondaryButtonReleased();
        public bool IsKeyPressed(Key key)
        {
            MyKeys nativeKey;
            return _input != null && TryToNativeKey(key, out nativeKey) &&
                   _input.IsKeyPress(nativeKey);
        }

        public bool IsNewKeyPressed(Key key)
        {
            MyKeys nativeKey;
            return _input != null && TryToNativeKey(key, out nativeKey) &&
                   _input.IsNewKeyPressed(nativeKey);
        }

        public void GetPressedKeys(List<Key> keys)
        {
            if (keys == null)
                throw new ArgumentNullException(nameof(keys));
            if (_input == null)
                return;

            _nativeKeys.Clear();
            _input.GetPressedKeys(_nativeKeys);
            for (int i = 0; i < _nativeKeys.Count; i++)
            {
                Key key;
                if (Enum.TryParse(_nativeKeys[i].ToString(), true, out key))
                    keys.Add(key);
            }
        }

        static bool TryToNativeKey(Key key, out MyKeys nativeKey)
        {
            return Enum.TryParse(key.ToString(), true, out nativeKey);
        }

        // SE only emits native TextInput while one of its own IME-aware GUI
        // controls has focus. Mod-owned ADK TextBoxes need a keyboard fallback.
        sealed class KeyTextFallback
        {
            const int RepeatDelayFrames = 30;
            const int RepeatIntervalFrames = 3;
            static readonly Dictionary<MyKeys, string> Symbols = new Dictionary<MyKeys, string>
            {
                { MyKeys.Space, "  " },
                { MyKeys.D0, "0)" }, { MyKeys.D1, "1!" }, { MyKeys.D2, "2@" },
                { MyKeys.D3, "3#" }, { MyKeys.D4, "4$" }, { MyKeys.D5, "5%" },
                { MyKeys.D6, "6^" }, { MyKeys.D7, "7&" }, { MyKeys.D8, "8*" },
                { MyKeys.D9, "9(" },
                { MyKeys.NumPad0, "00" }, { MyKeys.NumPad1, "11" },
                { MyKeys.NumPad2, "22" }, { MyKeys.NumPad3, "33" },
                { MyKeys.NumPad4, "44" }, { MyKeys.NumPad5, "55" },
                { MyKeys.NumPad6, "66" }, { MyKeys.NumPad7, "77" },
                { MyKeys.NumPad8, "88" }, { MyKeys.NumPad9, "99" },
                { MyKeys.Multiply, "**" }, { MyKeys.Add, "++" },
                { MyKeys.Subtract, "--" }, { MyKeys.Decimal, ".." },
                { MyKeys.Divide, "//" },
                { MyKeys.OemMinus, "-_" }, { MyKeys.OemPlus, "=+" },
                { MyKeys.OemComma, ",<" }, { MyKeys.OemPeriod, ".>" },
                { MyKeys.OemQuestion, "/?" }, { MyKeys.OemSemicolon, ";:" },
                { MyKeys.OemQuotes, "'\"" }, { MyKeys.OemOpenBrackets, "[{" },
                { MyKeys.OemCloseBrackets, "]}" }, { MyKeys.OemPipe, "\\|" },
                { MyKeys.OemBackslash, "\\|" }, { MyKeys.OemTilde, "`~" }
            };

            readonly Dictionary<MyKeys, int> _held = new Dictionary<MyKeys, int>();
            readonly List<MyKeys> _released = new List<MyKeys>();
            bool _capsLock;
            bool _capsWasDown;

            public bool CapsLockActive => _capsLock;

            public void Collect(IList<MyKeys> pressed, bool shift, bool ctrl, List<char> output)
            {
                bool capsDown = pressed.Contains(MyKeys.CapsLock);
                if (capsDown && !_capsWasDown) _capsLock = !_capsLock;
                _capsWasDown = capsDown;
                _released.Clear();
                foreach (MyKeys key in _held.Keys)
                    if (!pressed.Contains(key)) _released.Add(key);
                for (int i = 0; i < _released.Count; i++) _held.Remove(_released[i]);
                for (int i = 0; i < pressed.Count; i++)
                {
                    MyKeys key = pressed[i];
                    char character;
                    if (key >= MyKeys.A && key <= MyKeys.Z)
                        character = (char)((shift != _capsLock ? 'A' : 'a') + (key - MyKeys.A));
                    else
                    {
                        string pair;
                        if (!Symbols.TryGetValue(key, out pair)) continue;
                        character = pair[shift ? 1 : 0];
                    }
                    int frames;
                    _held.TryGetValue(key, out frames);
                    _held[key] = ++frames;
                    if (!ctrl && (frames == 1 ||
                        frames >= RepeatDelayFrames &&
                        (frames - RepeatDelayFrames) % RepeatIntervalFrames == 0))
                        output.Add(character);
                }
            }
        }
    }
}
