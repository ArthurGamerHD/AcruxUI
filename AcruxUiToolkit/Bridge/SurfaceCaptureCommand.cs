#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using Acrux.Lcd;
using Adk.Compression.Zip;
using LcdEyeTrackingApi;
using LcdEyeTrackingApi.Geometry;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.ModAPI;
using VRageMath;

namespace Acrux.Bridge
{
    // Dev builds only (MDK drops this file from release packs). "/acrux capture" saves the screen the camera looks at,
    // "/acrux capture-all" every Acrux screen that drew in the last minute, each as capture-<time>-...acruxcap in the
    // toolkit's local storage, which "AcruxPreviewHost --render-capture <file> <png>" renders.
    internal static class SurfaceCaptureCommand
    {
        const double RANGE_METERS = 30;
        const string TITLE = "Acrux UI";
        static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(1);

        sealed class Candidate : ITrackedSurface
        {
            public IMyCubeBlock Block { get; set; }
            public IMyTextSurface Surface { get; set; }
            public int RotationOrSurfaceIndex => ScreenTracker.ResolveSurfaceIndex(Block, Surface);
        }

        internal static void Run()
        {
            string failure;
            IMyCubeBlock block;
            IMyTextSurface surface = FindLookedAtSurface(out block, out failure);
            if (surface == null)
            {
                MyAPIGateway.Utilities.ShowMessage(TITLE, failure);
                return;
            }
            LcdSurfaceCapture.Frame frame = LcdSurfaceCapture.Capture(surface, block.DisplayNameText);
            if (frame == null)
            {
                MyAPIGateway.Utilities.ShowMessage(TITLE, "'" + surface.DisplayName + "' of " + block.DisplayNameText +
                                                          " has drawn no Acrux frame.");
                return;
            }
            SaveInBackground(new List<LcdSurfaceCapture.Frame> { frame });
        }

        internal static void RunAll()
        {
            List<Sandbox.ModAPI.Ingame.IMyTextSurface> surfaces = LcdSurfaceCapture.ActiveSurfaces(ActiveWindow);
            if (surfaces.Count == 0)
            {
                MyAPIGateway.Utilities.ShowMessage(TITLE, "No Acrux screen drew in the last minute.");
                return;
            }
            Dictionary<Sandbox.ModAPI.Ingame.IMyTextSurface, string> blocks = FindBlocks();
            var frames = new List<LcdSurfaceCapture.Frame>();
            foreach (var surface in surfaces)
            {
                string block;
                blocks.TryGetValue(surface, out block);
                LcdSurfaceCapture.Frame frame = LcdSurfaceCapture.Capture(surface, block);
                if (frame != null)
                    frames.Add(frame);
            }
            SaveInBackground(frames);
        }

        internal static void Unload()
        {
            LcdSurfaceCapture.Clear();
        }

        // Surfaces do not know their block, so the grids are searched once for the blocks providing them.
        static Dictionary<Sandbox.ModAPI.Ingame.IMyTextSurface, string> FindBlocks()
        {
            var blocks = new Dictionary<Sandbox.ModAPI.Ingame.IMyTextSurface, string>();
            var entities = new HashSet<IMyEntity>();
            var slims = new List<IMySlimBlock>();
            MyAPIGateway.Entities.GetEntities(entities, entity => entity is IMyCubeGrid);
            foreach (IMyEntity entity in entities)
            {
                slims.Clear();
                ((IMyCubeGrid)entity).GetBlocks(slims, slim => slim.FatBlock is Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider);
                foreach (IMySlimBlock slim in slims)
                {
                    var provider = (Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider)slim.FatBlock;
                    for (int i = 0; i < provider.SurfaceCount; i++)
                    {
                        var surface = provider.GetSurface(i);
                        if (surface != null)
                            blocks[surface] = slim.FatBlock.DisplayNameText;
                    }
                }
            }
            return blocks;
        }

        static IMyTextSurface FindLookedAtSurface(out IMyCubeBlock block, out string failure)
        {
            block = null;
            Vector3D origin;
            Vector3D direction;
            if (!ScreenTracker.TryGetCameraRay(out origin, out direction))
            {
                failure = "No camera to aim with.";
                return null;
            }
            Vector3D end = origin + direction * RANGE_METERS;
            IHitInfo hit;
            if (MyAPIGateway.Physics == null || !MyAPIGateway.Physics.CastRay(origin, end, out hit) || hit == null ||
                hit.HitEntity == null)
            {
                failure = "Nothing under the crosshair within " + RANGE_METERS + " m.";
                return null;
            }
            var grid = hit.HitEntity.GetTopMostParent() as IMyCubeGrid;
            Vector3I? cell = grid == null ? null : grid.RayCastBlocks(origin, end);
            IMySlimBlock slim = cell.HasValue ? grid.GetCubeBlock(cell.Value) : null;
            block = slim == null ? null : slim.FatBlock;
            var provider = block as Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount == 0)
            {
                failure = "The block under the crosshair has no screen.";
                return null;
            }

            IMyTextSurface nearest = null;
            double nearestDistance = double.MaxValue;
            for (int i = 0; i < provider.SurfaceCount; i++)
            {
                var candidate = new Candidate { Block = block, Surface = provider.GetSurface(i) as IMyTextSurface };
                Vector2 point;
                double distance;
                if (candidate.Surface != null &&
                    ScreenAreaGeometry.TryGetScreenPointIntersection(candidate, origin, direction, out point, out distance) &&
                    distance < nearestDistance)
                {
                    nearest = candidate.Surface;
                    nearestDistance = distance;
                }
            }
            // A block whose screen geometry is unknown still has one obvious screen when it has only one.
            if (nearest == null && provider.SurfaceCount == 1)
                nearest = provider.GetSurface(0) as IMyTextSurface;
            failure = nearest == null ? "None of " + block.DisplayNameText + "'s screens is under the crosshair." : null;
            return nearest;
        }

        // Compressing the pixels is the slow part, so it runs in the background; the files are written on the game
        // thread (mods cannot buffer a stream in memory, so each writer goes straight to its file).
        static void SaveInBackground(List<LcdSurfaceCapture.Frame> frames)
        {
            MyAPIGateway.Parallel.StartBackground(() =>
            {
                var packed = new List<Dictionary<string, byte[]>>();
                string error = null;
                try
                {
                    foreach (LcdSurfaceCapture.Frame frame in frames)
                    {
                        var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                        foreach (var pair in frame.Textures)
                            textures[pair.Key] = Zlib.DeflateZlib(pair.Value.Rgba, DeflateLevel.Fast);
                        packed.Add(textures);
                    }
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                }
                MyAPIGateway.Utilities.InvokeOnGameThread(() => Save(frames, error == null ? packed : null, error));
            });
        }

        static void Save(List<LcdSurfaceCapture.Frame> frames, List<Dictionary<string, byte[]>> packed, string error)
        {
            if (packed == null)
            {
                MyAPIGateway.Utilities.ShowMessage(TITLE, "Capture failed: " + error);
                return;
            }
            string stamp = "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string last = null;
            int saved = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                LcdSurfaceCapture.Frame frame = frames[i];
                string name = frames.Count == 1
                    ? stamp + "-" + FileSafe(frame.BlockName, frame.DisplayName) + ".acruxcap"
                    : stamp + "-" + (i + 1).ToString("00") + "-" + FileSafe(frame.BlockName, frame.DisplayName) + ".acruxcap";
                Dictionary<string, byte[]> textures = packed[i];
                try
                {
                    using (BinaryWriter writer = MyAPIGateway.Utilities.WriteBinaryFileInLocalStorage(name, typeof(SurfaceCaptureCommand)))
                        LcdSurfaceCapture.Write(frame, writer, texture => textures[texture]);
                    saved++;
                    last = name;
                }
                catch (Exception exception)
                {
                    MyAPIGateway.Utilities.ShowMessage(TITLE, "Capture of '" + frame.DisplayName + "' not saved: " +
                                                              exception.Message);
                }
            }
            if (saved == 0)
                return;
            if (frames.Count == 1)
                MyAPIGateway.Utilities.ShowMessage(TITLE, "Captured '" + frames[0].DisplayName + "' (" +
                    frames[0].Sprites.Count + " sprites, " + frames[0].Textures.Count + " textures) to " +
                    StoragePath(last) + ". Render it with: AcruxPreviewHost --render-capture <file> <png>");
            else
                MyAPIGateway.Utilities.ShowMessage(TITLE, "Captured " + saved + " screens to " + StoragePath(stamp + "-*") +
                                                          ". Render each with: AcruxPreviewHost --render-capture <file> <png>");
        }

        // Where WriteBinaryFileInLocalStorage put it: the user folder's Storage, in a folder named after this mod's scope.
        static string StoragePath(string file)
        {
            IMyGamePaths paths = MyAPIGateway.Utilities.GamePaths;
            return Path.Combine(paths.UserDataPath, "Storage", paths.ModScopeName, file);
        }

        static string FileSafe(string block, string surface)
        {
            string text = string.IsNullOrWhiteSpace(block) ? surface : block + "-" + surface;
            var safe = new System.Text.StringBuilder();
            foreach (char c in text ?? string.Empty)
            {
                if (safe.Length >= 60)
                    break;
                safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            return safe.Length == 0 ? "screen" : safe.ToString();
        }
    }
}
#endif
