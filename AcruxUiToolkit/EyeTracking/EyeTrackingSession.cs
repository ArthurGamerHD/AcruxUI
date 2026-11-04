using Generated;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace LcdEyeTrackingApi
{
    [APIManager(0x4C43444559450001L, Provider = nameof(_api))]
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public sealed partial class EyeTrackingSession : MySessionComponentBase
    {
        ScreenTracker _tracker;
        EyeTrackingApiServer _api;

        public override void LoadData()
        {
            _tracker = new ScreenTracker();
            _api = new EyeTrackingApiServer(_tracker);
            _tracker.Load();
            RegisterApiManager();
        }

        protected override void UnloadData()
        {
            UnregisterApiManager();
            if (_tracker != null)
                _tracker.Unload();
            _tracker = null;
            _api = null;
        }

        public override void UpdateBeforeSimulation()
        {
            if (_tracker != null && MyAPIGateway.Utilities != null && !MyAPIGateway.Utilities.IsDedicated)
                _tracker.Update();
        }
    }
}
