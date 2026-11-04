using System;
using System.Collections.Generic;
using Adk.Utils;
using LcdEyeTrackingApi.Geometry;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace LcdEyeTrackingApi
{
    /// <summary>
    /// Each frame the camera ray picks the nearest registered surface; that surface
    /// receives the pointer, buttons and wheel, and the player's tool actions are
    /// suppressed so clicking a screen doesn't also fire the held tool.
    /// </summary>
    internal sealed class ScreenTracker
    {
        const double MAX_TRACKING_DISTANCE_METERS = 20d;
        const double MAX_TRACKING_DISTANCE_SQ = MAX_TRACKING_DISTANCE_METERS * MAX_TRACKING_DISTANCE_METERS;
        const int HEARTBEAT_TIMEOUT_FRAMES = 30;
        const ushort INPUT_BLOCK_CHANNEL = 47221;
        const int BUTTON_COUNT = 5;

        static readonly string[] UseInputControls =
        {
            MyControlsSpace.PRIMARY_TOOL_ACTION.String,
            MyControlsSpace.SECONDARY_TOOL_ACTION.String
        };

        sealed class Registration : ITrackedSurface
        {
            public long Handle;
            public IMyCubeBlock Block { get; set; }
            public IMyTextSurface Surface { get; set; }
            public Action<Vector2> Moved;
            public Action Left;
            public Action<int, bool> Button;
            public Action<int> Scrolled;
            public Action CaptureLost;
            public long CaptureGroup;
            public PointerFlags Flags;
            public int LastHeartbeat = int.MinValue;
            int _surfaceIndex = -1;

            public int RotationOrSurfaceIndex
            {
                get
                {
                    if (_surfaceIndex < 0)
                        _surfaceIndex = ResolveSurfaceIndex(Block, Surface);
                    return _surfaceIndex;
                }
            }

            public bool IsAlive => Block != null && !Block.MarkedForClose && !Block.Closed;
        }

        readonly Dictionary<long, Registration> _registrations = new Dictionary<long, Registration>();
        readonly List<Registration> _iteration = new List<Registration>();
        readonly bool[] _sentButtons = new bool[BUTTON_COUNT];
        readonly bool[] _heldButtons = new bool[BUTTON_COUNT];
        long _nextHandle;
        Registration _hovered;
        Registration _capture;
        bool _inputBlocked;
        Color _originalHighlight = Color.Transparent;
        IMyControl _lookAround;
        bool _serverHandlerRegistered;

        public void Load()
        {
            if (MyAPIGateway.Multiplayer != null && MyAPIGateway.Multiplayer.IsServer)
            {
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(INPUT_BLOCK_CHANNEL, OnInputBlockMessage);
                _serverHandlerRegistered = true;
            }
        }

        public void Unload()
        {
            if (_serverHandlerRegistered)
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(INPUT_BLOCK_CHANNEL, OnInputBlockMessage);
            _serverHandlerRegistered = false;
            if (_inputBlocked)
                SetInputBlocked(false);
            _registrations.Clear();
            _hovered = null;
            _capture = null;
        }

        public long Register(IMyCubeBlock block, IMyTextSurface surface, Action<Vector2> moved, Action left,
            Action<int, bool> button, Action<int> scrolled, Action captureLost, long captureGroup)
        {
            if (block == null || surface == null)
                return 0L;
            var registration = new Registration
            {
                Handle = ++_nextHandle,
                Block = block,
                Surface = surface,
                Moved = moved,
                Left = left,
                Button = button,
                Scrolled = scrolled,
                CaptureLost = captureLost
            };
            registration.CaptureGroup = captureGroup != 0L ? captureGroup : -registration.Handle;
            _registrations[registration.Handle] = registration;
            return registration.Handle;
        }

        public void Unregister(long handle)
        {
            Registration registration;
            if (!_registrations.TryGetValue(handle, out registration))
                return;
            _registrations.Remove(handle);
            // Detach without callbacks: the owner is going away.
            if (ReferenceEquals(_hovered, registration))
                _hovered = null;
            if (ReferenceEquals(_capture, registration))
            {
                _capture = null;
                Array.Clear(_sentButtons, 0, BUTTON_COUNT);
            }
        }

        public void Heartbeat(long handle)
        {
            Registration registration;
            if (_registrations.TryGetValue(handle, out registration))
                registration.LastHeartbeat = MyAPIGateway.Session.GameplayFrameCounter;
        }

        public void SetFlags(long handle, PointerFlags flags)
        {
            Registration registration;
            if (_registrations.TryGetValue(handle, out registration))
                registration.Flags = flags;
        }

        public bool IsButtonHeld(PointerButton button)
        {
            int index = (int)button;
            return index >= 0 && index < BUTTON_COUNT && _heldButtons[index];
        }

        public void Update()
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Input == null)
                return;
            try
            {
                Track();
            }
            catch (Exception error)
            {
                LogHelper.Log(MyLogSeverity.Error, error.ToString());
            }
        }

        void Track()
        {
            ReadButtons();
            if (_lookAround == null)
                _lookAround = MyAPIGateway.Input.GetGameControl(MyStringId.GetOrCompute("LOOKAROUND"));
            bool altHeld = _lookAround != null && _lookAround.IsPressed();

            Vector3D origin;
            Vector3D direction;
            if (_registrations.Count == 0 || !CanTrack(altHeld) || !TryGetCameraRay(out origin, out direction))
            {
                SetHovered(null, Vector2.Zero);
                SetCapture(null);
                SetInputBlocked(false);
                return;
            }

            int frame = MyAPIGateway.Session.GameplayFrameCounter;
            Registration nearest = null;
            Vector2 nearestPoint = Vector2.Zero;
            double nearestDistance = double.MaxValue;
            _iteration.Clear();
            _iteration.AddRange(_registrations.Values);
            foreach (Registration registration in _iteration)
            {
                if (!registration.IsAlive)
                {
                    Unregister(registration.Handle);
                    continue;
                }
                if ((registration.Flags & PointerFlags.RequiresAlt) != 0 && !altHeld)
                    continue;
                if ((registration.Flags & PointerFlags.RequiresHeartbeat) != 0 &&
                    (registration.LastHeartbeat == int.MinValue ||
                     frame - registration.LastHeartbeat > HEARTBEAT_TIMEOUT_FRAMES))
                    continue;
                if (Vector3D.DistanceSquared(registration.Block.WorldMatrix.Translation, origin) >
                    MAX_TRACKING_DISTANCE_SQ)
                    continue;

                Vector2 point;
                double distance;
                if (ScreenAreaGeometry.TryGetScreenPointIntersection(
                        registration, origin, direction, out point, out distance) &&
                    distance < nearestDistance)
                {
                    nearest = registration;
                    nearestPoint = point;
                    nearestDistance = distance;
                }
            }
            _iteration.Clear();

            // Only the nearest surface along the ray is being looked at.
            SetHovered(nearest, nearestPoint);
            SetInputBlocked(nearest != null);

            bool anyHeld = false;
            for (int i = 0; i < BUTTON_COUNT; i++)
                anyHeld |= _heldButtons[i];

            // A held button keeps its owner through the gap between panels; within
            // one capture group the input follows the panel under the cursor.
            Registration target = nearest;
            if (target == null && anyHeld && _capture != null && _capture.IsAlive)
                target = _capture;
            if (target != null && _capture != null && target.CaptureGroup == _capture.CaptureGroup)
                _capture = target;
            else
                SetCapture(target);

            if (_capture != null)
                for (int i = 0; i < BUTTON_COUNT; i++)
                {
                    if (_sentButtons[i] == _heldButtons[i])
                        continue;
                    _sentButtons[i] = _heldButtons[i];
                    Registration owner = _capture;
                    bool pressed = _heldButtons[i];
                    int button = i;
                    Invoke(owner.Button, callback => callback(button, pressed));
                }

            int wheel = MyAPIGateway.Input.DeltaMouseScrollWheelValue();
            if (nearest != null && wheel != 0)
                Invoke(nearest.Scrolled, callback => callback(wheel));
        }

        void ReadButtons()
        {
            VRage.ModAPI.IMyInput input = MyAPIGateway.Input;
            _heldButtons[(int)PointerButton.Primary] = input.IsLeftMousePressed() ||
                                                      input.IsJoystickButtonPressed(MyJoystickButtonsEnum.J06);
            _heldButtons[(int)PointerButton.Secondary] = input.IsRightMousePressed();
            _heldButtons[(int)PointerButton.Middle] = input.IsMiddleMousePressed();
            _heldButtons[(int)PointerButton.Back] = input.IsXButton1MousePressed();
            _heldButtons[(int)PointerButton.Forward] = input.IsXButton2MousePressed();
        }

        static bool CanTrack(bool altHeld)
        {
            if (MyAPIGateway.Gui.IsCursorVisible)
                return false;
            var cubeBuilder = MyCubeBuilder.Static;
            if (cubeBuilder != null && cubeBuilder.IsActivated && cubeBuilder.IsBuildToolActive())
                return false;
            // Seated at ship controls, mouse look steers the ship unless Alt frees the camera.
            var player = MyAPIGateway.Session.LocalHumanPlayer;
            var controlled = player == null || player.Controller == null || player.Controller.ControlledEntity == null
                ? null
                : player.Controller.ControlledEntity.Entity as MyCubeBlock;
            var cockpit = controlled == null ? null : controlled.BlockDefinition as MyCockpitDefinition;
            return altHeld || cockpit == null || !cockpit.EnableShipControl;
        }

        void SetHovered(Registration next, Vector2 point)
        {
            Registration previous = _hovered;
            _hovered = next;
            if (!ReferenceEquals(previous, next) && previous != null && previous.IsAlive)
                Invoke(previous.Left, callback => callback());
            if (next != null)
                Invoke(next.Moved, callback => callback(point));
        }

        void SetCapture(Registration next)
        {
            if (ReferenceEquals(_capture, next))
                return;
            Registration previous = _capture;
            _capture = next;
            Array.Clear(_sentButtons, 0, BUTTON_COUNT);
            if (previous != null && previous.IsAlive)
                Invoke(previous.CaptureLost, callback => callback());
        }

        static void Invoke<T>(T callback, Action<T> invoke) where T : class
        {
            if (callback == null)
                return;
            try
            {
                invoke(callback);
            }
            catch (Exception error)
            {
                LogHelper.Log(MyLogSeverity.Error, error.ToString());
            }
        }

        void SetInputBlocked(bool blocked)
        {
            if (_inputBlocked == blocked)
                return;
            IMyPlayer player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.LocalHumanPlayer;
            if (player == null)
                return;
            _inputBlocked = blocked;
            ApplyUseInputEnabled(player.IdentityId, !blocked);
            // The tool action is applied on the server too.
            if (MyAPIGateway.Multiplayer.MultiplayerActive && !MyAPIGateway.Multiplayer.IsServer)
                MyAPIGateway.Multiplayer.SendMessageToServer(INPUT_BLOCK_CHANNEL,
                    new[] { blocked ? (byte)0 : (byte)1 });

            // Hide the block highlight while a screen owns the cursor.
            var environment = MyDefinitionManager.Static.EnvironmentDefinition;
            if (blocked)
            {
                if (environment.ContourHighlightColor != Color.Transparent)
                    _originalHighlight = environment.ContourHighlightColor;
                environment.ContourHighlightColor = Color.Transparent;
            }
            else if (_originalHighlight != Color.Transparent)
            {
                environment.ContourHighlightColor = _originalHighlight;
            }
        }

        static void ApplyUseInputEnabled(long identityId, bool enabled)
        {
            foreach (string control in UseInputControls)
                MyVisualScriptLogicProvider.SetPlayerInputBlacklistState(control, identityId, enabled);
        }

        static void OnInputBlockMessage(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            if (data == null || data.Length != 1)
                return;
            // Only the sender's own identity: a client can't block someone else.
            long identityId = MyAPIGateway.Players.TryGetIdentityId(sender);
            if (identityId != 0L)
                ApplyUseInputEnabled(identityId, data[0] != 0);
        }

        static bool TryGetCameraRay(out Vector3D origin, out Vector3D direction)
        {
            origin = Vector3D.Zero;
            direction = Vector3D.Zero;
            var session = MyAPIGateway.Session;
            if (session.Camera != null)
            {
                MatrixD matrix = session.Camera.WorldMatrix;
                origin = matrix.Translation;
                direction = matrix.Forward;
                if (direction.LengthSquared() > 1e-8)
                {
                    direction.Normalize();
                    return true;
                }
            }

            var cameraEntity = session.CameraController == null ? null : session.CameraController.Entity;
            if (cameraEntity == null)
                return false;
            MatrixD fallback = cameraEntity.WorldMatrix;
            origin = fallback.Translation;
            direction = fallback.Forward;
            if (direction.LengthSquared() <= 1e-8)
                return false;
            direction.Normalize();
            return true;
        }

        // LCD panels: selected rotation; other providers: the surface's index. Physics-less grids are allowed: the
        // registering client already decided the screen is usable (e.g. a mod's wrist pad spawned as a physics-less
        // preview grid).
        static int ResolveSurfaceIndex(IMyCubeBlock block, IMyTextSurface surface)
        {
            if (block == null || surface == null || block.CubeGrid == null)
                return -1;
            if (block is IMyTextPanel)
            {
                foreach (var component in block.Components)
                {
                    var lcd = component as IMyLcdSurfaceComponent;
                    if (lcd != null)
                        return lcd.SelectedRotationIndex;
                }
                return -1;
            }

            var provider = block as Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;
            if (provider == null)
                return -1;
            for (int i = 0; i < provider.SurfaceCount; i++)
                if (provider.GetSurface(i).Name == surface.Name)
                    return i;
            return -1;
        }
    }
}
