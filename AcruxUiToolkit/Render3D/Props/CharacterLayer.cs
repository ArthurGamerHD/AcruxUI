using System.Collections.Generic;
using Acrux.Render3D.Models;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Acrux.Render3D.Props
{
    /// <summary>
    /// Characters standing or flying free in the region (players, and bots such as wolves and spiders), as their
    /// models in their bind pose, the suit painted with the relation color of whoever controls them. A character
    /// seated in a cockpit is part of its grid's hierarchy, not a top-most entity, and is left out with it; so are
    /// dead ones.
    /// </summary>
    internal sealed class CharacterLayer : PropLayer
    {
        readonly List<IMyEntity> _entities = new List<IMyEntity>();

        protected override string PendingDescription
        {
            get { return "character models"; }
        }

        // Game thread. Only the characters with these ids when capture.Only is set.
        public void Capture(ref SceneCapture capture)
        {
            BeginCapture(ref capture, Render3DSettings.Current.CharacterTriangles);
            var sphere = new BoundingSphereD(capture.CenterWorld, capture.Radius);
            _entities.AddRange(MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere));
            for (int e = 0; e < _entities.Count; e++)
            {
                var character = _entities[e] as IMyCharacter;
                if (character == null || character.MarkedForClose || character.Closed || character.IsDead ||
                    (capture.Only != null && !capture.Only.Contains(character.EntityId)))
                    continue;
                MeshAsset model = Resolve(character.Model == null ? null : character.Model.AssetName);
                if (model == null)
                    continue;
                var controller = character.ControllerInfo;
                long identity = controller == null ? 0 : controller.ControllingIdentityId;
                Vector3 paint = RelationPalette.ForOwner(identity).ToVector3();
                MatrixD toScene = character.WorldMatrix * capture.WorldToScene;
                Add(model, ref toScene, paint, PropTint.Painted);
            }
            _entities.Clear();
            EndCapture();
        }
    }
}
