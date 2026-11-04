using System;
using System.Collections.Generic;
using Acrux.Render3D.Blocks;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Acrux.Render3D.Missiles
{
    /// <summary>
    /// Missiles in flight around a scene, sampled on the game thread: each one's recent trail, and the path it is
    /// predicted to take to the grid it is closing on (turning toward it at the rate it was seen turning, so a
    /// rocket that flies straight gets a straight line). Each draws in its launcher owner's
    /// <see cref="RelationPalette"/> color. Shared by the 3D views; each owns one.
    /// </summary>
    internal sealed class MissileTracks
    {
        const double TRAIL_SECONDS = 2.5d;
        const int MAXIMUM_TRAIL_POINTS = 48;
        const double SAMPLE_SECONDS = 0.05d;
        // How far back the turn rate is measured over.
        const double TURN_WINDOW_SECONDS = 0.3d;
        const double PREDICTION_SECONDS = 8d;
        const double PREDICTION_STEP_SECONDS = 0.1d;
        // A target is a grid ahead of the missile, within this angle of where it flies.
        const double TARGET_CONE_RADIANS = 50d * Math.PI / 180d;
        // Below this the missile is taken as flying straight.
        const double MINIMUM_TURN_RADIANS_PER_SECOND = 0.03d;
        const double MARKER_PIXELS = 7d;
        // The trail drawn behind a missile, fading out over this many metres.
        const double TRAIL_METERS = 30d;
        const int MAXIMUM_TRACKS = 64;

        struct TrailPoint
        {
            public Vector3D Position;
            public Vector3D Velocity;
            public DateTime Time;
        }

        sealed class Track
        {
            public readonly List<TrailPoint> Trail = new List<TrailPoint>();
            public long LauncherId;
            public Color Color;
            // How much farther it flies before it runs out of range; infinite when it has no range.
            public double RemainingMeters;
            public DateTime Seen;
            public readonly List<Vector3D> Prediction = new List<Vector3D>();
            public bool HasTarget;
        }

        readonly Dictionary<long, Track> _tracks = new Dictionary<long, Track>();
        readonly List<MyEntity> _missiles = new List<MyEntity>();
        readonly List<IMyEntity> _near = new List<IMyEntity>();
        readonly List<long> _gone = new List<long>();
        DateTime _lastSample;

        public int Count
        {
            get { return _tracks.Count; }
        }

        /// <summary>
        /// Game thread: records the missiles inside the sphere and predicts where they go; with only, just the
        /// missiles whose entity ids it holds.
        /// </summary>
        public void Sample(BoundingSphereD sphere, HashSet<long> only = null)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastSample).TotalSeconds < SAMPLE_SECONDS)
                return;
            _lastSample = now;
            if (MyAPIGateway.Missiles == null)
            {
                _tracks.Clear();
                return;
            }
            _missiles.Clear();
            MyAPIGateway.Missiles.GetAllMissilesInSphere(ref sphere, _missiles);
            AddEntityMissiles(sphere);
            for (int i = 0; i < _missiles.Count && i < MAXIMUM_TRACKS; i++)
            {
                var missile = _missiles[i] as IMyMissile;
                if (missile == null)
                    continue;
                var entity = (IMyEntity)missile;
                if (only != null && !only.Contains(entity.EntityId))
                    continue;
                Track track;
                if (!_tracks.TryGetValue(entity.EntityId, out track))
                {
                    var launcher = MyAPIGateway.Entities.GetEntityById(missile.LauncherId) as IMyCubeBlock;
                    track = new Track
                    {
                        LauncherId = missile.LauncherId,
                        Color = RelationPalette.ForMissile(launcher == null ? 0 : launcher.OwnerId)
                    };
                    _tracks[entity.EntityId] = track;
                }
                track.Seen = now;
                track.RemainingMeters = missile.MaxTrajectory <= 0f
                    ? double.PositiveInfinity
                    : Math.Max(0d, missile.MaxTrajectory - Vector3D.Distance(entity.GetPosition(), missile.Origin));
                track.Trail.Add(new TrailPoint
                {
                    Position = entity.GetPosition(),
                    Velocity = missile.LinearVelocity,
                    Time = now
                });
                while (track.Trail.Count > MAXIMUM_TRAIL_POINTS ||
                       (track.Trail.Count > 1 && (now - track.Trail[0].Time).TotalSeconds > TRAIL_SECONDS))
                    track.Trail.RemoveAt(0);
                Predict(track);
            }
            _missiles.Clear();

            _gone.Clear();
            foreach (KeyValuePair<long, Track> pair in _tracks)
                if (pair.Value.Seen != now)
                    _gone.Add(pair.Key);
            for (int i = 0; i < _gone.Count; i++)
                _tracks.Remove(_gone[i]);
        }

        // Seeking ammo (homing missiles, seeking torpedoes) also lives in the game's general entity index; a missile
        // the missile index does not return is taken from there.
        void AddEntityMissiles(BoundingSphereD sphere)
        {
            List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            if (entities == null)
                return;
            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i] as MyEntity;
                if (entity == null || !(entity is IMyMissile) || _missiles.Contains(entity))
                    continue;
                _missiles.Add(entity);
            }
            entities.Clear();
        }

        // Pure pursuit of the target's centre (moving at its own velocity), at the missile's speed and turn rate.
        void Predict(Track track)
        {
            track.Prediction.Clear();
            track.HasTarget = false;
            TrailPoint head = track.Trail[track.Trail.Count - 1];
            double speed = head.Velocity.Length();
            if (speed < 1d)
                return;
            Vector3D direction = head.Velocity / speed;
            IMyCubeGrid target = FindTarget(track, head, direction, speed);
            double turnRate = TurnRate(track);
            Vector3D position = head.Position;
            track.Prediction.Add(position);
            double reach = Math.Min(speed * PREDICTION_SECONDS, track.RemainingMeters);
            if (target == null)
            {
                track.Prediction.Add(position + direction * reach);
                return;
            }
            track.HasTarget = true;
            BoundingSphereD volume = target.WorldVolume;
            Vector3D targetPosition = volume.Center;
            Vector3D targetVelocity = target.Physics == null ? Vector3D.Zero : (Vector3D)target.Physics.LinearVelocity;
            double step = PREDICTION_STEP_SECONDS;
            double closest = double.MaxValue;
            double flown = 0d;
            for (double t = 0d; t < PREDICTION_SECONDS; t += step)
            {
                // Out of range: the path ends where the missile does.
                if (flown + speed * step > reach)
                {
                    track.Prediction.Add(position + direction * (reach - flown));
                    return;
                }
                flown += speed * step;
                targetPosition += targetVelocity * step;
                Vector3D toTarget = targetPosition - position;
                double distance = toTarget.Length();
                if (distance <= Math.Max(volume.Radius * 0.5d, speed * step))
                {
                    track.Prediction.Add(targetPosition);
                    return;
                }
                // Moving away again: it missed.
                if (distance > closest + speed * step)
                    return;
                closest = Math.Min(closest, distance);
                if (turnRate > 0d)
                    direction = TurnToward(direction, toTarget / distance, turnRate * step);
                position += direction * speed * step;
                track.Prediction.Add(position);
            }
        }

        // The grid ahead of the missile it is most nearly flying at, not its own launcher's.
        IMyCubeGrid FindTarget(Track track, TrailPoint head, Vector3D direction, double speed)
        {
            var launcher = MyAPIGateway.Entities.GetEntityById(track.LauncherId) as IMyCubeBlock;
            IMyCubeGrid own = launcher == null ? null : launcher.CubeGrid;
            var reach = new BoundingSphereD(head.Position, Math.Max(200d, speed * PREDICTION_SECONDS));
            _near.Clear();
            _near.AddRange(MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref reach));
            IMyCubeGrid best = null;
            double bestAngle = TARGET_CONE_RADIANS;
            for (int i = 0; i < _near.Count; i++)
            {
                var grid = _near[i] as IMyCubeGrid;
                if (grid == null || grid.MarkedForClose || grid.Physics == null ||
                    (own != null && grid.IsInSameLogicalGroupAs(own)))
                    continue;
                BoundingSphereD volume = grid.WorldVolume;
                Vector3D toGrid = volume.Center - head.Position;
                double distance = toGrid.Length();
                if (distance < 1e-3d)
                    return grid;
                double angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(toGrid / distance, direction), -1d, 1d)) -
                               Math.Asin(Math.Min(1d, volume.Radius / distance));
                if (angle < bestAngle)
                {
                    bestAngle = angle;
                    best = grid;
                }
            }
            _near.Clear();
            return best;
        }

        static double TurnRate(Track track)
        {
            List<TrailPoint> trail = track.Trail;
            TrailPoint head = trail[trail.Count - 1];
            for (int i = trail.Count - 2; i >= 0; i--)
            {
                double seconds = (head.Time - trail[i].Time).TotalSeconds;
                if (seconds < TURN_WINDOW_SECONDS && i > 0)
                    continue;
                if (seconds <= 1e-3d)
                    return 0d;
                double a = head.Velocity.Length();
                double b = trail[i].Velocity.Length();
                if (a < 1d || b < 1d)
                    return 0d;
                double angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(head.Velocity / a, trail[i].Velocity / b), -1d, 1d));
                double rate = angle / seconds;
                return rate < MINIMUM_TURN_RADIANS_PER_SECOND ? 0d : rate;
            }
            return 0d;
        }

        static Vector3D TurnToward(Vector3D direction, Vector3D wanted, double maximum)
        {
            double angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(direction, wanted), -1d, 1d));
            if (angle <= maximum || angle < 1e-6d)
                return wanted;
            Vector3D axis = Vector3D.Cross(direction, wanted);
            if (axis.Normalize() < 1e-9d)
                return direction;
            return Vector3D.Normalize(Vector3D.TransformNormal(direction, MatrixD.CreateFromAxisAngle(axis, maximum)));
        }

        /// <summary>
        /// Game thread: each missile as a small diamond, a short trail fading out behind it, and its predicted path
        /// ahead as one curve, in scene coordinates.
        /// </summary>
        public void AddLines(List<SceneLine> lines, SceneCamera camera, MatrixD worldToScene)
        {
            foreach (Track track in _tracks.Values)
            {
                List<TrailPoint> trail = track.Trail;
                if (trail.Count == 0)
                    continue;
                Color color = track.Color;
                Vector3D head = trail[trail.Count - 1].Position;
                AddTrail(lines, trail, worldToScene, color);
                List<Vector3D> path = track.Prediction;
                for (int i = 1; i < path.Count; i++)
                    lines.Add(new SceneLine
                    {
                        From = Vector3D.Transform(path[i - 1], worldToScene),
                        To = Vector3D.Transform(path[i], worldToScene),
                        Color = color
                    });
                AddDiamond(lines, camera, Vector3D.Transform(head, worldToScene), color);
            }
        }

        // Back from the head, darker with the distance, until TRAIL_METERS.
        static void AddTrail(List<SceneLine> lines, List<TrailPoint> trail, MatrixD worldToScene, Color color)
        {
            double travelled = 0d;
            for (int i = trail.Count - 1; i > 0 && travelled < TRAIL_METERS; i--)
            {
                Vector3D from = trail[i].Position;
                Vector3D to = trail[i - 1].Position;
                double length = Vector3D.Distance(from, to);
                if (length < 1e-3d)
                    continue;
                if (travelled + length > TRAIL_METERS)
                {
                    to = from + (to - from) * ((TRAIL_METERS - travelled) / length);
                    length = TRAIL_METERS - travelled;
                }
                Color faded = color * (float)(1d - (travelled + length * .5d) / TRAIL_METERS);
                faded.A = 255;
                lines.Add(new SceneLine
                {
                    From = Vector3D.Transform(from, worldToScene),
                    To = Vector3D.Transform(to, worldToScene),
                    Color = faded
                });
                travelled += length;
            }
        }

        static void AddDiamond(List<SceneLine> lines, SceneCamera camera, Vector3D at, Color color)
        {
            double size = Vector3D.Distance(at, camera.Position) * MARKER_PIXELS / camera.FocalLengthPixels;
            Vector3D right = camera.Right * size;
            Vector3D up = camera.Up * size;
            Vector3D toward = camera.Forward * size;
            var corners = new[] { at + right, at + up, at - right, at - up, at + toward, at - toward };
            for (int c = 0; c < 4; c++)
            {
                lines.Add(new SceneLine { From = corners[c], To = corners[(c + 1) % 4], Color = color });
                lines.Add(new SceneLine { From = corners[c], To = corners[4], Color = color });
                lines.Add(new SceneLine { From = corners[c], To = corners[5], Color = color });
            }
        }

        public void Clear()
        {
            _tracks.Clear();
        }
    }
}
