using System;
using System.Collections.Generic;
using Acrux.Cartography.Maps.Planet3D;
using Acrux.Render3D;
using Acrux.Render3D.Blocks;
using Acrux.Render3D.Missiles;
using Acrux.Render3D.Models;
using Acrux.Render3D.Voxels;
using Acrux.Rendering;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace Acrux.Cartography.Maps
{
    // The 3D surface view: past the Planet Map's maximum zoom the flat globe hands over to an orbital camera
    // over displaced terrain, and the Star Map's first-person camera draws the planet it is inside the same way.
    public sealed partial class CartographyMapRenderer
    {
        /// <summary>Closest the orbital camera gets to its target, in metres.</summary>
        public const float SURFACE_VIEW_MINIMUM_DISTANCE = 60f;
        /// <summary>Lowest camera elevation above the horizon, in radians; a quarter turn looks straight down.</summary>
        public const float SURFACE_VIEW_MINIMUM_PITCH = 0.14f;
        const float SURFACE_VIEW_ZOOM_STEP = 1.25f;
        const float SURFACE_VIEW_ORBIT_SENSITIVITY = 0.009f;
        const double SURFACE_VIEW_GROUND_CLEARANCE_METERS = 12d;
        // Terrain takes over from the smooth sphere at the edge of the planet's gravity well, and relief fades
        // into the sphere between this fraction of the well's altitude and the whole of it, as distance from
        // the camera. Entering the well the nearest ground is already that far, so the hand-over is invisible.
        const double SPHERIZE_START_FRACTION = 0.3d;
        const double HUD_NEAR_DEPTH_METERS = 0.05d;
        // A planet without a readable gravity well uses this fraction of its radius as the well's altitude.
        const double FALLBACK_WELL_ALTITUDE_RATIO = 0.5d;
        // Gravity at the well's edge (MySphericalNaturalGravityComponent: where it falls to 0.05 g).
        const double WELL_EDGE_GRAVITY = 0.05d;
        // The surface view starts flat, like the globe it opens from, and its relief grows in over this
        // fraction of the entry distance.
        const double SURFACE_VIEW_RELIEF_FADE = 0.5d;

        struct SurfaceView
        {
            public MyPlanet Planet;
            public PlanetSnapshot Snapshot;
            public PlanetHeightCubemap Heights;
            // Planet-local metres.
            public SceneCamera Camera;
            public Vector3 TargetDirection;
            public Vector3D Target;
            // Ground-plane axes at the target: where the camera's right and forward point along the surface.
            public Vector3 GroundRight;
            public Vector3 GroundForward;
        }

        // Pooled optional layers; a frame still painting keeps its own and the next gets fresh ones.
        // The layers the frame being prepared draws, in order, for its loading report.
        readonly List<ISceneLayer> _frameLayers = new List<ISceneLayer>();
        // The last prepared frame drew a 3D scene, and found part of it still waiting on the worker.
        bool _frameHas3D;
        bool _sceneLoading;
        // The HUD's atmosphere, added after every other command of the Star Map frame.
        ISceneLayer _hudAtmosphere;
        readonly Dictionary<long, PlanetAtmosphereData> _atmospheres = new Dictionary<long, PlanetAtmosphereData>();
        // Voxel capture radius around the view, in camera-to-target distances, and its bounds in metres.
        const double VOXEL_SCENE_DISTANCE_FACTOR = 3d;
        const double VOXEL_SCENE_MINIMUM_RADIUS = 500d;
        const double VOXEL_SCENE_MAXIMUM_RADIUS = 30000d;

        PlanetHeightCubemap FindHeights(long planetId, CartographyLayer preferred)
        {
            PlanetColorCubemap cubemap = GetCubemap(planetId, preferred);
            if (cubemap != null && cubemap.Heights != null)
                return cubemap.Heights;
            // Heights do not depend on the layer; any full-resolution cubemap of the planet carries them.
            foreach (KeyValuePair<CubemapKey, PlanetColorCubemap> pair in _cubemaps)
            {
                if (pair.Key.PlanetId == planetId && pair.Value != null && pair.Value.Heights != null)
                    return pair.Value.Heights;
            }
            return null;
        }

        // Pooled per frame slot: a frame in flight keeps its slot's scenes, the next frame uses the other slot.
        TerrainScene RentTerrainScene(int index)
        {
            List<TerrainScene> scenes = _slot.TerrainScenes;
            while (scenes.Count <= index)
                scenes.Add(new TerrainScene());
            return scenes[index];
        }

        AtmosphereScene RentAtmosphereScene(int index)
        {
            List<AtmosphereScene> scenes = _slot.AtmosphereScenes;
            while (scenes.Count <= index)
                scenes.Add(new AtmosphereScene());
            return scenes[index];
        }

        /// <summary>True when the focused planet's terrain heights are loaded, so the 3D surface view can open.</summary>
        public bool IsPlanetSurfaceViewAvailable(MapData data, MapViewState state)
        {
            PlanetSnapshot snapshot = ResolvePlanet(data, state);
            return state != null && snapshot != null && snapshot.Planet != null &&
                   !snapshot.Planet.MarkedForClose &&
                   FindHeights(snapshot.EntityId, state.Layer) != null;
        }

        /// <summary>
        /// Wheel input for the Planet Map. At the maximum zoom a further step in opens the 3D surface view at
        /// the distance that shows the same ground as the globe did; inside it the wheel moves the camera, and
        /// stepping back out past that distance returns to the globe, unless <see cref="MapViewState.SurfaceViewOnly"/>
        /// holds the camera there. False leaves the wheel to the globe zoom.
        /// </summary>
        public bool ScrollPlanetSurfaceView(MapData data, MapViewState state, int delta)
        {
            if (state == null || delta == 0)
                return false;

            PlanetSnapshot snapshot = ResolvePlanet(data, state);
            MyPlanet planet = snapshot == null ? null : snapshot.Planet;
            PlanetHeightCubemap heights = planet == null || planet.MarkedForClose
                ? null
                : FindHeights(snapshot.EntityId, state.Layer);
            if (heights == null)
            {
                if (!state.SurfaceViewActive)
                    return false;
                ExitPlanetSurfaceView(state);
                return true;
            }

            Vector3 view;
            Vector3 right;
            Vector3 up;
            BuildProjection(planet, data, state, out view, out right, out up);
            float entry = (float)GetSurfaceViewEntryDistance(heights.SampleRadius(view), state);
            if (!state.SurfaceViewActive)
            {
                if (state.SurfaceViewOnly)
                {
                    state.SurfaceViewActive = true;
                    state.SurfaceViewDistance = entry;
                    return true;
                }
                if (delta < 0 || state.Zoom < state.MaximumZoom - 0.001f)
                    return false;
                state.SurfaceViewActive = true;
                state.SurfaceViewDistance = entry;
                state.SurfaceViewYaw = 0f;
                state.SurfaceViewPitch = MathHelper.PiOver2;
                return true;
            }

            float distance = IsFinite(state.SurfaceViewDistance) && state.SurfaceViewDistance > 0f
                ? state.SurfaceViewDistance
                : entry;
            float next = delta > 0 ? distance / SURFACE_VIEW_ZOOM_STEP : distance * SURFACE_VIEW_ZOOM_STEP;
            if (delta < 0 && next > entry * 1.001f && !state.SurfaceViewOnly)
            {
                ExitPlanetSurfaceView(state);
                return true;
            }

            state.SurfaceViewDistance = MathHelper.Clamp(
                next, Math.Min(SURFACE_VIEW_MINIMUM_DISTANCE, entry), entry);
            return true;
        }

        /// <summary>
        /// Opens the 3D surface view at the distance that shows the same ground as the globe at its maximum zoom,
        /// keeping the current camera yaw and pitch. False while the focused planet's terrain heights are loading.
        /// </summary>
        public bool EnterPlanetSurfaceView(MapData data, MapViewState state)
        {
            if (state == null)
                return false;
            if (state.SurfaceViewActive)
                return true;
            PlanetSnapshot snapshot = ResolvePlanet(data, state);
            MyPlanet planet = snapshot == null ? null : snapshot.Planet;
            PlanetHeightCubemap heights = planet == null || planet.MarkedForClose
                ? null
                : FindHeights(snapshot.EntityId, state.Layer);
            if (heights == null)
                return false;
            Vector3 view;
            Vector3 right;
            Vector3 up;
            BuildProjection(planet, data, state, out view, out right, out up);
            state.SurfaceViewActive = true;
            state.SurfaceViewDistance = (float)GetSurfaceViewEntryDistance(heights.SampleRadius(view), state);
            return true;
        }

        /// <summary>Returns the Planet Map to its flat globe.</summary>
        public void ExitPlanetSurfaceView(MapViewState state)
        {
            if (state == null)
                return;
            state.SurfaceViewActive = false;
            state.SurfaceViewYaw = 0f;
            state.SurfaceViewPitch = MathHelper.PiOver2;
        }

        /// <summary>
        /// Drag input for the 3D surface view, in pixels: horizontal turns the camera around the target,
        /// vertical tilts it between straight down and the horizon. False when the view is not open.
        /// </summary>
        public bool OrbitPlanetSurfaceView(MapViewState state, Vector2 delta)
        {
            if (state == null || !state.SurfaceViewActive)
                return false;
            state.SurfaceViewYaw = MathHelper.WrapAngle(
                state.SurfaceViewYaw - delta.X * SURFACE_VIEW_ORBIT_SENSITIVITY);
            state.SurfaceViewPitch = MathHelper.Clamp(
                state.SurfaceViewPitch - delta.Y * SURFACE_VIEW_ORBIT_SENSITIVITY,
                SURFACE_VIEW_MINIMUM_PITCH,
                MathHelper.PiOver2);
            return true;
        }

        /// <summary>
        /// Slides the 3D surface view's target along the ground so the terrain follows the pointer. The map must
        /// not be following the player, or the next update moves the target back.
        /// </summary>
        public bool PanPlanetSurfaceView(MapData data, MapViewState state, Vector2 delta, float canvasShortSide)
        {
            SurfaceView surface;
            int side = Math.Max(1, (int)canvasShortSide);
            if (!TryBuildSurfaceView(data, state, side, side, out surface))
                return false;

            double metresPerPixel = state.SurfaceViewDistance / surface.Camera.FocalLengthPixels;
            double forward = delta.Y * metresPerPixel / Math.Max(0.2d, Math.Sin(state.SurfaceViewPitch));
            double sideways = -delta.X * metresPerPixel;
            double radius = Math.Max(1d, surface.Target.Length());
            float sine = (float)Math.Sin(state.SurfaceViewYaw);
            float cosine = (float)Math.Cos(state.SurfaceViewYaw);
            // The same motion in the globe's screen axes, as angles on the sphere.
            float towardRight = (float)((forward * sine + sideways * cosine) / radius);
            float towardUp = (float)((forward * cosine - sideways * sine) / radius);

            Vector3 view = state.PlanetOrbitView;
            Vector3 right = state.PlanetOrbitRight;
            Vector3 up = state.PlanetOrbitUp;
            NormalizeOrbitBasis(ref view, ref right, ref up);
            Vector3 turned = view * (float)Math.Cos(towardUp) + up * (float)Math.Sin(towardUp);
            up = up * (float)Math.Cos(towardUp) - view * (float)Math.Sin(towardUp);
            view = turned;
            turned = view * (float)Math.Cos(towardRight) + right * (float)Math.Sin(towardRight);
            right = right * (float)Math.Cos(towardRight) - view * (float)Math.Sin(towardRight);
            view = turned;
            NormalizeOrbitBasis(ref view, ref right, ref up);
            state.PlanetOrbitView = view;
            state.PlanetOrbitRight = right;
            state.PlanetOrbitUp = up;
            return true;
        }

        // How much of the terrain's relief the surface view shows: none where it opens from the flat globe, all
        // of it once the camera has come SURFACE_VIEW_RELIEF_FADE of the way in.
        static float SurfaceViewRelief(MapViewState state, SurfaceView surface)
        {
            if (state.SurfaceViewOnly)
                return 1f;
            double distance = Vector3D.Distance(surface.Camera.Position, surface.Target);
            double entry = GetSurfaceViewEntryDistance(surface.Target.Length(), state);
            return (float)MathHelper.Clamp((entry - distance) / (entry * SURFACE_VIEW_RELIEF_FADE), 0d, 1d);
        }

        // As TerrainScene draws it near the camera: relief above the planet's sphere scaled, the rest as it is.
        static double DrawnGroundRadius(PlanetHeightCubemap heights, Vector3 direction, double sphere, float relief)
        {
            double radius = heights.SampleRadius(direction);
            return sphere <= 0d || radius <= sphere ? radius : sphere + (radius - sphere) * relief;
        }

        /// <summary>
        /// How far from the focused planet's center the 3D surface view draws the ground in a planet-local
        /// direction: the terrain's height there, flattened toward the planet's sphere while the view's relief
        /// is still growing in. With <see cref="TryGetPlanetSurfaceViewCamera"/> it stands a point on the drawn
        /// ground. False while the surface view is not open or the direction is not finite.
        /// </summary>
        public bool TryGetPlanetSurfaceViewGroundRadius(MapData data, MapViewState state, Vector3 localDirection,
            out double radius)
        {
            radius = 0d;
            SurfaceView surface;
            if (!IsFinite(localDirection.X) || !IsFinite(localDirection.Y) || !IsFinite(localDirection.Z) ||
                localDirection.Normalize() <= VECTOR_EPSILON || !TryBuildSurfaceView(data, state, 1, 1, out surface))
                return false;
            radius = DrawnGroundRadius(surface.Heights, localDirection, surface.Planet.AverageRadius,
                SurfaceViewRelief(state, surface));
            return true;
        }

        /// <summary>
        /// <see cref="TryGetPlanetSurfaceViewGroundRadius"/> for the first <paramref name="count"/> of many
        /// planet-local unit directions in one call, into <paramref name="radii"/> (at least as long).
        /// </summary>
        public bool TryGetPlanetSurfaceViewGroundRadii(MapData data, MapViewState state, Vector3[] localDirections,
            int count, double[] radii)
        {
            SurfaceView surface;
            if (localDirections == null || radii == null || count < 0 || count > localDirections.Length ||
                count > radii.Length || !TryBuildSurfaceView(data, state, 1, 1, out surface))
                return false;
            double sphere = surface.Planet.AverageRadius;
            float relief = SurfaceViewRelief(state, surface);
            for (int i = 0; i < count; i++)
                radii[i] = DrawnGroundRadius(surface.Heights, localDirections[i], sphere, relief);
            return true;
        }

        // The camera distance at which the perspective view shows the target at the globe's maximum-zoom scale.
        static double GetSurfaceViewEntryDistance(double targetRadius, MapViewState state)
        {
            double zoom = Math.Max(0.0001d, state.MaximumZoom);
            return targetRadius / (2d * Math.Tan(STAR_VERTICAL_FOV_RADIANS * 0.5d) * 0.47d * zoom);
        }

        static Vector3D WorldToPlanetLocal(MyPlanet planet, Vector3D worldPosition)
        {
            MatrixD world = planet.WorldMatrix;
            Vector3D offset = worldPosition - world.Translation;
            return new Vector3D(
                Vector3D.Dot(offset, world.Right),
                Vector3D.Dot(offset, world.Up),
                Vector3D.Dot(offset, world.Backward));
        }

        static Vector3D PlanetLocalToWorld(MyPlanet planet, Vector3D localPosition)
        {
            MatrixD world = planet.WorldMatrix;
            return world.Translation +
                   world.Right * localPosition.X +
                   world.Up * localPosition.Y +
                   world.Backward * localPosition.Z;
        }

        bool TryBuildSurfaceView(MapData data, MapViewState state, int width, int height, out SurfaceView surface)
        {
            surface = default(SurfaceView);
            if (state == null || !state.SurfaceViewActive || width <= 0 || height <= 0)
                return false;
            PlanetSnapshot snapshot = ResolvePlanet(data, state);
            MyPlanet planet = snapshot == null ? null : snapshot.Planet;
            if (planet == null || planet.MarkedForClose)
                return false;
            PlanetHeightCubemap heights = FindHeights(snapshot.EntityId, state.Layer);
            if (heights == null)
                return false;

            Vector3 view;
            Vector3 right;
            Vector3 up;
            BuildProjection(planet, data, state, out view, out right, out up);

            double targetRadius = heights.SampleRadius(view);
            double entry = GetSurfaceViewEntryDistance(targetRadius, state);
            double distance = IsFinite(state.SurfaceViewDistance) && state.SurfaceViewDistance > 0f
                ? Math.Min(state.SurfaceViewDistance, entry)
                : entry;
            float pitch = IsFinite(state.SurfaceViewPitch)
                ? MathHelper.Clamp(state.SurfaceViewPitch, SURFACE_VIEW_MINIMUM_PITCH, MathHelper.PiOver2)
                : MathHelper.PiOver2;
            float yaw = IsFinite(state.SurfaceViewYaw) ? state.SurfaceViewYaw : 0f;

            float sineYaw = (float)Math.Sin(yaw);
            float cosineYaw = (float)Math.Cos(yaw);
            Vector3 groundForward = up * cosineYaw + right * sineYaw;
            Vector3 groundRight = right * cosineYaw - up * sineYaw;
            Vector3D target = (Vector3D)view * targetRadius;
            Vector3D offset = (Vector3D)view * Math.Sin(pitch) - (Vector3D)groundForward * Math.Cos(pitch);
            Vector3D position = target + offset * distance;

            // Tilted low over rough ground the orbit can dip into a hillside; lift the camera clear of it.
            double positionRadius = position.Length();
            if (positionRadius > 1d)
            {
                Vector3D positionDirection = position / positionRadius;
                double ground = heights.SampleRadius((Vector3)positionDirection) + SURFACE_VIEW_GROUND_CLEARANCE_METERS;
                if (positionRadius < ground)
                    position = positionDirection * ground;
            }

            Vector3D forward = target - position;
            double toTarget = forward.Normalize();
            if (toTarget <= 1e-6)
                return false;
            Vector3D cameraRight = groundRight;
            Vector3D cameraUp = Vector3D.Cross(cameraRight, forward);
            if (cameraUp.Normalize() <= 1e-9)
                return false;
            cameraRight = Vector3D.Cross(forward, cameraUp);

            surface.Planet = planet;
            surface.Snapshot = snapshot;
            surface.Heights = heights;
            surface.TargetDirection = view;
            surface.Target = target;
            surface.GroundRight = groundRight;
            surface.GroundForward = groundForward;
            surface.Camera = new SceneCamera
            {
                Position = position,
                Right = cameraRight,
                Up = cameraUp,
                Forward = forward,
                // The short side spans the map's 60-degree field of view, as the globe's scale uses the short side.
                FocalLengthPixels = Math.Min(width, height) * 0.5d / Math.Tan(STAR_VERTICAL_FOV_RADIANS * 0.5d),
                CenterX = width * 0.5d + state.PlanetCenterOffsetNormalized.X * width,
                CenterY = height * 0.5d + state.PlanetCenterOffsetNormalized.Y * height,
                NearDepth = Math.Max(0.5d, Math.Min(25d, toTarget * 0.05d)),
                Width = width,
                Height = height
            };
            return true;
        }

        void RenderSurfaceView(PixelCanvas canvas, MapViewState state, SurfaceView surface)
        {
            _frameHas3D = true;
            SceneFrame frame = _preparingFrame;
            SceneCamera camera = surface.Camera;
            MyPlanet planet = surface.Planet;
            AddSkyboxBackground(state,
                (Vector3)PlanetLocalDirectionToWorld(planet, (Vector3)camera.Forward),
                (Vector3)PlanetLocalDirectionToWorld(planet, (Vector3)camera.Right),
                (Vector3)PlanetLocalDirectionToWorld(planet, (Vector3)camera.Up),
                (float)camera.FocalLengthPixels, (float)camera.CenterX, (float)camera.CenterY, true);

            double distance = Vector3D.Distance(camera.Position, surface.Target);
            TerrainScene scene = RentTerrainScene(0);
            ConfigureTerrainScene(scene, camera, surface.Heights,
                GetCubemap(surface.Snapshot.EntityId, state.Layer), state.WaterVisible,
                surface.TargetDirection,
                // About six pixels per cell at the target.
                distance / camera.FocalLengthPixels * 6d, planet);
            scene.ReliefScale = SurfaceViewRelief(state, surface);

            int width = canvas.Width;
            int height = canvas.Height;
            FrameSlot slot = _slot;
            frame.Initialize = () => frame.DepthBuffer = PrepareStarDepthBuffer(slot, width, height);
            AddLayer(frame, scene);
            Vector3 light = TerrainScene.GetMapLight(surface.TargetDirection, PlanetLocalNorth);
            if (state.BlocksVisible)
                AddBlockLayer(frame, camera, planet, surface.Target, Math.Max(250d, distance * 2d), light, null);
            if (state.VoxelMapsVisible)
                AddVoxelLayer(frame, camera, planet, surface.Target, distance * VOXEL_SCENE_DISTANCE_FACTOR, light);
            AddModelLayer(frame, camera, MatrixD.Invert(planet.WorldMatrix), light);
            // Only where the layers above left sky, and under the atmosphere that blends over it.
            FlushSkybox();
            if (state.AtmosphereVisible)
            {
                AtmosphereScene atmosphere = CreateAtmosphereScene(planet, camera, 0);
                if (atmosphere != null)
                    AddLayer(frame, atmosphere);
            }
        }

        // Its Build runs after Initialize, in parallel with the other layers' builds; its Draw on every band in the
        // order added.
        void AddLayer(SceneFrame frame, ISceneLayer layer)
        {
            frame.Builds.Add(layer.Build);
            frame.Commands.Add((target, first, end) => layer.Draw(target, frame.DepthBuffer, first, end));
            _frameLayers.Add(layer);
        }

        // The planet a first-person Star Map camera is inside of, as terrain instead of a smooth sphere.
        bool TryAddHudTerrain(
            SceneFrame frame,
            List<TerrainScene> scenes,
            MapViewState state,
            StarCamera camera,
            PlanetSnapshot planet,
            int width,
            int height)
        {
            MyPlanet entity = planet.Planet;
            if (entity == null || entity.MarkedForClose || planet.Radius <= 0f)
                return false;
            Vector3D position = WorldToPlanetLocal(entity, camera.PositionWorld);
            double radius = position.Length();
            if (radius <= 1d || radius - entity.AverageRadius > GetGravityWellAltitude(entity))
                return false;
            _frameHas3D = true;
            PlanetHeightCubemap heights = FindHeights(planet.EntityId, state.Layer);
            if (heights == null)
            {
                // Inside the well, but the full-resolution planet is still being built on the worker.
                _sceneLoading = true;
                return false;
            }

            Vector3 nadir = (Vector3)(position / radius);
            double altitude = Math.Max(0d, radius - heights.SampleRadius(nadir));
            var sceneCamera = new SceneCamera
            {
                Position = position,
                Right = WorldDirectionToPlanetLocal(entity, new Vector3D(camera.Right)),
                Up = WorldDirectionToPlanetLocal(entity, new Vector3D(camera.Up)),
                Forward = WorldDirectionToPlanetLocal(entity, -new Vector3D(camera.Backward)),
                FocalLengthPixels = camera.FocalLengthPixels,
                CenterX = camera.ScreenCenterX,
                CenterY = camera.ScreenCenterY,
                // A screen against a wall must not see through it: the Star Map's own near plane (metres
                // out, for planets) is far too deep for blocks and ground right in front of the screen.
                NearDepth = HUD_NEAR_DEPTH_METERS,
                Width = width,
                Height = height
            };

            TerrainScene scene = RentTerrainScene(scenes.Count);
            ConfigureTerrainScene(scene, sceneCamera, heights, GetCubemap(planet.EntityId, state.Layer),
                state.WaterVisible, nadir, altitude / camera.FocalLengthPixels * 6d, entity);
            bool firstTerrain = scenes.Count == 0;
            scenes.Add(scene);
            AddLayer(frame, scene);
            // One set of entity layers per frame: the camera is inside one planet at a time.
            Vector3 light = TerrainScene.GetMapLight(nadir, PlanetLocalNorth);
            if (state.VoxelMapsVisible && firstTerrain)
                AddVoxelLayer(frame, sceneCamera, entity, position, Math.Max(2000d, altitude * 4d), light);
            // Every grid, the screen's own included; only the screen block itself is left out.
            if (state.BlocksVisible && firstTerrain)
                AddBlockLayer(frame, sceneCamera, entity, position, HudBlockRadius(), light,
                    camera.PositionWorld);
            if (firstTerrain)
                AddModelLayer(frame, sceneCamera, MatrixD.Invert(entity.WorldMatrix), light);
            if (state.AtmosphereVisible && firstTerrain)
                _hudAtmosphere = CreateAtmosphereScene(entity, sceneCamera, 1);
            return true;
        }

        // Away from every gravity well (no terrain), how far around a HUD camera asteroids are drawn.
        const double HUD_SPACE_VOXEL_RADIUS = 5000d;

        // Without readable session settings: the game's default sync distance.
        const double DEFAULT_SYNC_DISTANCE = 3000d;

        // As far as the game draws grids (the view distance), never under the multiplayer sync distance.
        static double HudBlockRadius()
        {
            var session = MyAPIGateway.Session;
            var settings = session == null ? null : session.SessionSettings;
            if (settings == null)
                return DEFAULT_SYNC_DISTANCE;
            return Math.Max(Math.Max(settings.ViewDistance, settings.SyncDistance), 1d);
        }

        // The scene is the world moved to the camera: precision stays high far from the origin, and depths stay the
        // camera-space metres the planets use.
        void AddHudSpaceEntities(SceneFrame frame, MapViewState state, StarCamera camera, int width, int height)
        {
            if (!state.BlocksVisible && !state.VoxelMapsVisible && _models.Count == 0)
                return;
            _frameHas3D = true;
            var sceneCamera = new SceneCamera
            {
                Position = Vector3D.Zero,
                Right = new Vector3D(camera.Right),
                Up = new Vector3D(camera.Up),
                Forward = -new Vector3D(camera.Backward),
                FocalLengthPixels = camera.FocalLengthPixels,
                CenterX = camera.ScreenCenterX,
                CenterY = camera.ScreenCenterY,
                NearDepth = HUD_NEAR_DEPTH_METERS,
                Width = width,
                Height = height
            };
            MatrixD worldToScene = MatrixD.CreateTranslation(-camera.PositionWorld);
            Vector3 light = MyVisualScriptLogicProvider.GetSunDirection();
            if (light.Normalize() <= VECTOR_EPSILON)
                light = Vector3.Up;

            if (state.VoxelMapsVisible)
            {
                if (_slot.Voxels == null)
                    _slot.Voxels = new VoxelLayer(VoxelMaterialPalette.Shared);
                var voxels = new SceneCapture
                {
                    Camera = sceneCamera,
                    WorldToScene = worldToScene,
                    CenterWorld = camera.PositionWorld,
                    Radius = MathHelper.Clamp(HUD_SPACE_VOXEL_RADIUS, VOXEL_SCENE_MINIMUM_RADIUS,
                        VOXEL_SCENE_MAXIMUM_RADIUS),
                    Light = light,
                    Only = _entityFilter
                };
                _slot.Voxels.Capture(ref voxels);
                AddLayer(frame, _slot.Voxels);
            }
            // Every grid, the screen's own included; only the screen block itself is left out.
            if (state.BlocksVisible)
            {
                if (_slot.Blocks == null)
                    _slot.Blocks = new BlockLayer();
                var blocks = new SceneCapture
                {
                    Camera = sceneCamera,
                    WorldToScene = worldToScene,
                    CenterWorld = camera.PositionWorld,
                    Radius = HudBlockRadius(),
                    Light = light,
                    ExcludeWorld = camera.PositionWorld,
                    Only = _entityFilter
                };
                _slot.Blocks.Capture(ref blocks);
                AddMissileLines(_slot.Blocks, sceneCamera, worldToScene, state);
                AddLayer(frame, _slot.Blocks);
            }
            AddModelLayer(frame, sceneCamera, worldToScene, light);
        }

        // The client meshes (AddModel), after the scene's own solid layers so a blended model lies over them.
        void AddModelLayer(SceneFrame frame, SceneCamera camera, MatrixD worldToScene, Vector3 light)
        {
            if (_models.Count == 0)
                return;
            if (_slot.Models == null)
                _slot.Models = new ModelLayer();
            var capture = new SceneCapture
            {
                Camera = camera,
                WorldToScene = worldToScene,
                Light = light
            };
            if (_slot.Models.Capture(ref capture, _models))
                AddLayer(frame, _slot.Models);
        }

        readonly MissileTracks _missiles = new MissileTracks();
        readonly List<SceneLine> _missileLines = new List<SceneLine>();
        MapViewState _missileState;

        // Seen through everything, as the grid screens draw them; only the first-person views follow missiles.
        void AddMissileLines(BlockLayer layer, SceneCamera camera, MatrixD worldToScene, MapViewState state)
        {
            _missileLines.Clear();
            if (state == null || !state.MissilesVisible || !state.HasFixedStarCamera)
            {
                _missiles.Clear();
                layer.SetLines(null);
                return;
            }
            Vector3D cameraWorld = Vector3D.Transform(camera.Position, MatrixD.Invert(worldToScene));
            _missiles.Sample(new BoundingSphereD(cameraWorld, HudBlockRadius()), _entityFilter);
            _missiles.AddLines(_missileLines, camera, worldToScene);
            layer.SetLines(_missileLines);
        }

        void AddBlockLayer(SceneFrame frame, SceneCamera camera, MyPlanet planet, Vector3D centerLocal,
            double radius, Vector3 light, Vector3D? excludeWorld)
        {
            if (_slot.Blocks == null)
                _slot.Blocks = new BlockLayer();
            SceneCapture capture = PlanetCapture(camera, planet, centerLocal, radius, light, excludeWorld);
            capture.Only = _entityFilter;
            _slot.Blocks.Capture(ref capture);
            AddMissileLines(_slot.Blocks, camera, capture.WorldToScene, _missileState);
            AddLayer(frame, _slot.Blocks);
        }

        // The entity layers' inputs in a planet's local frame: its axes are the planet's Right, Up and Backward,
        // which is its world matrix inverted.
        static SceneCapture PlanetCapture(SceneCamera camera, MyPlanet planet, Vector3D centerLocal, double radius,
            Vector3 light, Vector3D? excludeWorld)
        {
            return new SceneCapture
            {
                Camera = camera,
                WorldToScene = MatrixD.Invert(planet.WorldMatrix),
                CenterWorld = PlanetLocalToWorld(planet, centerLocal),
                Radius = radius,
                Light = light,
                ExcludeWorld = excludeWorld
            };
        }

        // The game derives Rayleigh scattering per channel as 1 / wavelength^4 over RayleighScattering: that is what
        // makes one sky blue and another orange.
        AtmosphereScene CreateAtmosphereScene(MyPlanet planet, SceneCamera camera, int poolIndex)
        {
            AtmosphereParameters air;
            if (!TryGetAtmosphere(planet, out air))
                return null;
            // The HUD hands the planet over to the orbit view at the gravity well's edge.
            air.ScaleHeight = AtmosphereScene.ScaleHeightAt(camera.Position.Length(), air.GroundRadius, air.TopRadius,
                planet.AverageRadius + GetGravityWellAltitude(planet));
            AtmosphereScene scene = RentAtmosphereScene(poolIndex);
            scene.Camera = camera;
            scene.Atmosphere = air;
            // A few dozen horizon rays, once per frame, on the capturing thread.
            scene.Prepare();
            return scene;
        }

        bool TryGetAtmosphere(MyPlanet planet, out AtmosphereParameters air)
        {
            air = default(AtmosphereParameters);
            if (planet == null || planet.MarkedForClose || !planet.HasAtmosphere ||
                planet.AtmosphereRadius <= planet.AverageRadius)
                return false;
            PlanetAtmosphereData settings;
            if (!_atmospheres.TryGetValue(planet.EntityId, out settings))
            {
                try
                {
                    settings = PlanetAtmosphereData.From(planet);
                }
                catch (Exception)
                {
                    settings = null;
                }
                // Cached even when unreadable, so a failing planet is not serialized every frame.
                _atmospheres[planet.EntityId] = settings;
            }

            Vector3 wavelengths = planet.AtmosphereWavelengths;
            Vector3 rayleigh = settings == null ? Vector3.One : settings.RayleighScattering;
            var scattering = new Vector3(
                Scattering(wavelengths.X, rayleigh.X),
                Scattering(wavelengths.Y, rayleigh.Y),
                Scattering(wavelengths.Z, rayleigh.Z));
            float strongest = Math.Max(scattering.X, Math.Max(scattering.Y, scattering.Z));
            if (!(strongest > 0f) || float.IsInfinity(strongest))
                return false;
            scattering /= strongest;

            float sea = settings == null || settings.SeaLevelModifier <= 0f ? 1f : settings.SeaLevelModifier;
            float top = settings == null || settings.AtmosphereTopModifier <= 0f ? 1f : settings.AtmosphereTopModifier;
            Vector3 sunColor = settings != null && settings.SunColorLinear.HasValue
                ? Vector3.Min(Vector3.One, Vector3.Max(Vector3.Zero, settings.SunColorLinear.Value))
                : Vector3.One;
            Vector3 sun = WorldDirectionToPlanetLocal(planet, MyVisualScriptLogicProvider.GetSunDirection());
            if (sun.Normalize() <= VECTOR_EPSILON)
                return false;

            air = new AtmosphereParameters
            {
                // As the game's atmosphere renderer places its ground and top spheres.
                GroundRadius = planet.AverageRadius * 1.01d * sea,
                TopRadius = planet.AtmosphereRadius * top,
                Scattering = scattering,
                SunColor = sunColor,
                SunDirection = sun,
                Intensity = settings == null ? 1f : settings.Intensity,
                FogIntensity = settings == null ? 0f : Math.Max(0f, settings.FogIntensity)
            };
            return true;
        }

        // A planet spawned without wavelengths gets the generator's base ones (MyWorldGenerator).
        static float Scattering(float wavelength, float rayleigh)
        {
            if (wavelength <= 0f)
                wavelength = 0.57f;
            return (float)(1d / Math.Pow(wavelength, 4d)) / (rayleigh > 0f ? rayleigh : 1f);
        }

        void AddVoxelLayer(SceneFrame frame, SceneCamera camera, MyPlanet planet, Vector3D centerLocal,
            double radius, Vector3 light)
        {
            if (_slot.Voxels == null)
                _slot.Voxels = new VoxelLayer(VoxelMaterialPalette.Shared);
            SceneCapture capture = PlanetCapture(camera, planet, centerLocal,
                MathHelper.Clamp(radius, VOXEL_SCENE_MINIMUM_RADIUS, VOXEL_SCENE_MAXIMUM_RADIUS), light, null);
            capture.Only = _entityFilter;
            _slot.Voxels.Capture(ref capture);
            AddLayer(frame, _slot.Voxels);
        }

        // The game's own limit when its gravity component is readable, else its formula over the generator's gravity.
        static double GetGravityWellAltitude(MyPlanet planet)
        {
            double limit = 0d;
            var gravity = planet.Components.Get<MyGravityProviderComponent>() as IMyNaturalGravityComponent;
            if (gravity != null)
                limit = gravity.GravityLimit;
            if (!(limit > planet.AverageRadius) && planet.Generator != null)
            {
                double falloff = Math.Max(0.1d, planet.Generator.GravityFalloffPower);
                double surface = planet.Generator.SurfaceGravity;
                if (surface > WELL_EDGE_GRAVITY)
                    limit = planet.MaximumRadius * Math.Pow(surface / WELL_EDGE_GRAVITY, 1d / falloff);
            }
            return limit > planet.AverageRadius
                ? limit - planet.AverageRadius
                : planet.AverageRadius * FALLBACK_WELL_ALTITUDE_RATIO;
        }

        static void ConfigureTerrainScene(
            TerrainScene scene,
            SceneCamera camera,
            PlanetHeightCubemap heights,
            PlanetColorCubemap colors,
            bool waterVisible,
            Vector3 centerDirection,
            double innerCellMeters,
            MyPlanet planet)
        {
            scene.Camera = camera;
            // The distant planet everywhere else is a sphere of its average radius.
            double sphere = planet == null ? 0d : planet.AverageRadius;
            scene.SphereRadius = sphere;
            double well = planet == null ? 0d : GetGravityWellAltitude(planet);
            scene.SpherizeStart = well * SPHERIZE_START_FRACTION;
            scene.SpherizeEnd = well;
            scene.ReliefScale = 1f;
            scene.Heights = heights;
            scene.Colors = colors;
            scene.Surface = colors == null ? null : colors.Surface;
            scene.WaterVisible = waterVisible;
            scene.WaterTint = WaterOverlayTint;
            scene.CenterDirection = centerDirection;
            scene.North = PlanetLocalNorth;
            // The bicubic patch and the detail texture keep shape below a texel; pixels resolve finer still.
            scene.InnerCellMeters = Math.Max(1.5d, innerCellMeters);

            // Reach the horizon: the tangent from the camera to the lowest terrain, plus how far past it the
            // highest terrain still rises into view, plus the camera's own offset from the mesh centre.
            double cameraRadius = Math.Max(1d, camera.Position.Length());
            double lowest = Math.Max(1d, heights.MinimumRadius);
            double arc = Math.Acos(Math.Min(1d, lowest / Math.Max(lowest, cameraRadius))) +
                         Math.Acos(Math.Min(1d, lowest / Math.Max(lowest, heights.MaximumRadius)));
            double centerDot = Vector3D.Dot(camera.Position / cameraRadius, (Vector3D)centerDirection);
            arc += Math.Acos(Math.Max(-1d, Math.Min(1d, centerDot)));
            scene.OuterArcRadians = arc * 1.05d + 0.002d;
        }

        bool TryProjectSurfaceView(
            RectangleF mapBounds,
            Vector3D worldPosition,
            MapData data,
            MapViewState state,
            out Vector2 screenPosition)
        {
            screenPosition = mapBounds.Center;
            SurfaceView surface;
            if (!TryBuildSurfaceView(data, state,
                    Math.Max(1, (int)Math.Round(mapBounds.Width)),
                    Math.Max(1, (int)Math.Round(mapBounds.Height)), out surface))
                return false;

            Vector2 pixel;
            double depth;
            if (!surface.Camera.TryProject(WorldToPlanetLocal(surface.Planet, worldPosition), out pixel, out depth))
                return false;
            screenPosition = new Vector2(mapBounds.X + pixel.X, mapBounds.Y + pixel.Y);
            return IsFinite(screenPosition.X) && IsFinite(screenPosition.Y);
        }

        bool TryGetSurfaceViewDirection(
            RectangleF mapBounds,
            Vector2 point,
            MapData data,
            MapViewState state,
            out Vector3 localDirection)
        {
            localDirection = Vector3.Zero;
            SurfaceView surface;
            if (!TryBuildSurfaceView(data, state,
                    Math.Max(1, (int)Math.Round(mapBounds.Width)),
                    Math.Max(1, (int)Math.Round(mapBounds.Height)), out surface))
                return false;

            Vector3D hit;
            Vector3D ray = surface.Camera.GetRay(new Vector2(point.X - mapBounds.X, point.Y - mapBounds.Y));
            if (!TerrainScene.TryIntersect(surface.Heights, surface.Camera.Position, ray, out hit))
                return false;
            localDirection = (Vector3)hit;
            return localDirection.Normalize() > VECTOR_EPSILON;
        }

        // Clockwise screen rotation of a planet-local direction lying on the ground under the surface view.
        bool TryGetSurfaceViewRotation(MapData data, MapViewState state, Vector3 localDirection, out float clockwiseRotation)
        {
            clockwiseRotation = 0f;
            SurfaceView surface;
            if (!TryBuildSurfaceView(data, state, 1, 1, out surface))
                return false;
            float screenX = Vector3.Dot(localDirection, surface.GroundRight);
            float screenUp = Vector3.Dot(localDirection, surface.GroundForward);
            if (!IsFinite(screenX) || !IsFinite(screenUp) ||
                screenX * screenX + screenUp * screenUp <= VECTOR_EPSILON * VECTOR_EPSILON)
                return false;
            clockwiseRotation = (float)Math.Atan2(screenX, screenUp);
            return IsFinite(clockwiseRotation);
        }
    }
}
