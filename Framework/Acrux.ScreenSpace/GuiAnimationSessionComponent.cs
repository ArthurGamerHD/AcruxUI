using System;
using System.Collections.Generic;
using Acrux.Animation;
using Acrux.Controls;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace Acrux.ScreenSpace
{
    /// <summary>
    /// Space Engineers host for Acrux's renderer-independent animation clock.
    /// The animation implementation remains in Acrux; only frame scheduling
    /// and redraw notification are owned by this game-specific component.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public sealed class GuiAnimationSessionComponent : MySessionComponentBase
    {
        readonly List<Action> _nextFrameCallbacks = new List<Action>();
        readonly List<Action> _executingCallbacks = new List<Action>();
        AnimationController _animations;

        public static GuiAnimationSessionComponent Instance { get; private set; }

        public static AnimationController Animations
        {
            get { return Instance == null ? null : Instance._animations; }
        }

        /// <summary>
        /// Raised after an animation changes a value. Screen-space hosts may use
        /// this to invalidate cached output or request an immediate redraw.
        /// </summary>
        public static event Action RedrawRequested;

        public override void LoadData()
        {
            Instance = this;
            _animations = new AnimationController(
                GetCurrentFrame,
                ScheduleNextFrame,
                RequestRedraw);
            AnimationServices.DefaultController = _animations;
            if (WindowPlatforms.Default == null)
                WindowPlatforms.Default = ScreenSpaceWindowPlatform.Instance;
        }

        public override void UpdateBeforeSimulation()
        {
            if (_nextFrameCallbacks.Count > 0)
            {
                _executingCallbacks.AddRange(_nextFrameCallbacks);
                _nextFrameCallbacks.Clear();
                try
                {
                    for (int i = 0; i < _executingCallbacks.Count; i++)
                        _executingCallbacks[i]();
                }
                finally
                {
                    _executingCallbacks.Clear();
                }
            }

            ScreenSpaceWindowManager.Update();
        }

        public override void Draw()
        {
            ScreenSpaceWindowManager.Draw();
        }

        protected override void UnloadData()
        {
            if (ReferenceEquals(AnimationServices.DefaultController, _animations))
                AnimationServices.DefaultController = null;

            if (_animations != null)
                _animations.Dispose();
            _animations = null;
            _nextFrameCallbacks.Clear();
            _executingCallbacks.Clear();
            ScreenSpaceWindowManager.Clear();
            if (ReferenceEquals(WindowPlatforms.Default, ScreenSpaceWindowPlatform.Instance))
                WindowPlatforms.Default = null;
            RedrawRequested = null;
            Instance = null;
        }

        long GetCurrentFrame()
        {
            return MyAPIGateway.Session.GameplayFrameCounter;
        }

        void ScheduleNextFrame(Action callback)
        {
            if (callback != null)
                _nextFrameCallbacks.Add(callback);
        }

        static void RequestRedraw()
        {
            Action handler = RedrawRequested;
            if (handler != null)
                handler();
        }
    }
}
