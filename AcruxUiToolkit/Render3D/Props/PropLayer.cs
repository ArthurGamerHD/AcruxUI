using System;
using System.Collections.Generic;
using Acrux.Render3D.Models;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Render3D.Props
{
    /// <summary>
    /// Content models placed whole in the scene (trees, forageable plants, characters), flat-lit like the grids and
    /// sharing the depth buffer. Each kind captures its own placements on the game thread
    /// (<see cref="BeginCapture"/>, <see cref="Add"/>, <see cref="EndCapture"/>); <see cref="Build"/> culls them
    /// to the screen, keeps the nearest within the triangle budget and transforms them on the paint threads in
    /// chunks; <see cref="Draw"/> runs on the row-band workers. Models come from <see cref="MeshAssets"/>, the
    /// lowest-detail LOD each lists, tinted by its materials' textures.
    /// </summary>
    internal abstract class PropLayer : ISceneLayer
    {
        /// <summary>How a placement colors its model.</summary>
        protected enum PropTint
        {
            // The model's texture tints as they are.
            Textured,
            // The texture tints under a paint color, as the game's colorable materials (a character's suit).
            Painted,
            // The paint color alone.
            Flat
        }

        struct Prop
        {
            public MeshAsset Model;
            public MatrixD ToScene;
            public Vector3 Paint;
            public PropTint Tint;
        }

        struct Candidate
        {
            public int Prop;
            public float Distance;
        }

        sealed class BuildChunk
        {
            public readonly SceneTriangleList Triangles = new SceneTriangleList();
            public Vector3D[] Transformed = new Vector3D[0];
            public int First;
            public int End;
            public Exception Failure;
        }

        // Chunks per paint thread: more than one balances props whose real cost the count misses.
        const int CHUNKS_PER_THREAD = 4;
        // A chunk is at least this many triangles: a small scene builds on one thread without the overhead.
        const int MINIMUM_CHUNK_TRIANGLES = 4096;

        // Captured on the game thread.
        readonly List<Prop> _props = new List<Prop>();
        SceneCamera _camera;
        Vector3 _light;
        int _triangleBudget;
        double _minimumPixels;
        int _loads;
        bool _waiting;

        // Built on the paint coordinator.
        readonly List<Candidate> _candidates = new List<Candidate>();
        readonly List<Prop> _selected = new List<Prop>();
        readonly List<BuildChunk> _chunks = new List<BuildChunk>();
        int _chunkCount;
        Action<int> _buildChunk;

        sealed class NearestFirstComparer : IComparer<Candidate>
        {
            public int Compare(Candidate a, Candidate b)
            {
                return a.Distance.CompareTo(b.Distance);
            }
        }

        static readonly NearestFirstComparer NearestFirst = new NearestFirstComparer();

        public string PendingWork { get; private set; }

        /// <summary>True when the last capture placed anything: the layer is worth adding to the frame.</summary>
        public bool HasContent
        {
            get { return _props.Count > 0; }
        }

        // What the loading report calls models of this kind still being read.
        protected abstract string PendingDescription { get; }

        // Game thread.
        protected void BeginCapture(ref SceneCapture capture, int triangleBudget)
        {
            Render3DSettings settings = Render3DSettings.Current;
            _props.Clear();
            _camera = capture.Camera;
            _light = capture.Light;
            _triangleBudget = Math.Max(0, triangleBudget);
            _minimumPixels = settings.MinimumPropPixels;
            _loads = settings.PropModelLoadsPerFrame;
            _waiting = false;
        }

        // Game thread. The model, or null while it is not loaded (the frame then reports it as pending). Its last
        // LOD is passed over when it is an impostor (a tree's crossed cards): the scene draws the real shape.
        protected MeshAsset Resolve(string assetName)
        {
            if (string.IsNullOrEmpty(assetName))
                return null;
            MeshAsset model = MeshAssets.Resolve(assetName, ref _loads, true);
            if (model == null && !MeshAssets.HasFailed(assetName, true))
                _waiting = true;
            return model;
        }

        // Game thread. Model space to scene space in toScene.
        protected void Add(MeshAsset model, ref MatrixD toScene, Vector3 paint, PropTint tint)
        {
            if (model == null || model.TriangleCount == 0)
                return;
            _props.Add(new Prop { Model = model, ToScene = toScene, Paint = paint, Tint = tint });
        }

        // Game thread.
        protected void EndCapture()
        {
            PendingWork = _waiting ? PendingDescription : null;
        }

        public void Build()
        {
            Select();
            BuildTriangles();
        }

        // The props on screen and large enough, nearest first, within the triangle budget.
        void Select()
        {
            _candidates.Clear();
            _selected.Clear();
            for (int i = 0; i < _props.Count; i++)
            {
                Prop prop = _props[i];
                MatrixD toScene = prop.ToScene;
                Vector3D center = Vector3D.Transform((Vector3D)prop.Model.Center, toScene);
                double scale = Math.Sqrt(Math.Max(toScene.Right.LengthSquared(),
                    Math.Max(toScene.Up.LengthSquared(), toScene.Forward.LengthSquared())));
                SceneVertex vertex;
                _camera.ToCameraSpace(ref center, out vertex);
                double pixels;
                if (double.IsNaN(vertex.Z) || double.IsInfinity(vertex.Z) ||
                    !_camera.MayBeOnScreen(ref vertex, prop.Model.Radius * scale, out pixels) ||
                    pixels < _minimumPixels)
                    continue;
                _candidates.Add(new Candidate
                {
                    Prop = i,
                    Distance = (float)Math.Max(0d, Vector3D.Distance(center, _camera.Position))
                });
            }
            _candidates.Sort(NearestFirst);

            int triangles = 0;
            for (int i = 0; i < _candidates.Count; i++)
            {
                Prop prop = _props[_candidates[i].Prop];
                if (triangles + prop.Model.TriangleCount > _triangleBudget)
                    break;
                triangles += prop.Model.TriangleCount;
                _selected.Add(prop);
            }
        }

        void BuildTriangles()
        {
            int count = _selected.Count;
            long total = 0;
            for (int i = 0; i < count; i++)
                total += _selected[i].Model.TriangleCount;
            int chunks = count == 0
                ? 0
                : (int)Math.Max(1L, Math.Min(Math.Min(count, PaintWorkers.ThreadCount * CHUNKS_PER_THREAD),
                    total / MINIMUM_CHUNK_TRIANGLES));
            while (_chunks.Count < chunks)
                _chunks.Add(new BuildChunk());
            _chunkCount = chunks;
            if (chunks == 0)
                return;

            int prop = 0;
            long done = 0;
            for (int c = 0; c < chunks; c++)
            {
                BuildChunk chunk = _chunks[c];
                chunk.First = prop;
                long target = total * (c + 1) / chunks;
                while (prop < count && (done < target || c == chunks - 1))
                    done += _selected[prop++].Model.TriangleCount;
                chunk.End = prop;
                chunk.Failure = null;
            }

            if (chunks == 1)
            {
                BuildRange(_chunks[0]);
            }
            else
            {
                if (_buildChunk == null)
                    _buildChunk = BuildChunkAt;
                PaintWorkers.For(0, chunks, _buildChunk);
            }
            for (int c = 0; c < chunks; c++)
            {
                if (_chunks[c].Failure != null)
                    throw new Exception("Prop scene build failed.", _chunks[c].Failure);
            }
        }

        void BuildChunkAt(int index)
        {
            BuildChunk chunk = _chunks[index];
            try
            {
                BuildRange(chunk);
            }
            catch (Exception exception)
            {
                // Caught on the worker: the engine may treat uncaught parallel task exceptions as fatal.
                chunk.Failure = exception;
            }
        }

        void BuildRange(BuildChunk chunk)
        {
            SceneTriangleList triangles = chunk.Triangles;
            triangles.Clear();
            for (int i = chunk.First; i < chunk.End; i++)
            {
                Prop prop = _selected[i];
                MeshAsset model = prop.Model;
                Vector3[] vertices = model.Vertices;
                if (chunk.Transformed.Length < vertices.Length)
                    chunk.Transformed = new Vector3D[vertices.Length];
                Vector3D[] transformed = chunk.Transformed;
                for (int v = 0; v < vertices.Length; v++)
                    transformed[v] = Vector3D.Transform((Vector3D)vertices[v], prop.ToScene);
                for (int part = 0; part < model.PartIndices.Length; part++)
                {
                    Vector3 color;
                    switch (prop.Tint)
                    {
                        case PropTint.Flat:
                            color = prop.Paint;
                            break;
                        case PropTint.Painted:
                            color = model.PartTints[part] * (new Vector3(0.30f) + prop.Paint * 1.05f);
                            break;
                        default:
                            color = model.PartTints[part];
                            break;
                    }
                    int[] indices = model.PartIndices[part];
                    for (int t = 0; t + 2 < indices.Length; t += 3)
                    {
                        int a = indices[t];
                        int b = indices[t + 1];
                        int c = indices[t + 2];
                        if ((uint)a >= (uint)vertices.Length || (uint)b >= (uint)vertices.Length ||
                            (uint)c >= (uint)vertices.Length)
                            continue;
                        AddTriangle(triangles, ref transformed[a], ref transformed[b], ref transformed[c], color);
                    }
                }
            }
            triangles.Bin(_camera.Height);
        }

        void AddTriangle(SceneTriangleList triangles, ref Vector3D a, ref Vector3D b, ref Vector3D c, Vector3 color)
        {
            // Two-sided, as the grids are lit: ambient plus a directional term on the face normal.
            Vector3 normal = (Vector3)Vector3D.Cross(b - a, c - a);
            float shade = normal.Normalize() > 1e-9f
                ? MathHelper.Clamp(0.64f + 0.34f * Math.Abs(Vector3.Dot(normal, _light)), 0.34f, 1.05f)
                : 0.62f;

            SceneVertex va;
            SceneVertex vb;
            SceneVertex vc;
            _camera.ToCameraSpace(ref a, out va);
            _camera.ToCameraSpace(ref b, out vb);
            _camera.ToCameraSpace(ref c, out vc);
            va.Attribute = vb.Attribute = vc.Attribute = color * shade;
            triangles.Add(ref _camera, ref va, ref vb, ref vc);
        }

        public void Draw(PixelCanvas canvas, float[] depthBuffer, int firstRow, int endRow)
        {
            if (depthBuffer == null)
                return;
            for (int c = 0; c < _chunkCount; c++)
            {
                SceneTriangleList triangles = _chunks[c].Triangles;
                if (triangles.Count > 0)
                    SceneRasterizer.DrawFlat(canvas, depthBuffer, triangles, firstRow, endRow);
            }
        }
    }
}
