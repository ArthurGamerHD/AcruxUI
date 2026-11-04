using System;
using System.Collections.Generic;
using System.Text;
using Adk.Compression.Zip;
using VRage.Game;
using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>
    /// A grid's blocks kept in memory: enough to draw the grid without it (<see cref="GridSceneState.MemorySnapshot"/>)
    /// and to compare it with the grid later (<see cref="GridSceneState.ReferenceSnapshot"/>). Blocks are kept relative
    /// to a pivot block, so they still line up after the grid's own origin moved (blocks lost, split, merged): the
    /// pivot is found again by its entity id, or, once it is gone, the snapshot is matched against the grid's blocks.
    /// Immutable; make one with <see cref="GridSnapshotCapture"/> or <see cref="FromBytes"/>.
    /// </summary>
    public sealed class GridSnapshot
    {
        internal const byte FLAG_ENTITY = 1;
        internal const byte FLAG_FULL_CUBE = 2;
        internal const byte FLAG_WALL = 4;

        const uint MAGIC = 0x31534741; // "AGS1"
        const byte VERSION = 2;
        const byte BLOCK_MULTI_CELL = 1;
        const byte BLOCK_MODEL = 2;
        const int MAXIMUM_BLOCKS = 1 << 20;
        const int MAXIMUM_RAW_BYTES = 256 * 1024 * 1024;

        internal struct Definition
        {
            public MyDefinitionId Id;
            public float MaxIntegrity;
            public GridBlockCategory Category;
            public byte Flags;
        }

        // In the pivot's cells: the pivot block sits at the origin, looking down its own Forward.
        internal struct Block
        {
            public Vector3I Position;
            public Vector3I Min;
            public Vector3I Max;
            public Base6Directions.Direction Forward;
            public Base6Directions.Direction Up;
            public int Definition;
            public float Integrity;
            public int Color;
            // Index into Locals, or -1 for a block without a model of its own (armor).
            public int Local;
        }

        // A block's model and moving parts (turret barrels, doors), as they stood when captured.
        internal struct Local
        {
            public int Model;
            // Model space to the pivot's metres.
            public Matrix Matrix;
            public int FirstSubpart;
            public int SubpartCount;
        }

        internal struct Subpart
        {
            public int Model;
            public Matrix Matrix;
        }

        static readonly object KeyGate = new object();
        static long _nextKey = -1;

        internal Definition[] Definitions;
        internal string[] Models;
        internal Vector3[] Colors;
        internal Block[] Blocks;
        internal Local[] Locals;
        internal Subpart[] Subparts;
        // The pivot's cell and orientation in the grid when captured: the grid's own axes, for drawing it as it was.
        internal Vector3I PivotPosition;
        internal Base6Directions.Direction PivotForward = Base6Directions.Direction.Forward;
        internal Base6Directions.Direction PivotUp = Base6Directions.Direction.Up;
        // The grid's cells when captured (its own axes).
        internal Vector3I CapturedMin;
        internal Vector3I CapturedMax;
        // The grids mechanically attached to it (rotor, piston and hinge tops, and what is built on them), each a
        // snapshot of its own in its own cells; empty for a part.
        internal GridSnapshot[] Parts = new GridSnapshot[0];
        // A part's metres to the main snapshot's pivot metres, as it stood when captured.
        internal Matrix ToParent = Matrix.Identity;
        // Draw caches are kept per key; negative, so it never meets an entity id.
        internal readonly long CacheKey;

        internal GridSnapshot()
        {
            lock (KeyGate)
                CacheKey = _nextKey--;
        }

        /// <summary>Metres per cell: 2.5 for large grids, 0.5 for small ones.</summary>
        public float GridSize { get; internal set; }

        /// <summary>The pivot block's entity id, or zero when the grid had none to pick.</summary>
        public long PivotEntityId { get; internal set; }

        /// <summary>The grid's entity id when captured.</summary>
        public long GridEntityId { get; internal set; }

        /// <summary>The grid's name when captured.</summary>
        public string GridName { get; internal set; }

        /// <summary>When it was captured, as <see cref="DateTime.Ticks"/> in UTC.</summary>
        public long CapturedTicks { get; internal set; }

        /// <summary>How many blocks it holds, its subgrids' included.</summary>
        public int BlockCount
        {
            get
            {
                int count = Blocks == null ? 0 : Blocks.Length;
                for (int i = 0; i < Parts.Length; i++)
                    count += Parts[i].BlockCount;
                return count;
            }
        }

        // Pivot cells to the grid's cells as captured.
        internal MatrixD PivotToCaptured
        {
            get { return Pose(PivotPosition, PivotForward, PivotUp, 1d); }
        }

        internal static MatrixD Pose(Vector3I position, Base6Directions.Direction forward, Base6Directions.Direction up,
            double scale)
        {
            return MatrixD.CreateWorld(new Vector3D(position) * scale, Base6Directions.GetVector(forward),
                Base6Directions.GetVector(up));
        }

        /// <summary>
        /// The snapshot as bytes, compressed, for storage. Slow for large grids: call it off the game thread.
        /// </summary>
        public byte[] ToBytes()
        {
            var writer = new ByteWriter();
            Write(writer);
            byte[] raw = writer.ToArray();
            byte[] packed = Zlib.DeflateRaw(raw);
            var output = new byte[packed.Length + 4];
            output[0] = (byte)raw.Length;
            output[1] = (byte)(raw.Length >> 8);
            output[2] = (byte)(raw.Length >> 16);
            output[3] = (byte)(raw.Length >> 24);
            Buffer.BlockCopy(packed, 0, output, 4, packed.Length);
            return output;
        }

        void Write(ByteWriter writer)
        {
            writer.UInt32(MAGIC);
            writer.Byte(VERSION);
            writer.Single(GridSize);
            writer.Int64(PivotEntityId);
            writer.Int64(GridEntityId);
            writer.Int64(CapturedTicks);
            writer.String(GridName);
            WriteCell(writer, PivotPosition);
            writer.Byte((byte)PivotForward);
            writer.Byte((byte)PivotUp);
            WriteCell(writer, CapturedMin);
            WriteCell(writer, CapturedMax);

            writer.Int32(Definitions.Length);
            for (int i = 0; i < Definitions.Length; i++)
            {
                writer.String(Definitions[i].Id.ToString());
                writer.Single(Definitions[i].MaxIntegrity);
                writer.Byte((byte)Definitions[i].Category);
                writer.Byte(Definitions[i].Flags);
            }
            writer.Int32(Models.Length);
            for (int i = 0; i < Models.Length; i++)
                writer.String(Models[i]);
            writer.Int32(Colors.Length);
            for (int i = 0; i < Colors.Length; i++)
            {
                writer.Single(Colors[i].X);
                writer.Single(Colors[i].Y);
                writer.Single(Colors[i].Z);
            }

            writer.Int32(Blocks.Length);
            for (int i = 0; i < Blocks.Length; i++)
            {
                Block block = Blocks[i];
                bool multiCell = block.Min != block.Position || block.Max != block.Position;
                byte flags = (byte)((multiCell ? BLOCK_MULTI_CELL : 0) | (block.Local >= 0 ? BLOCK_MODEL : 0));
                writer.UInt16(block.Position.X);
                writer.UInt16(block.Position.Y);
                writer.UInt16(block.Position.Z);
                writer.Byte((byte)((int)block.Forward * 6 + (int)block.Up));
                writer.Byte(flags);
                writer.UInt16(block.Definition);
                writer.UInt16(block.Color);
                float maximum = Definitions[block.Definition].MaxIntegrity;
                float ratio = maximum > 0f ? MathHelper.Clamp(block.Integrity / maximum, 0f, 1f) : 1f;
                writer.UInt16((int)Math.Round(ratio * ushort.MaxValue));
                if (multiCell)
                {
                    Vector3I low = block.Min - block.Position;
                    Vector3I high = block.Max - block.Position;
                    writer.UInt16(low.X);
                    writer.UInt16(low.Y);
                    writer.UInt16(low.Z);
                    writer.UInt16(high.X);
                    writer.UInt16(high.Y);
                    writer.UInt16(high.Z);
                }
                if (block.Local < 0)
                    continue;
                Local local = Locals[block.Local];
                writer.UInt16(local.Model);
                WriteMatrix(writer, ref local.Matrix);
                writer.Byte((byte)local.SubpartCount);
                for (int s = 0; s < local.SubpartCount; s++)
                {
                    Subpart subpart = Subparts[local.FirstSubpart + s];
                    writer.UInt16(subpart.Model);
                    WriteMatrix(writer, ref subpart.Matrix);
                }
            }
            writer.Int32(Parts.Length);
            for (int i = 0; i < Parts.Length; i++)
            {
                Matrix toParent = Parts[i].ToParent;
                WriteMatrix(writer, ref toParent);
                Parts[i].Write(writer);
            }
        }

        /// <summary>
        /// A snapshot from <see cref="ToBytes"/>, or null when the bytes hold none. Slow for large grids: call it off
        /// the game thread. Blocks whose definition the game no longer has draw as their box.
        /// </summary>
        public static GridSnapshot FromBytes(byte[] data)
        {
            if (data == null || data.Length < 8)
                return null;
            try
            {
                int rawLength = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
                if (rawLength <= 0 || rawLength > MAXIMUM_RAW_BYTES)
                    return null;
                byte[] raw = Zlib.InflateRawDeflate(data, 4, data.Length - 4, rawLength);
                return Read(new ByteReader(raw));
            }
            catch (Exception)
            {
                return null;
            }
        }

        static GridSnapshot Read(ByteReader reader)
        {
            if (reader.UInt32() != MAGIC || reader.Byte() != VERSION)
                return null;
            var snapshot = new GridSnapshot
            {
                GridSize = reader.Single(),
                PivotEntityId = reader.Int64(),
                GridEntityId = reader.Int64(),
                CapturedTicks = reader.Int64(),
                GridName = reader.String(),
                PivotPosition = ReadCell(reader),
                PivotForward = ReadDirection(reader.Byte()),
                PivotUp = ReadDirection(reader.Byte()),
                CapturedMin = ReadCell(reader),
                CapturedMax = ReadCell(reader)
            };

            int count = ReadCount(reader);
            snapshot.Definitions = new Definition[count];
            for (int i = 0; i < count; i++)
            {
                MyDefinitionId id;
                MyDefinitionId.TryParse(reader.String(), out id);
                snapshot.Definitions[i] = new Definition
                {
                    Id = id,
                    MaxIntegrity = reader.Single(),
                    Category = (GridBlockCategory)reader.Byte(),
                    Flags = reader.Byte()
                };
            }
            count = ReadCount(reader);
            snapshot.Models = new string[count];
            for (int i = 0; i < count; i++)
                snapshot.Models[i] = reader.String();
            count = ReadCount(reader);
            snapshot.Colors = new Vector3[count];
            for (int i = 0; i < count; i++)
                snapshot.Colors[i] = new Vector3(reader.Single(), reader.Single(), reader.Single());

            count = ReadCount(reader);
            snapshot.Blocks = new Block[count];
            var locals = new List<Local>();
            var subparts = new List<Subpart>();
            for (int i = 0; i < count; i++)
            {
                var position = new Vector3I(reader.Int16(), reader.Int16(), reader.Int16());
                byte orientation = reader.Byte();
                byte flags = reader.Byte();
                var block = new Block
                {
                    Position = position,
                    Min = position,
                    Max = position,
                    Forward = ReadDirection((byte)(orientation / 6)),
                    Up = ReadDirection((byte)(orientation % 6)),
                    Definition = reader.UInt16(),
                    Color = reader.UInt16(),
                    Local = -1
                };
                if (block.Definition >= snapshot.Definitions.Length || block.Color >= snapshot.Colors.Length)
                    return null;
                block.Integrity = reader.UInt16() / (float)ushort.MaxValue *
                                  snapshot.Definitions[block.Definition].MaxIntegrity;
                if ((flags & BLOCK_MULTI_CELL) != 0)
                {
                    block.Min = position + new Vector3I(reader.Int16(), reader.Int16(), reader.Int16());
                    block.Max = position + new Vector3I(reader.Int16(), reader.Int16(), reader.Int16());
                }
                if ((flags & BLOCK_MODEL) != 0)
                {
                    var local = new Local { Model = reader.UInt16(), Matrix = ReadMatrix(reader) };
                    local.SubpartCount = reader.Byte();
                    local.FirstSubpart = subparts.Count;
                    if (local.Model >= snapshot.Models.Length)
                        return null;
                    for (int s = 0; s < local.SubpartCount; s++)
                    {
                        var subpart = new Subpart { Model = reader.UInt16(), Matrix = ReadMatrix(reader) };
                        if (subpart.Model >= snapshot.Models.Length)
                            return null;
                        subparts.Add(subpart);
                    }
                    block.Local = locals.Count;
                    locals.Add(local);
                }
                snapshot.Blocks[i] = block;
            }
            snapshot.Locals = locals.ToArray();
            snapshot.Subparts = subparts.ToArray();
            count = ReadCount(reader);
            snapshot.Parts = new GridSnapshot[count];
            for (int i = 0; i < count; i++)
            {
                Matrix toParent = ReadMatrix(reader);
                GridSnapshot part = Read(reader);
                if (part == null)
                    return null;
                part.ToParent = toParent;
                snapshot.Parts[i] = part;
            }
            return snapshot;
        }

        static int ReadCount(ByteReader reader)
        {
            int count = reader.Int32();
            if (count < 0 || count > MAXIMUM_BLOCKS)
                throw new Exception("Bad snapshot count");
            return count;
        }

        static Base6Directions.Direction ReadDirection(byte value)
        {
            if (value > 5)
                throw new Exception("Bad snapshot direction");
            return (Base6Directions.Direction)value;
        }

        static void WriteCell(ByteWriter writer, Vector3I cell)
        {
            writer.Int32(cell.X);
            writer.Int32(cell.Y);
            writer.Int32(cell.Z);
        }

        static Vector3I ReadCell(ByteReader reader)
        {
            return new Vector3I(reader.Int32(), reader.Int32(), reader.Int32());
        }

        static void WriteMatrix(ByteWriter writer, ref Matrix matrix)
        {
            writer.Single(matrix.M11);
            writer.Single(matrix.M12);
            writer.Single(matrix.M13);
            writer.Single(matrix.M21);
            writer.Single(matrix.M22);
            writer.Single(matrix.M23);
            writer.Single(matrix.M31);
            writer.Single(matrix.M32);
            writer.Single(matrix.M33);
            writer.Single(matrix.M41);
            writer.Single(matrix.M42);
            writer.Single(matrix.M43);
        }

        static Matrix ReadMatrix(ByteReader reader)
        {
            var matrix = Matrix.Identity;
            matrix.M11 = reader.Single();
            matrix.M12 = reader.Single();
            matrix.M13 = reader.Single();
            matrix.M21 = reader.Single();
            matrix.M22 = reader.Single();
            matrix.M23 = reader.Single();
            matrix.M31 = reader.Single();
            matrix.M32 = reader.Single();
            matrix.M33 = reader.Single();
            matrix.M41 = reader.Single();
            matrix.M42 = reader.Single();
            matrix.M43 = reader.Single();
            return matrix;
        }
        // MemoryStream is not whitelisted.
        sealed class ByteWriter
        {
            byte[] _data = new byte[64 * 1024];
            int _length;

            void Ensure(int count)
            {
                if (_length + count <= _data.Length)
                    return;
                var data = new byte[Math.Max(_data.Length * 2, _length + count)];
                Buffer.BlockCopy(_data, 0, data, 0, _length);
                _data = data;
            }

            public void Byte(byte value)
            {
                Ensure(1);
                _data[_length++] = value;
            }

            public void UInt16(int value)
            {
                Ensure(2);
                _data[_length++] = (byte)value;
                _data[_length++] = (byte)(value >> 8);
            }

            public void Int32(int value)
            {
                Ensure(4);
                _data[_length++] = (byte)value;
                _data[_length++] = (byte)(value >> 8);
                _data[_length++] = (byte)(value >> 16);
                _data[_length++] = (byte)(value >> 24);
            }

            public void UInt32(uint value)
            {
                Int32((int)value);
            }

            public void Int64(long value)
            {
                Int32((int)value);
                Int32((int)(value >> 32));
            }

            public void Single(float value)
            {
                byte[] bytes = BitConverter.GetBytes(value);
                Ensure(4);
                Buffer.BlockCopy(bytes, 0, _data, _length, 4);
                _length += 4;
            }

            public void String(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                Int32(bytes.Length);
                Ensure(bytes.Length);
                Buffer.BlockCopy(bytes, 0, _data, _length, bytes.Length);
                _length += bytes.Length;
            }

            public byte[] ToArray()
            {
                var data = new byte[_length];
                Buffer.BlockCopy(_data, 0, data, 0, _length);
                return data;
            }
        }

        sealed class ByteReader
        {
            readonly byte[] _data;
            int _offset;

            public ByteReader(byte[] data)
            {
                _data = data;
            }

            int Take(int count)
            {
                if (count < 0 || _offset > _data.Length - count)
                    throw new Exception("Snapshot data ends early");
                int at = _offset;
                _offset += count;
                return at;
            }

            public byte Byte()
            {
                return _data[Take(1)];
            }

            public short Int16()
            {
                int at = Take(2);
                return (short)(_data[at] | (_data[at + 1] << 8));
            }

            public int UInt16()
            {
                return (ushort)Int16();
            }

            public int Int32()
            {
                int at = Take(4);
                return _data[at] | (_data[at + 1] << 8) | (_data[at + 2] << 16) | (_data[at + 3] << 24);
            }

            public uint UInt32()
            {
                return (uint)Int32();
            }

            public long Int64()
            {
                long low = (uint)Int32();
                return low | ((long)Int32() << 32);
            }

            public float Single()
            {
                return BitConverter.ToSingle(_data, Take(4));
            }

            public string String()
            {
                int length = Int32();
                return Encoding.UTF8.GetString(_data, Take(length), length);
            }
        }
    }
}
