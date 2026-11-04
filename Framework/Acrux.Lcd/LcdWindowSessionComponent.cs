using Sandbox.ModAPI;
using VRage.Game.Components;

namespace Acrux.Lcd
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public sealed class LcdWindowSessionComponent : MySessionComponentBase
    {
        public override void UpdateBeforeSimulation()
        {
            if (!MyAPIGateway.Utilities.IsDedicated)
                LcdWindowManager.Update();
        }

        public override void Draw()
        {
            if (!MyAPIGateway.Utilities.IsDedicated)
                LcdWindowManager.Draw();
        }

        protected override void UnloadData()
        {
            LcdWindowManager.Clear();
        }
    }
}
