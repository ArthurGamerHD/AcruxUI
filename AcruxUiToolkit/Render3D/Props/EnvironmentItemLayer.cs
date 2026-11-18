using System;
using System.Collections.Generic;
using Acrux.Render3D.Models;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.WorldEnvironment;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace Acrux.Render3D.Props
{
    /// <summary>
    /// A planet's environment items of one kind, as the game places them: every environment sector the planet has
    /// loaded around the region (children of the planet entity), each item at its sector's centre plus its own
    /// offset and rotation. An item whose model index is negative was removed (a tree cut down, a plant picked)
    /// and is left out, as the game's own instance buffers leave it. Sectors only exist where the game streams
    /// them in, near players and cameras; farther out the planet's terrain draws bare.
    /// </summary>
    internal abstract class EnvironmentItemLayer : PropLayer
    {
        readonly List<IMyEntity> _entities = new List<IMyEntity>();
        readonly List<MyEntity> _sectors = new List<MyEntity>();
        // By model definition: whether this layer draws it (asked once per definition).
        readonly Dictionary<MyPhysicalModelDefinition, bool> _accepted =
            new Dictionary<MyPhysicalModelDefinition, bool>();

        // Whether this layer draws items of this model.
        protected abstract bool Accepts(MyPhysicalModelDefinition model);

        protected abstract int TriangleBudget { get; }

        // Game thread. The items of every planet within the region.
        public void Capture(ref SceneCapture capture)
        {
            BeginCapture(ref capture, TriangleBudget);
            var sphere = new BoundingSphereD(capture.CenterWorld, capture.Radius);
            _entities.AddRange(MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere));
            for (int e = 0; e < _entities.Count; e++)
            {
                var planet = _entities[e] as MyPlanet;
                if (planet != null && !planet.MarkedForClose && planet.Hierarchy != null)
                    AddPlanet(planet, ref capture, ref sphere);
            }
            _entities.Clear();
            EndCapture();
        }

        void AddPlanet(MyPlanet planet, ref SceneCapture capture, ref BoundingSphereD sphere)
        {
            _sectors.Clear();
            planet.Hierarchy.QuerySphere(ref sphere, _sectors);
            double radiusSquared = sphere.Radius * sphere.Radius;
            for (int s = 0; s < _sectors.Count; s++)
            {
                var sector = _sectors[s] as MyEnvironmentSector;
                if (sector == null || sector.IsClosed || sector.MarkedForClose)
                    continue;
                try
                {
                    AddSector(sector, ref capture, ref sphere, radiusSquared);
                }
                catch (Exception)
                {
                    // A sector switching its detail on the game's worker swaps its data view under us: it is
                    // read again whole next frame.
                }
            }
            _sectors.Clear();
        }

        void AddSector(MyEnvironmentSector sector, ref SceneCapture capture, ref BoundingSphereD sphere,
            double radiusSquared)
        {
            MyEnvironmentDataView view = sector.DataView;
            IMyEnvironmentOwner owner = sector.Owner;
            if (view == null || owner == null)
                return;
            IReadOnlyList<ItemInfo> items = view.Items;
            if (items == null)
                return;
            Vector3D sectorCenter = sector.SectorCenter;
            MatrixD worldToScene = capture.WorldToScene;
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                ItemInfo item = items[i];
                if (item.ModelIndex < 0)
                    continue;
                Vector3D world = sectorCenter + item.Position;
                if (Vector3D.DistanceSquared(world, sphere.Center) > radiusSquared)
                    continue;
                MyPhysicalModelDefinition definition = owner.GetModelForId(item.ModelIndex);
                if (definition == null || !IsAccepted(definition))
                    continue;
                MeshAsset model = Resolve(definition.Model);
                if (model == null)
                    continue;
                MatrixD itemToWorld = MatrixD.CreateFromQuaternion(item.Rotation);
                itemToWorld.Translation = world;
                MatrixD toScene = itemToWorld * worldToScene;
                Add(model, ref toScene, Vector3.One, PropTint.Textured);
            }
        }

        bool IsAccepted(MyPhysicalModelDefinition definition)
        {
            bool accepted;
            if (!_accepted.TryGetValue(definition, out accepted))
            {
                accepted = !string.IsNullOrEmpty(definition.Model) && Accepts(definition);
                _accepted[definition] = accepted;
            }
            return accepted;
        }
    }

    /// <summary>Trees and bushes: every environment item that is not foraged.</summary>
    internal sealed class VegetationLayer : EnvironmentItemLayer
    {
        protected override string PendingDescription
        {
            get { return "vegetation models"; }
        }

        protected override int TriangleBudget
        {
            get { return Render3DSettings.Current.VegetationTriangles; }
        }

        protected override bool Accepts(MyPhysicalModelDefinition model)
        {
            return !(model is MyForageableDefinition);
        }
    }

    /// <summary>The plants, mushrooms and other items a player can forage, until they are picked.</summary>
    internal sealed class ForageableLayer : EnvironmentItemLayer
    {
        protected override string PendingDescription
        {
            get { return "forageable models"; }
        }

        protected override int TriangleBudget
        {
            get { return Render3DSettings.Current.ForageableTriangles; }
        }

        protected override bool Accepts(MyPhysicalModelDefinition model)
        {
            return model is MyForageableDefinition;
        }
    }
}
