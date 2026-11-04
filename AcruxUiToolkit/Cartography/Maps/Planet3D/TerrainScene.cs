using System;
using Acrux.Render3D;
using Acrux.Rendering;
using Sandbox.ModAPI;
using VRageMath;

namespace Acrux.Cartography.Maps.Planet3D
{
    /// <summary>
    /// The planet surface as a displaced mesh, in planet-local metres. The lattice is polar around a centre
    /// direction: ring spacing grows geometrically, so cells are near-square everywhere, finest at the centre
    /// (the orbit target, or the ground under a first-person camera) and coarsest at the horizon.
    /// <para>
    /// The caller captures the fields on its own thread; <see cref="Build"/> then runs once on the paint
    /// coordinator and <see cref="Draw"/> on the row-band workers. Heights and colors are immutable cubemaps.
    /// </para>
    /// </summary>
    internal sealed class TerrainScene : ISceneLayer
    {
        const int SEGMENTS = 112;
        const int MAXIMUM_RINGS = 176;
        // Twelve rings per doubling of radius; with SEGMENTS around, cells stay close to square.
        static readonly double RING_GROWTH = Math.Pow(2d, 1d / 12d);
        // Relief contrast: how far a slope facing the light, or away from it, departs from the flat-ground color.
        const float RELIEF_STRENGTH = 1.15f;
        const float MINIMUM_SHADE = 0.5f;
        const float MAXIMUM_SHADE = 1.3f;
        static readonly Color MissingSurfaceColor = new Color(82, 86, 89);
        // Per-pixel ray refinement: Newton steps, and the largest correction (normal to the ground) it may
        // make, in pixels of ground plus a floor in metres; the mesh cells are a few pixels wide.
        const int REFINE_STEPS = 2;
        const double REFINE_MAXIMUM_PIXELS = 24d;
        const double REFINE_MINIMUM_METERS = 4d;

        public SceneCamera Camera;
        public PlanetHeightCubemap Heights;
        public PlanetColorCubemap Colors;
        // Null keeps the cubemap's texel colors at every distance.
        public PlanetSurfaceShader Surface;
        public bool WaterVisible;
        public Color WaterTint;
        public Vector3 CenterDirection;
        // The relief light comes from the north-west, as on a printed map.
        public Vector3 North;
        public double InnerCellMeters;
        public double OuterArcRadians;
        // Spherizing, as the game's planet LOD morphs far terrain: relief is whole within SpherizeStart of the camera
        // and gone past SpherizeEnd, leaving the sphere of SphereRadius.
        public double SphereRadius;
        public double SpherizeStart;
        public double SpherizeEnd;
        // 0..1, for a view fading in from the flat globe.
        public float ReliefScale = 1f;

        readonly SceneTriangleList _triangles = new SceneTriangleList();
        readonly float[] _cosine = new float[SEGMENTS];
        readonly float[] _sine = new float[SEGMENTS];
        Vector3[] _directions = new Vector3[0];
        Vector3D[] _positions = new Vector3D[0];
        SceneVertex[] _vertices = new SceneVertex[0];
        double[] _arcs = new double[0];
        readonly TerrainPixelBuffer _pixels = new TerrainPixelBuffer();
        // The inner cell's power of two, kept across frames for hysteresis.
        int _cellLevel = int.MinValue;
        const double CELL_HYSTERESIS = 0.35d;
        Vector3 _light;
        double _inverseFocal;

        public TerrainScene()
        {
            for (int j = 0; j < SEGMENTS; j++)
            {
                double angle = j * MathHelper.TwoPi / SEGMENTS;
                _cosine[j] = (float)Math.Cos(angle);
                _sine[j] = (float)Math.Sin(angle);
            }
        }

        public string PendingWork
        {
            get { return Surface != null && PlanetMaterialTextures.IsLoading ? "textures" : null; }
        }

        public int TriangleCount
        {
            get { return _triangles.Count; }
        }

        public void Build()
        {
            _triangles.Clear();
            if (Heights == null)
                return;
            _pixels.Prepare(Camera.Width * Camera.Height);
            _lattices.Prepare(Camera.Height);
            _inverseFocal = 1d / Math.Max(1e-6, Camera.FocalLengthPixels);

            // The lattice is anchored to the planet, not the camera: a mesh that slides with the camera samples
            // the heightmap at moving points, and far mountains bob as their vertices slide over peaks.
            // Inner cells come in powers of two metres, rings grow by exactly 2^(1/12) (twelve rings double
            // the radius, so a doubled inner cell lands the outer rings on the same radii), and the centre
            // snaps to a grid of inner cells on the cube face, so vertices stay put while the camera moves
            // within a cell and shift by one inner cell (nothing, at the far rings) when it crosses one.
            double reference = Math.Max(1d, Heights.MinimumRadius);
            double growth = RING_GROWTH;
            double outer = Math.Max(1e-5, Math.Min(OuterArcRadians, 1.45d));
            // Hysteresis: altitude over bumpy ground must move clearly past a doubling before the inner cell
            // (and with it the centre's snapping grid) changes, or the whole lattice re-snaps back and forth.
            double wanted = Math.Log(Math.Max(1e-3, InnerCellMeters), 2d);
            if (_cellLevel == int.MinValue || Math.Abs(wanted - (_cellLevel - 0.5d)) > CELL_HYSTERESIS + 0.5d)
                _cellLevel = (int)Math.Ceiling(wanted);
            double cellMeters = Math.Pow(2d, _cellLevel);
            double first = cellMeters / reference;
            while (first < outer * 0.5d &&
                   1 + (int)Math.Ceiling(Math.Log(outer / first) / Math.Log(growth)) > MAXIMUM_RINGS)
            {
                cellMeters *= 2d;
                first *= 2d;
            }
            first = Math.Min(first, outer * 0.5d);
            int rings = Math.Max(2, Math.Min(MAXIMUM_RINGS,
                1 + (int)Math.Ceiling(Math.Log(outer / first) / Math.Log(growth))));

            Vector3 center = SnapToFaceGrid(CenterDirection, cellMeters / (reference * MathHelper.PiOver2));
            if (center.Normalize() <= 1e-6f)
                return;
            Vector3 tangentA = Vector3.Cross(North, center);
            if (tangentA.Normalize() <= 1e-4f)
            {
                tangentA = Vector3.Cross(Math.Abs(center.X) < 0.9f ? Vector3.Right : Vector3.Up, center);
                tangentA.Normalize();
            }
            Vector3 tangentB = Vector3.Cross(center, tangentA);
            double centerRadius = Heights.SampleRadius(center);

            int vertexCount = 1 + rings * SEGMENTS;
            if (_directions.Length < vertexCount)
            {
                _directions = new Vector3[vertexCount];
                _positions = new Vector3D[vertexCount];
                _vertices = new SceneVertex[vertexCount];
            }

            _directions[0] = center;
            _positions[0] = Spherize(center, centerRadius);
            if (_arcs.Length < rings)
                _arcs = new double[rings];
            double arc = first;
            for (int ring = 0; ring < rings; ring++)
            {
                _arcs[ring] = arc;
                arc *= growth;
            }
            // Ground spacing between rings at the current arc: the detail filter for its vertices.
            double spacing = (growth - 1d) * reference;
            // Some 20k height samples, four grid points each: the rings are independent, so they run on the
            // workers instead of holding every band back on the coordinator.
            ForEachRing(rings, ring =>
            {
                double ringArc = _arcs[ring];
                float cosineArc = (float)Math.Cos(ringArc);
                float sineArc = (float)Math.Sin(ringArc);
                int offset = 1 + ring * SEGMENTS;
                for (int j = 0; j < SEGMENTS; j++)
                {
                    Vector3 direction = center * cosineArc +
                                        (tangentA * _cosine[j] + tangentB * _sine[j]) * sineArc;
                    _directions[offset + j] = direction;
                    _positions[offset + j] = Spherize(direction, SampleOnGrid(direction, ringArc * spacing, reference));
                }
            });

            Vector3 light = GetMapLight(center, North);
            _light = light;

            SceneCamera camera = Camera;
            camera.ToCameraSpace(ref _positions[0], out _vertices[0]);
            _vertices[0].Attribute = center;
            _vertices[0].Shade = 1f;
            ForEachRing(rings, ring =>
            {
                int offset = 1 + ring * SEGMENTS;
                int inner = ring == 0 ? -1 : offset - SEGMENTS;
                int outerRing = ring == rings - 1 ? -1 : offset + SEGMENTS;
                for (int j = 0; j < SEGMENTS; j++)
                {
                    int index = offset + j;
                    int previous = offset + (j == 0 ? SEGMENTS - 1 : j - 1);
                    int next = offset + (j == SEGMENTS - 1 ? 0 : j + 1);
                    Vector3D around = _positions[next] - _positions[previous];
                    Vector3D outward = (outerRing < 0 ? _positions[index] : _positions[outerRing + j]) -
                                       (inner < 0 ? _positions[0] : _positions[inner + j]);
                    Vector3 direction = _directions[index];
                    Vector3 normal = (Vector3)Vector3D.Cross(around, outward);
                    float shade = 1f;
                    if (normal.Normalize() > 1e-6f)
                    {
                        if (Vector3.Dot(normal, direction) < 0f)
                            normal = -normal;
                        shade = 1f + RELIEF_STRENGTH *
                            (Vector3.Dot(normal, light) - Vector3.Dot(direction, light));
                        shade = MathHelper.Clamp(shade, MINIMUM_SHADE, MAXIMUM_SHADE);
                    }

                    camera.ToCameraSpace(ref _positions[index], out _vertices[index]);
                    _vertices[index].Attribute = direction;
                    _vertices[index].Shade = shade;
                }
            });

            for (int j = 0; j < SEGMENTS; j++)
            {
                int next = j == SEGMENTS - 1 ? 0 : j + 1;
                _triangles.Add(ref camera, ref _vertices[0], ref _vertices[1 + j], ref _vertices[1 + next]);
            }
            for (int ring = 0; ring + 1 < rings; ring++)
            {
                int offset = 1 + ring * SEGMENTS;
                for (int j = 0; j < SEGMENTS; j++)
                {
                    int next = j == SEGMENTS - 1 ? 0 : j + 1;
                    int i00 = offset + j;
                    int i01 = offset + next;
                    int i10 = i00 + SEGMENTS;
                    int i11 = i01 + SEGMENTS;
                    _triangles.Add(ref camera, ref _vertices[i00], ref _vertices[i10], ref _vertices[i01]);
                    _triangles.Add(ref camera, ref _vertices[i01], ref _vertices[i10], ref _vertices[i11]);
                }
            }

            if (Colors != null)
                TerrainRasterizer.SetMip(_triangles, 0, Colors);
            _triangles.Bin(camera.Height);
        }

        // Runs a ring body on the paint threads (Build is on the paint coordinator, which joins in), or inline
        // for a handful of rings.
        static void ForEachRing(int rings, Action<int> body)
        {
            if (rings < 8)
            {
                for (int ring = 0; ring < rings; ring++)
                    body(ring);
                return;
            }
            PaintWorkers.For(0, rings, body);
        }

        // How much of the terrain's relief remains at a distance from the camera.
        float Relief(double distance)
        {
            if (SphereRadius <= 0d || SpherizeEnd <= SpherizeStart)
                return ReliefScale;
            double t = MathHelper.Clamp((distance - SpherizeStart) / (SpherizeEnd - SpherizeStart), 0d, 1d);
            return ReliefScale * (float)(1d - t * t * (3d - 2d * t));
        }

        // A mesh vertex: the sampled ground, morphed toward the sphere by its distance from the camera.
        // Spherizing only ever lowers the ground: high ground flattens down to the sphere, ground below it
        // keeps its true height. Drawn ground is then never above the real one, so nothing resting on it
        // (ships, boulders, bases) can be buried, and nothing is moved from where it really is.
        Vector3D Spherize(Vector3 direction, double radius)
        {
            Vector3D point = (Vector3D)direction * radius;
            if (SphereRadius <= 0d || radius <= SphereRadius)
                return point;
            float relief = Relief(Vector3D.Distance(point, Camera.Position));
            return (Vector3D)direction * (SphereRadius + (radius - SphereRadius) * relief);
        }

        // A true radius morphed the same way (only ever lowered), at a distance from the camera.
        double SpherizedRadius(double radius, double distance)
        {
            if (SphereRadius <= 0d || radius <= SphereRadius)
                return radius;
            return SphereRadius + (radius - SphereRadius) * Relief(distance);
        }

        // A pixel's sample, morphed the same way (only ever lowered): its ground and lighting normal, not its
        // material inputs.
        void Spherize(ref PlanetSurfaceSample sample, Vector3 up, double distance)
        {
            if (SphereRadius <= 0d || sample.Radius <= SphereRadius)
                return;
            float relief = Relief(distance);
            if (relief >= 1f)
                return;
            sample.Radius = SphereRadius + (sample.Radius - SphereRadius) * relief;
            Vector3 normal = up + (sample.Normal - up) * relief;
            if (normal.Normalize() > 1e-6f)
                sample.Normal = normal;
        }

        // A vertex's height from a planet-fixed grid at its ring's cell size, bilinear between the four grid
        // points around it (geometry clipmaps). However the lattice slides, a vertex then moves along the same
        // fixed surface instead of picking up whatever height lies under it, so far mountains hold their shape.
        double SampleOnGrid(Vector3 direction, double cellMeters, double reference)
        {
            double step = cellMeters / (reference * MathHelper.PiOver2);
            // Fine cells sample directly: below a heightmap texel the patch is already smooth.
            if (!(step > 0d) || cellMeters <= Heights.TexelMeters)
                return Heights.SampleRadius(direction, cellMeters);
            // A power-of-two step in face units, so every ring's grid nests in the next one.
            step = Math.Pow(2d, Math.Floor(Math.Log(step, 2d)));
            PlanetCubeFace face;
            float u;
            float v;
            PlanetMapSource.DirectionToFaceUv(direction, out face, out u, out v);
            double gx = u / step;
            double gy = v / step;
            double ix = Math.Floor(gx);
            double iy = Math.Floor(gy);
            double tx = gx - ix;
            double ty = gy - iy;
            double footprint = step * reference * MathHelper.PiOver2;
            double r00 = GridRadius(face, ix * step, iy * step, footprint);
            double r10 = GridRadius(face, (ix + 1d) * step, iy * step, footprint);
            double r01 = GridRadius(face, ix * step, (iy + 1d) * step, footprint);
            double r11 = GridRadius(face, (ix + 1d) * step, (iy + 1d) * step, footprint);
            double top = r00 + (r10 - r00) * tx;
            double bottom = r01 + (r11 - r01) * tx;
            return top + (bottom - top) * ty;
        }

        double GridRadius(PlanetCubeFace face, double u, double v, double footprint)
        {
            // Points just past an edge are still valid directions; the sampler finds their own face.
            return Heights.SampleRadius(PlanetMapSource.FaceUvToDirection(face, (float)u, (float)v), footprint);
        }

        // A direction moved to the centre of its cell on a grid of the cube face's coordinates; step in face
        // units (0..1 across a face).
        static Vector3 SnapToFaceGrid(Vector3 direction, double step)
        {
            if (direction.Normalize() <= 1e-6f || !(step > 1e-9) || step >= 0.5d)
                return direction;
            PlanetCubeFace face;
            float u;
            float v;
            PlanetMapSource.DirectionToFaceUv(direction, out face, out u, out v);
            u = (float)MathHelper.Clamp((Math.Floor(u / step) + 0.5d) * step, 0d, 1d);
            v = (float)MathHelper.Clamp((Math.Floor(v / step) + 0.5d) * step, 0d, 1d);
            return PlanetMapSource.FaceUvToDirection(face, u, v);
        }

        // Printed-map convention: light from the north-west of a north-up map, 45 degrees above the ground.
        public static Vector3 GetMapLight(Vector3 center, Vector3 north)
        {
            Vector3 northTangent = north - center * Vector3.Dot(north, center);
            if (northTangent.Normalize() <= 1e-4f)
            {
                northTangent = Vector3.Cross(center, Math.Abs(center.X) < 0.9f ? Vector3.Right : Vector3.Up);
                northTangent.Normalize();
            }
            Vector3 eastTangent = Vector3.Cross(center, northTangent);
            Vector3 light = center + (northTangent - eastTangent) * 0.70710678f;
            light.Normalize();
            return light;
        }

        public void Draw(PixelCanvas canvas, float[] depthBuffer, int firstRow, int endRow)
        {
            if (_triangles.Count == 0 || depthBuffer == null)
                return;
            TerrainRasterizer.RasterizeTerrain(canvas.Width, canvas.Height, depthBuffer, _triangles, _pixels,
                firstRow, endRow);
            Resolve(canvas, depthBuffer, firstRow, endRow);
        }

        // Second half of the terrain pass, on this band only: every pixel the terrain won is shaded once.
        // Later layers (blocks, voxel maps) draw after this and depth-test against the terrain.
        //
        // The ground under a pixel (the refined ray hit, its height, slope and normal) is the costliest part of
        // a pixel and changes slowly across the screen: it is solved on the even pixels of even rows only, and
        // the pixels between take it interpolated (perspective-correct) from the two or four solved pixels
        // around them when those agree: same cube face, same kind of hit, similar normals, and a depth the
        // pixel's own mesh depth confirms. Silhouettes, ridges and anything else that disagrees are solved
        // per pixel as before. The material slot comes from the solved pixels too when they agree; textures are
        // still sampled at every pixel.
        void Resolve(PixelCanvas canvas, float[] depthBuffer, int firstRow, int endRow)
        {
            int width = canvas.Width;
            int height = canvas.Height;
            int last = Math.Min(height, endRow);
            int start = Math.Max(0, firstRow);
            if (start >= last)
                return;
            LatticeRows rows = LatticeFor(firstRow, endRow, width / 2 + 2);
            TerrainGeometry[] previous = rows.A;
            TerrainGeometry[] next = rows.B;
            int latticeY = start & ~1;
            SolveLatticeRow(depthBuffer, width, height, latticeY, previous);
            if (latticeY == start)
                ShadeLatticeRow(canvas, depthBuffer, width, latticeY, previous);
            for (int y = latticeY + 1; y < last; y += 2)
            {
                SolveLatticeRow(depthBuffer, width, height, y + 1, next);
                ShadeBetweenRows(canvas, depthBuffer, width, y, previous, next);
                if (y + 1 < last)
                    ShadeLatticeRow(canvas, depthBuffer, width, y + 1, next);
                TerrainGeometry[] swap = previous;
                previous = next;
                next = swap;
            }
        }

        // The ground at the even pixels of one even row; entries off the canvas or off the terrain stay empty.
        void SolveLatticeRow(float[] depthBuffer, int width, int height, int y, TerrainGeometry[] row)
        {
            int columns = width / 2 + 2;
            if (y >= height)
            {
                for (int i = 0; i < columns; i++)
                    row[i].State = GEOMETRY_NONE;
                return;
            }
            int[] stamps = _pixels.Stamps;
            int stamp = _pixels.Stamp;
            int offset = y * width;
            for (int i = 0; i < columns; i++)
            {
                int x = i * 2;
                if (x >= width || stamps[offset + x] != stamp)
                {
                    row[i].State = GEOMETRY_NONE;
                    continue;
                }
                int index = offset + x;
                SolveGeometry(x, y, _pixels.Directions[index], -depthBuffer[index], out row[i]);
                // The material is resolved here once; pixels between agreeing neighbours reuse it.
                if (row[i].State != GEOMETRY_NONE && Surface != null)
                    row[i].SlotPlusOne = Surface.ResolveSlotIndex(ref row[i].Sample, row[i].Direction) + 1;
            }
        }

        void ShadeLatticeRow(PixelCanvas canvas, float[] depthBuffer, int width, int y, TerrainGeometry[] row)
        {
            int[] stamps = _pixels.Stamps;
            int stamp = _pixels.Stamp;
            int offset = y * width;
            for (int x = 0; x < width; x++)
            {
                int index = offset + x;
                if (stamps[index] != stamp)
                    continue;
                if ((x & 1) == 0)
                {
                    canvas.SetPixel(x, y, ShadeSolved(x, y, index, depthBuffer, ref row[x >> 1]));
                    continue;
                }
                int left = x >> 1;
                ShadeInterpolated(canvas, depthBuffer, x, y, index,
                    ref row[left], ref row[left + 1], ref row[left], ref row[left + 1], 0.5f, 0.5f, 0f, 0f);
            }
        }

        void ShadeBetweenRows(PixelCanvas canvas, float[] depthBuffer, int width, int y,
            TerrainGeometry[] above, TerrainGeometry[] below)
        {
            int[] stamps = _pixels.Stamps;
            int stamp = _pixels.Stamp;
            int offset = y * width;
            for (int x = 0; x < width; x++)
            {
                int index = offset + x;
                if (stamps[index] != stamp)
                    continue;
                int left = x >> 1;
                if ((x & 1) == 0)
                    ShadeInterpolated(canvas, depthBuffer, x, y, index,
                        ref above[left], ref below[left], ref above[left], ref below[left], 0.5f, 0.5f, 0f, 0f);
                else
                    ShadeInterpolated(canvas, depthBuffer, x, y, index,
                        ref above[left], ref above[left + 1], ref below[left], ref below[left + 1],
                        0.25f, 0.25f, 0.25f, 0.25f);
            }
        }

        // A lattice pixel: its own solved ground, or (far away, or nothing solved) the plain per-pixel path.
        Color ShadeSolved(int x, int y, int index, float[] depthBuffer, ref TerrainGeometry geometry)
        {
            if (geometry.State == GEOMETRY_NONE)
                return ShadePixel(x, y, _pixels.Directions[index], -depthBuffer[index], _pixels.Shades[index],
                    _pixels.Mips[index]);
            return ShadeGeometry(ref geometry, _pixels.Directions[index], _pixels.Shades[index],
                _pixels.Mips[index]);
        }

        // A pixel between solved ones: their ground blended when they agree with each other and with this
        // pixel's mesh depth, otherwise solved here. Weights of zero leave a corner out.
        void ShadeInterpolated(PixelCanvas canvas, float[] depthBuffer, int x, int y, int index,
            ref TerrainGeometry a, ref TerrainGeometry b, ref TerrainGeometry c, ref TerrainGeometry d,
            float wa, float wb, float wc, float wd)
        {
            float depth = -depthBuffer[index];
            Vector3 direction = _pixels.Directions[index];
            float shade = _pixels.Shades[index];
            int mip = _pixels.Mips[index];
            TerrainGeometry blended;
            if (depth > 0f && TryBlend(1f / depth, ref a, ref b, ref c, ref d, wa, wb, wc, wd, out blended))
            {
                canvas.SetPixel(x, y, ShadeGeometry(ref blended, direction, shade, mip));
                return;
            }
            canvas.SetPixel(x, y, ShadePixel(x, y, direction, depth, shade, mip));
        }

        // Normals of neighbouring solved pixels must agree this well (about 25 degrees) to be blended.
        const float BLEND_MINIMUM_NORMAL_DOT = 0.9f;
        // The pixel's mesh depth may differ this much (relative, in inverse depth) from its neighbours' blend.
        const float BLEND_DEPTH_TOLERANCE = 0.02f;
        // Neighbours' ray refinements (refined over mesh depth) may differ this much (ratio): hits pulled to the
        // same ground move alike, a ray that jumped to another hill does not. Grazing ground, whose depth
        // itself changes fast across the screen, still passes.
        const float BLEND_MAXIMUM_REFINE_RATIO = 1.03f;

        bool TryBlend(float inverseDepth, ref TerrainGeometry a, ref TerrainGeometry b, ref TerrainGeometry c,
            ref TerrainGeometry d, float wa, float wb, float wc, float wd, out TerrainGeometry result)
        {
            result = default(TerrainGeometry);
            byte state = a.State;
            if (state == GEOMETRY_NONE ||
                !Compatible(ref a, ref b) ||
                (wc > 0f && (!Compatible(ref a, ref c) || !Compatible(ref a, ref d) || !Compatible(ref b, ref c) ||
                             !Compatible(ref b, ref d) || !Compatible(ref c, ref d))))
                return false;

            // The mesh is planar per triangle: its inverse depth is linear on screen, so a pixel whose own mesh
            // depth matches its neighbours' blend lies on the same surface as they do.
            float meshExpected = a.MeshInverseDepth * wa + b.MeshInverseDepth * wb +
                                 c.MeshInverseDepth * wc + d.MeshInverseDepth * wd;
            if (Math.Abs(meshExpected - inverseDepth) > BLEND_DEPTH_TOLERANCE * inverseDepth)
                return false;

            // Perspective-correct weights: screen weights over the refined hits' depths.
            float pa = wa * a.InverseDepth;
            float pb = wb * b.InverseDepth;
            float pc = wc * c.InverseDepth;
            float pd = wd * d.InverseDepth;
            float total = pa + pb + pc + pd;
            if (!(total > 0f))
                return false;
            float inverseTotal = 1f / total;
            pa *= inverseTotal;
            pb *= inverseTotal;
            pc *= inverseTotal;
            pd *= inverseTotal;

            result.State = state;
            result.Sample.Face = a.Sample.Face;
            result.Sample.U = a.Sample.U * pa + b.Sample.U * pb + c.Sample.U * pc + d.Sample.U * pd;
            result.Sample.V = a.Sample.V * pa + b.Sample.V * pb + c.Sample.V * pc + d.Sample.V * pd;
            result.Sample.NormalizedHeight = a.Sample.NormalizedHeight * pa + b.Sample.NormalizedHeight * pb +
                                             c.Sample.NormalizedHeight * pc + d.Sample.NormalizedHeight * pd;
            result.Sample.SlopeCosine = a.Sample.SlopeCosine * pa + b.Sample.SlopeCosine * pb +
                                        c.Sample.SlopeCosine * pc + d.Sample.SlopeCosine * pd;
            result.Sample.Radius = a.Sample.Radius * pa + b.Sample.Radius * pb +
                                   c.Sample.Radius * pc + d.Sample.Radius * pd;
            Vector3 normal = a.Sample.Normal * pa + b.Sample.Normal * pb + c.Sample.Normal * pc + d.Sample.Normal * pd;
            if (normal.Normalize() <= 1e-6f)
                return false;
            result.Sample.Normal = normal;
            Vector3 direction = a.Direction * pa + b.Direction * pb + c.Direction * pc + d.Direction * pd;
            if (direction.Normalize() <= 1e-6f)
                return false;
            result.Direction = direction;
            result.Distance = a.Distance * pa + b.Distance * pb + c.Distance * pc + d.Distance * pd;
            result.Footprint = a.Footprint * pa + b.Footprint * pb + c.Footprint * pc + d.Footprint * pd;
            result.Fine = a.Fine * pa + b.Fine * pb + c.Fine * pc + d.Fine * pd;
            // Neighbours on the same material share it; at a border the pixel resolves its own.
            int slot = a.SlotPlusOne;
            result.SlotPlusOne = slot != 0 && b.SlotPlusOne == slot &&
                                 (!(wc > 0f) || (c.SlotPlusOne == slot && d.SlotPlusOne == slot))
                ? slot
                : 0;
            return true;
        }

        static bool Compatible(ref TerrainGeometry a, ref TerrainGeometry b)
        {
            if (b.State != a.State || b.Sample.Face != a.Sample.Face)
                return false;
            if (Vector3.Dot(a.Sample.Normal, b.Sample.Normal) < BLEND_MINIMUM_NORMAL_DOT)
                return false;
            float refineA = a.InverseDepth * b.MeshInverseDepth;
            float refineB = b.InverseDepth * a.MeshInverseDepth;
            return Math.Max(refineA, refineB) <= Math.Min(refineA, refineB) * BLEND_MAXIMUM_REFINE_RATIO;
        }

        const byte GEOMETRY_NONE = 0;
        const byte GEOMETRY_REFINED = 1;
        const byte GEOMETRY_MESH = 2;

        /// <summary>
        /// The ground a close pixel shows: the sample, its unit direction (the point is direction times the
        /// sample's radius), the distance along the ray and the pixel's ground footprint. Empty for pixels far
        /// enough that the cubemap alone shades them.
        /// </summary>
        struct TerrainGeometry
        {
            public PlanetSurfaceSample Sample;
            public Vector3 Direction;
            public double Distance;
            public double Footprint;
            public float Fine;
            public float InverseDepth;
            public float MeshInverseDepth;
            // The material slot plus one; 0 until resolved (the shader resolves it per pixel).
            public int SlotPlusOne;
            public byte State;
        }

        sealed class LatticeRows
        {
            public TerrainGeometry[] A;
            public TerrainGeometry[] B;
        }

        // Each band's two lattice rows (ping-pong), owned by that band: no locks between the band workers.
        readonly BandSlots<LatticeRows> _lattices = new BandSlots<LatticeRows>();

        LatticeRows LatticeFor(int firstRow, int endRow, int columns)
        {
            int band = _lattices.IndexOf(firstRow, endRow);
            LatticeRows rows = band < 0 ? null : _lattices[band];
            if (rows == null || rows.A.Length < columns)
            {
                rows = new LatticeRows { A = new TerrainGeometry[columns], B = new TerrainGeometry[columns] };
                if (band >= 0)
                    _lattices[band] = rows;
            }
            return rows;
        }

        // Far away (two or more heightmap texels per pixel) the mipmapped cubemap and mesh relief are already the
        // right average. Closer, evaluated as the voxel generator would, cross-fading over one texel.
        Color ShadePixel(int x, int y, Vector3 direction, float depth, float shade, int mip)
        {
            TerrainGeometry geometry;
            SolveGeometry(x, y, direction, depth, out geometry);
            if (geometry.State == GEOMETRY_NONE)
            {
                if (direction.Normalize() <= 1e-9f)
                    return MissingSurfaceColor;
                geometry.Direction = direction;
            }
            return ShadeGeometry(ref geometry, direction, shade, mip);
        }

        // The ground under one pixel; empty when the pixel is far enough for the cubemap alone.
        void SolveGeometry(int x, int y, Vector3 direction, float depth, out TerrainGeometry geometry)
        {
            geometry = default(TerrainGeometry);
            if (Surface == null || !(depth > 0f) || direction.Normalize() <= 1e-9f)
                return;

            double texel = Heights.TexelMeters;
            double footprint = depth * _inverseFocal;
            float fine = (float)MathHelper.Clamp((2d * texel - footprint) / texel, 0d, 1d);
            if (!(fine > 0f))
                return;

            // The mesh is rebuilt around the moving camera every frame, so the point it puts under a pixel
            // slides over the true surface as the view moves, and colors computed there swim, most on hills.
            // Re-solving the pixel's ray against the surface itself makes what it shows depend on the ground
            // alone. Distant pixels average many texels, where the slide is invisible.
            geometry.Fine = fine;
            geometry.MeshInverseDepth = 1f / depth;
            Vector3D point;
            double distance;
            double refinedDepth;
            if (RefineHit(x, y, depth, footprint, out geometry.Sample, out point, out distance, out refinedDepth))
            {
                geometry.State = GEOMETRY_REFINED;
                geometry.Direction = (Vector3)(point / Math.Max(1e-9, point.Length()));
                geometry.Distance = distance;
                geometry.Footprint = refinedDepth * _inverseFocal;
                geometry.InverseDepth = (float)(1d / Math.Max(1e-9, refinedDepth));
            }
            else
            {
                Heights.SampleSurface(direction, footprint, out geometry.Sample);
                Spherize(ref geometry.Sample, direction, depth);
                geometry.State = GEOMETRY_MESH;
                geometry.Direction = direction;
                geometry.Distance = depth;
                geometry.Footprint = footprint;
                geometry.InverseDepth = geometry.MeshInverseDepth;
            }
        }

        // The pixel's color from its ground (or, with no ground solved, from the cubemap at its mesh direction).
        Color ShadeGeometry(ref TerrainGeometry geometry, Vector3 meshDirection, float shade, int mip)
        {
            float fine = geometry.State == GEOMETRY_NONE ? 0f : geometry.Fine;
            Vector3 direction = geometry.Direction;
            PlanetSurfaceShader surface = Surface;
            bool waterRule = fine >= 1f && WaterVisible && surface.HasWater;

            // A fully close pixel replaces the cubemap's color (and, with a water level, its water) entirely.
            Color color = fine >= 1f ? MissingSurfaceColor
                : Colors == null ? MissingSurfaceColor : Colors.Sample(direction, mip);
            float water = 0f;
            PlanetColorCubemap waterOverlay = WaterVisible && Colors != null && !waterRule ? Colors.WaterOverlay : null;
            if (waterOverlay != null)
                water = waterOverlay.Sample(direction, mip).A / 255f;

            if (fine > 0f)
            {
                Vector3D point = (Vector3D)direction * geometry.Sample.Radius;
                Color evaluated = surface.Shade(ref geometry.Sample, direction, point, geometry.Distance,
                    geometry.Footprint, geometry.SlotPlusOne - 1);
                float lit = MathHelper.Clamp(
                    1f + RELIEF_STRENGTH * (Vector3.Dot(geometry.Sample.Normal, _light) -
                                            Vector3.Dot(direction, _light)),
                    MINIMUM_SHADE, MAXIMUM_SHADE);
                color = fine >= 1f ? evaluated : Color.Lerp(color, evaluated, fine);
                shade += (lit - shade) * fine;
                if (WaterVisible && surface.HasWater)
                {
                    float wet = geometry.Sample.NormalizedHeight <= surface.WaterLevel ? 1f : 0f;
                    water += (wet - water) * fine;
                }
            }

            if (water > 0f)
            {
                color = SceneRasterizer.Tint(color, WaterTint, (byte)(water * 255f + 0.5f));
                // Water is level: it keeps only a little of the seabed's relief.
                shade = 1f + (shade - 1f) * (1f - water * 0.75f);
            }
            return SceneRasterizer.Shade(color, shade);
        }

        // Steps the pixel's camera ray from the mesh hit to the true surface: each step intersects the ray
        // with the surface's tangent plane under the current point (Newton's method on the height field).
        // The correction is bounded, so a ray that would jump to another hill keeps the mesh hit instead.
        bool RefineHit(int x, int y, float depth, double footprint,
            out PlanetSurfaceSample sample, out Vector3D point, out double distance, out double refinedDepth)
        {
            refinedDepth = depth;
            SceneCamera camera = Camera;
            Vector3D ray = camera.GetRay(new Vector2(x + 0.5f, y + 0.5f));
            double along = Vector3D.Dot(ray, camera.Forward);
            sample = default(PlanetSurfaceSample);
            point = Vector3D.Zero;
            distance = depth;
            if (along <= 1e-6)
                return false;

            double start = depth / along;
            double t = start;
            double limit = REFINE_MAXIMUM_PIXELS * footprint + REFINE_MINIMUM_METERS;
            Vector3D current = camera.Position + ray * t;
            // The ground's normal is taken once, at the mesh hit; the steps after it only need heights, which
            // skips the tangent frame and lighting work of a full sample.
            Vector3D normal = Vector3D.Zero;
            double facing = 0d;
            for (int i = 0; i < REFINE_STEPS; i++)
            {
                double radius = current.Length();
                if (radius <= 1d)
                    return false;
                Vector3 up = (Vector3)(current / radius);
                double groundRadius;
                if (i == 0)
                {
                    Heights.SampleSurface(up, footprint, out sample);
                    Spherize(ref sample, up, t);
                    normal = sample.Normal;
                    facing = Vector3D.Dot(ray, normal);
                    // Grazing or back-facing: the plane step is unreliable.
                    if (facing > -0.05d)
                        return false;
                    groundRadius = sample.Radius;
                }
                else
                {
                    groundRadius = SpherizedRadius(Heights.SampleRadius(up, footprint), t);
                }
                Vector3D ground = (Vector3D)up * groundRadius;
                double step = Vector3D.Dot(ground - current, normal) / facing;
                t += step;
                if (Math.Abs(t - start) * -facing > limit || t <= camera.NearDepth)
                    return false;
                current = camera.Position + ray * t;
                if (Math.Abs(step) * -facing < footprint * 0.05d)
                    break;
            }

            double finalRadius = current.Length();
            Vector3 finalUp = (Vector3)(current / Math.Max(1e-9, finalRadius));
            // Most pixels move less than half a pixel along the ground: the first sample still describes them,
            // so the second full sample (the costliest part of a pixel) is only taken when the hit moved.
            if (Math.Abs(t - start) * -facing >= footprint * 0.5d)
            {
                Heights.SampleSurface(finalUp, t * along * _inverseFocal, out sample);
                Spherize(ref sample, finalUp, t);
            }
            point = (Vector3D)finalUp * sample.Radius;
            distance = t;
            refinedDepth = t * along;
            return true;
        }

        // Marched in steps proportional to the height above ground, then refined by bisection.
        public static bool TryIntersect(
            PlanetHeightCubemap heights,
            Vector3D origin,
            Vector3D ray,
            out Vector3D hit)
        {
            hit = origin;
            if (heights == null || ray.Normalize() <= 1e-9)
                return false;

            // Skip straight to the shell that contains every terrain sample.
            double b = Vector3D.Dot(origin, ray);
            double c = origin.LengthSquared() - heights.MaximumRadius * heights.MaximumRadius;
            double travelled = 0d;
            if (c > 0d)
            {
                double discriminant = b * b - c;
                if (discriminant < 0d || b > 0d)
                    return false;
                travelled = -b - Math.Sqrt(discriminant);
            }

            double step = Math.Max(2d, heights.TexelMeters * 0.25d);
            double limit = travelled + 2d * heights.MaximumRadius;
            double previous = travelled;
            for (int i = 0; i < 2048 && travelled <= limit; i++)
            {
                Vector3D point = origin + ray * travelled;
                double radius = point.Length();
                if (radius > heights.MaximumRadius + 1d && Vector3D.Dot(point, ray) > 0d)
                    return false;
                double above = radius - heights.SampleRadius((Vector3)(point / Math.Max(1e-9, radius)));
                if (above <= 0d)
                {
                    double low = previous;
                    double high = travelled;
                    for (int refine = 0; refine < 24; refine++)
                    {
                        double middle = (low + high) * 0.5d;
                        Vector3D candidate = origin + ray * middle;
                        double candidateRadius = candidate.Length();
                        if (candidateRadius - heights.SampleRadius(
                                (Vector3)(candidate / Math.Max(1e-9, candidateRadius))) <= 0d)
                            high = middle;
                        else
                            low = middle;
                    }
                    hit = origin + ray * high;
                    return true;
                }
                previous = travelled;
                travelled += Math.Max(step, above * 0.5d);
            }
            return false;
        }
    }
}
