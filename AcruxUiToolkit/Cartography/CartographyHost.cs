using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace Acrux.Cartography
{
    /// <summary>
    /// Services the shared cartography sources depend on. The toolkit hosts one cartography engine for every
    /// client mod, so planet maps are built (and cached) once per session.
    /// </summary>
    internal static class CartographyHost
    {
        // "ACRUX"; derives the toolkit's private reply channel for the Voxel Cubemap API.
        public const long CommunicationId = 0x4143525558L;

        static readonly HashSet<IMyEntity> Entities = new HashSet<IMyEntity>();
        static readonly Dictionary<long, MyPlanet> Planets = new Dictionary<long, MyPlanet>();
        static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
        static DateTime _refreshedAt;

        public static Dictionary<long, MyPlanet> PlanetsById
        {
            get
            {
                lock (Planets)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now - _refreshedAt < RefreshInterval)
                        return Planets;
                    _refreshedAt = now;
                    Entities.Clear();
                    Planets.Clear();
                    if (MyAPIGateway.Entities != null)
                    {
                        MyAPIGateway.Entities.GetEntities(Entities, entity => entity is MyPlanet);
                        foreach (IMyEntity entity in Entities)
                        {
                            var planet = entity as MyPlanet;
                            if (planet != null && !planet.MarkedForClose)
                                Planets[planet.EntityId] = planet;
                        }
                        Entities.Clear();
                    }
                    return Planets;
                }
            }
        }

        public static void LogError(Exception error, Type source)
        {
            MyLog.Default.WriteLineAndConsole(string.Format(
                "Acrux UI: cartography error in {0}: {1}",
                source == null ? "unknown" : source.FullName,
                error));
        }
    }
}
