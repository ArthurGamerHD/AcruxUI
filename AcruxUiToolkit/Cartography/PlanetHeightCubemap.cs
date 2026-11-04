using System;
using VRageMath;

namespace Acrux.Cartography
{
    /// <summary>One evaluated point of the planet surface, as the voxel generator computes it.</summary>
    internal struct PlanetSurfaceSample
    {
        public PlanetCubeFace Face;
        public float U;
        public float V;
        // In 0..1 of the hill range, without detail: what material rules and water test.
        public float NormalizedHeight;
        // The generator's slope measure: the heightmap normal's vertical component (1 is flat).
        public float SlopeCosine;
        // Detail included.
        public double Radius;
        // Planet-local, detail included.
        public Vector3 Normal;
    }

    /// <summary>
    /// The planet's source heightmap faces, kept beside a full-resolution color cubemap so the 3D surface view
    /// can displace its terrain mesh and light each pixel on a worker. Sampling follows MyPlanetShapeProvider:
    /// a Catmull-Rom patch over the heightmap texels plus the generator's slope-weighted detail texture, so the
    /// surface keeps its shape below one heightmap texel. Immutable: the face arrays are the ones the map
    /// source decoded.
    /// </summary>
    internal sealed class PlanetHeightCubemap
    {
        readonly ushort[][] _faces;
        readonly int _resolution;
        readonly double _radius;
        readonly double _minimumHillMeters;
        readonly double _hillRangeMeters;
        // Heightmap texel size over the hill range: the vertical component of the generator's normal.
        readonly float _mapStepScale;
        readonly PlanetDetailMap _detail;

        public PlanetHeightCubemap(
            ushort[][] faces,
            int resolution,
            double radiusMeters,
            double minimumHillRatio,
            double maximumHillRatio,
            ushort minimumHeight,
            ushort maximumHeight,
            PlanetDetailMap detail)
        {
            if (faces == null || faces.Length != 6)
                throw new ArgumentException("A height cubemap must contain six faces.", nameof(faces));
            _faces = faces;
            _resolution = resolution;
            _radius = radiusMeters;
            _minimumHillMeters = minimumHillRatio * radiusMeters;
            _hillRangeMeters = Math.Max(1e-3d, (maximumHillRatio - minimumHillRatio) * radiusMeters);
            _mapStepScale = (float)(TexelMeters / _hillRangeMeters);
            _detail = detail;
            MinimumNormalizedHeight = minimumHeight / 65535f;
            MaximumNormalizedHeight = maximumHeight / 65535f;

            // The patch overshoots its texels a little on sharp ridges, and detail only ever raises the ground.
            double overshoot = (maximumHeight - minimumHeight) / 65535d * _hillRangeMeters * 0.1d + 1d;
            MinimumRadius = RadiusOf(MinimumNormalizedHeight) - overshoot;
            MaximumRadius = RadiusOf(MaximumNormalizedHeight) + overshoot +
                            (detail == null ? 0d : detail.ScaleMeters);
        }

        public int Resolution
        {
            get { return _resolution; }
        }

        public double MinimumRadius { get; private set; }
        public double MaximumRadius { get; private set; }

        public float MinimumNormalizedHeight { get; private set; }
        public float MaximumNormalizedHeight { get; private set; }

        public double TexelMeters
        {
            get { return _radius * MathHelper.PiOver2 / Math.Max(1, _resolution); }
        }

        public double SampleRadius(Vector3 direction)
        {
            return SampleRadius(direction, TexelMeters);
        }

        // Detail filtered to footprintMeters, so a coarse mesh vertex does not alias it.
        public double SampleRadius(Vector3 direction, double footprintMeters)
        {
            PlanetCubeFace face;
            float u;
            float v;
            PlanetMapSource.DirectionToFaceUv(direction, out face, out u, out v);
            float gradientS;
            float gradientT;
            float height = SampleBicubic(_faces[(int)face], u, v, out gradientS, out gradientT);
            double radius = RadiusOf(height);
            if (_detail != null)
            {
                float detailS;
                float detailT;
                radius += _detail.Sample(u, v, SlopeCosine(gradientS, gradientT), footprintMeters,
                    _resolution, out detailS, out detailT);
            }
            return radius;
        }

        public void SampleSurface(Vector3 direction, double footprintMeters, out PlanetSurfaceSample sample)
        {
            sample = default(PlanetSurfaceSample);
            PlanetMapSource.DirectionToFaceUv(direction, out sample.Face, out sample.U, out sample.V);
            float gradientS;
            float gradientT;
            float height = SampleBicubic(_faces[(int)sample.Face], sample.U, sample.V, out gradientS, out gradientT);
            sample.NormalizedHeight = height;
            sample.SlopeCosine = SlopeCosine(gradientS, gradientT);
            sample.Radius = RadiusOf(height);

            // Height change per texel step, in metres.
            float riseS = (float)(gradientS * _hillRangeMeters);
            float riseT = (float)(gradientT * _hillRangeMeters);
            if (_detail != null)
            {
                float detailS;
                float detailT;
                sample.Radius += _detail.Sample(sample.U, sample.V, sample.SlopeCosine, footprintMeters,
                    _resolution, out detailS, out detailT);
                riseS += detailS;
                riseT += detailT;
            }

            // Ground step of one texel along the face axes, from the cube projection itself: the derivative of
            // normalize(cube point) along the face's u and v axes (the same as the central difference over one
            // texel, without four normalized projections per sample).
            Vector3 axisU;
            Vector3 axisV;
            float major = FaceAxes(sample.Face, direction, out axisU, out axisV);
            float scale = 2f * major / Math.Max(1, _resolution - 1) * (float)sample.Radius;
            Vector3 alongS = (axisU - direction * Vector3.Dot(direction, axisU)) * scale;
            Vector3 alongT = (axisV - direction * Vector3.Dot(direction, axisV)) * scale;
            Vector3 normal = Vector3.Cross(alongS + direction * riseS, alongT + direction * riseT);
            if (normal.Normalize() <= 1e-9f)
                normal = direction;
            else if (Vector3.Dot(normal, direction) < 0f)
                normal = -normal;
            sample.Normal = normal;
        }

        // Change per unit of face u and v (halved: the raw -1..1 coordinate runs twice as fast), matching
        // PlanetMapSource.FaceUvToDirection.
        static float FaceAxes(PlanetCubeFace face, Vector3 direction, out Vector3 axisU, out Vector3 axisV)
        {
            switch (face)
            {
                case PlanetCubeFace.Left:
                    axisU = new Vector3(0f, 0f, -1f);
                    axisV = new Vector3(0f, -1f, 0f);
                    return Math.Abs(direction.X);
                case PlanetCubeFace.Right:
                    axisU = new Vector3(0f, 0f, 1f);
                    axisV = new Vector3(0f, -1f, 0f);
                    return Math.Abs(direction.X);
                case PlanetCubeFace.Up:
                    axisU = new Vector3(-1f, 0f, 0f);
                    axisV = new Vector3(0f, 0f, -1f);
                    return Math.Abs(direction.Y);
                case PlanetCubeFace.Down:
                    axisU = new Vector3(1f, 0f, 0f);
                    axisV = new Vector3(0f, 0f, -1f);
                    return Math.Abs(direction.Y);
                case PlanetCubeFace.Back:
                    axisU = new Vector3(1f, 0f, 0f);
                    axisV = new Vector3(0f, -1f, 0f);
                    return Math.Abs(direction.Z);
                default:
                    axisU = new Vector3(-1f, 0f, 0f);
                    axisV = new Vector3(0f, -1f, 0f);
                    return Math.Abs(direction.Z);
            }
        }

        float SlopeCosine(float gradientS, float gradientT)
        {
            float step = _mapStepScale;
            return step / (float)Math.Sqrt(gradientS * gradientS + gradientT * gradientT + step * step);
        }

        // The generator's centripetal Catmull-Rom patch (tau 0.5) over the 4x4 texels around the sample. Face edges
        // clamp; the game pads them with the neighbouring face instead.
        float SampleBicubic(ushort[] data, float u, float v, out float gradientS, out float gradientT)
        {
            int resolution = _resolution;
            int last = resolution - 1;
            float x = u * last;
            float y = v * last;
            int x0 = (int)x;
            int y0 = (int)y;
            if (x0 < 0) x0 = 0; else if (x0 > last) x0 = last;
            if (y0 < 0) y0 = 0; else if (y0 > last) y0 = last;
            float s = x - x0;
            float t = y - y0;

            float ws0, ws1, ws2, ws3, ds0, ds1, ds2, ds3;
            CatmullRom(s, out ws0, out ws1, out ws2, out ws3, out ds0, out ds1, out ds2, out ds3);
            float wt0, wt1, wt2, wt3, dt0, dt1, dt2, dt3;
            CatmullRom(t, out wt0, out wt1, out wt2, out wt3, out dt0, out dt1, out dt2, out dt3);

            int c0 = Math.Max(0, x0 - 1);
            int c1 = x0;
            int c2 = Math.Min(last, x0 + 1);
            int c3 = Math.Min(last, x0 + 2);
            float height = 0f;
            float alongS = 0f;
            float alongT = 0f;
            for (int j = 0; j < 4; j++)
            {
                int row = y0 - 1 + j;
                if (row < 0) row = 0; else if (row > last) row = last;
                row *= resolution;
                float p0 = data[row + c0];
                float p1 = data[row + c1];
                float p2 = data[row + c2];
                float p3 = data[row + c3];
                float value = p0 * ws0 + p1 * ws1 + p2 * ws2 + p3 * ws3;
                float slope = p0 * ds0 + p1 * ds1 + p2 * ds2 + p3 * ds3;
                float wt = j == 0 ? wt0 : j == 1 ? wt1 : j == 2 ? wt2 : wt3;
                float dt = j == 0 ? dt0 : j == 1 ? dt1 : j == 2 ? dt2 : dt3;
                height += value * wt;
                alongS += slope * wt;
                alongT += value * dt;
            }

            const float scale = 1f / 65535f;
            gradientS = alongS * scale;
            gradientT = alongT * scale;
            return height * scale;
        }

        static void CatmullRom(
            float s,
            out float w0, out float w1, out float w2, out float w3,
            out float d0, out float d1, out float d2, out float d3)
        {
            float s2 = s * s;
            float s3 = s2 * s;
            w0 = 0.5f * (-s3 + 2f * s2 - s);
            w1 = 0.5f * (3f * s3 - 5f * s2 + 2f);
            w2 = 0.5f * (-3f * s3 + 4f * s2 + s);
            w3 = 0.5f * (s3 - s2);
            d0 = 0.5f * (-3f * s2 + 4f * s - 1f);
            d1 = 0.5f * (9f * s2 - 10f * s);
            d2 = 0.5f * (-9f * s2 + 8f * s + 1f);
            d3 = 0.5f * (3f * s2 - 2f * s);
        }

        double RadiusOf(float normalizedHeight)
        {
            return _radius + _minimumHillMeters + normalizedHeight * _hillRangeMeters;
        }
    }
}
