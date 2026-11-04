using System.Collections.Generic;
using Acrux.Core.Input;
using VRageMath;

namespace Acrux.Core
{
    public interface IInputSource
    {
        IEnumerable<char> TextInput { get; }

        bool TryGetPointerPosition(out Vector2 position);
        int DeltaMouseScrollWheelValue();
        bool IsPrimaryButtonPressed();
        bool IsNewPrimaryButtonPressed();
        bool IsNewPrimaryButtonReleased();
        bool IsMiddleButtonPressed();
        bool IsNewMiddleButtonPressed();
        bool IsNewMiddleButtonReleased();
        bool IsSecondaryButtonPressed();
        bool IsNewSecondaryButtonPressed();
        bool IsNewSecondaryButtonReleased();
        bool IsKeyPressed(Key key);
        bool IsNewKeyPressed(Key key);
        void GetPressedKeys(List<Key> keys);
    }
}
