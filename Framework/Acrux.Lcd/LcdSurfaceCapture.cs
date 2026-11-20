#if DEBUG
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using VRage.Game.GUI.TextPanel;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Acrux.Lcd
{
    // Dev builds only: MDK drops this file from release packs. "/acrux capture" saves a surface's frame (its sprites
    // and the pixels of the generated textures they draw) to a file the preview host renders to a PNG with
    // "--render-capture". Every surface keeps its last composed frame, which is what it shows, so a capture needs no
    // redraw; and the game cannot read a generated texture back, so every texture keeps a copy of what it was last
    // painted with.
    internal static class LcdSurfaceCapture
    {
        internal sealed class Frame
        {
            public string SurfaceName = string.Empty;
            public string DisplayName = string.Empty;
            public string BlockName = string.Empty;
            public Vector2 TextureSize;
            public Vector2 SurfaceSize;
            public Color ScriptBackground;
            public readonly List<MySprite> Sprites = new List<MySprite>();
            public readonly Dictionary<string, Pixels> Textures = new Dictionary<string, Pixels>(StringComparer.Ordinal);
        }

        // Premultiplied RGBA8 rows, as the texture was painted. Never changed once recorded.
        internal struct Pixels
        {
            public int Width;
            public int Height;
            public byte[] Rgba;
        }

        // Built on the thread that composed it and replaced whole, so another thread can read it.
        sealed class Composed
        {
            public MySprite[] Sprites;
            public long Ticks;
        }

        // Surfaces and textures update from screens rendering in parallel.
        static readonly ConcurrentDictionary<string, Pixels> Painted = new ConcurrentDictionary<string, Pixels>();
        static readonly ConcurrentDictionary<IMyTextSurface, Composed> LastFrames =
            new ConcurrentDictionary<IMyTextSurface, Composed>();

        internal static void RecordTexture(string name, Vector2I size, byte[] pixels)
        {
            if (name == null || pixels == null)
                return;
            var copy = new byte[pixels.Length];
            Buffer.BlockCopy(pixels, 0, copy, 0, pixels.Length);
            Painted[name] = new Pixels { Width = size.X, Height = size.Y, Rgba = copy };
        }

        internal static void ForgetTexture(string name)
        {
            Pixels removed;
            if (name != null)
                Painted.TryRemove(name, out removed);
        }

        // The cursor is left out: it only shows where the player looked.
        internal static void Record(IMyTextSurface surface, List<MySprite> content, List<MySprite> overlay)
        {
            var sprites = new MySprite[content.Count + overlay.Count];
            content.CopyTo(sprites, 0);
            overlay.CopyTo(sprites, content.Count);
            LastFrames[surface] = new Composed { Sprites = sprites, Ticks = DateTime.UtcNow.Ticks };
        }

        // Null when the surface has drawn no Acrux frame.
        internal static Frame Capture(IMyTextSurface surface, string blockName)
        {
            Composed composed;
            return surface != null && LastFrames.TryGetValue(surface, out composed)
                ? Snapshot(surface, blockName, composed.Sprites, null)
                : null;
        }

        // The surfaces that drew within maxAge (the others are forgotten: a screen that stopped drawing for that long
        // has most likely been removed).
        internal static List<IMyTextSurface> ActiveSurfaces(TimeSpan maxAge)
        {
            long oldest = DateTime.UtcNow.Ticks - maxAge.Ticks;
            var active = new List<IMyTextSurface>();
            foreach (var pair in LastFrames)
            {
                Composed removed;
                if (pair.Value.Ticks >= oldest)
                    active.Add(pair.Key);
                else
                    LastFrames.TryRemove(pair.Key, out removed);
            }
            return active;
        }

        internal static void Clear()
        {
            LastFrames.Clear();
        }

        internal static Frame Snapshot(IMyTextSurface surface, string blockName, IEnumerable<MySprite> content,
            IEnumerable<MySprite> overlay)
        {
            var frame = new Frame
            {
                SurfaceName = surface.Name ?? string.Empty,
                DisplayName = surface.DisplayName ?? string.Empty,
                BlockName = blockName ?? string.Empty,
                TextureSize = surface.TextureSize,
                SurfaceSize = surface.SurfaceSize,
                ScriptBackground = surface.ScriptBackgroundColor
            };
            frame.Sprites.AddRange(content);
            if (overlay != null)
                frame.Sprites.AddRange(overlay);
            foreach (MySprite sprite in frame.Sprites)
            {
                Pixels pixels;
                if (sprite.Type == SpriteType.TEXTURE && sprite.Data != null && !frame.Textures.ContainsKey(sprite.Data) &&
                    Painted.TryGetValue(sprite.Data, out pixels))
                    frame.Textures.Add(sprite.Data, pixels);
            }
            return frame;
        }

        const string MAGIC = "ACRUXCAP";
        const int VERSION = 1;

        // packed gives each texture's compressed pixels and inflate undoes it, which keeps this file free of a compression
        // library (the bridge harness host has none).
        internal static void Write(Frame frame, BinaryWriter writer, Func<string, byte[]> packed)
        {
            writer.Write(MAGIC.ToCharArray());
            writer.Write(VERSION);
            writer.Write(frame.SurfaceName);
            writer.Write(frame.DisplayName);
            writer.Write(frame.BlockName);
            writer.Write(frame.TextureSize.X);
            writer.Write(frame.TextureSize.Y);
            writer.Write(frame.SurfaceSize.X);
            writer.Write(frame.SurfaceSize.Y);
            writer.Write(frame.ScriptBackground.PackedValue);

            writer.Write(frame.Sprites.Count);
            foreach (MySprite sprite in frame.Sprites)
            {
                writer.Write((byte)sprite.Type);
                writer.Write(sprite.Data ?? string.Empty);
                byte present = (byte)((sprite.Position.HasValue ? 1 : 0) | (sprite.Size.HasValue ? 2 : 0) |
                                      (sprite.Color.HasValue ? 4 : 0) | (sprite.Data == null ? 8 : 0));
                writer.Write(present);
                if (sprite.Position.HasValue)
                {
                    writer.Write(sprite.Position.Value.X);
                    writer.Write(sprite.Position.Value.Y);
                }
                if (sprite.Size.HasValue)
                {
                    writer.Write(sprite.Size.Value.X);
                    writer.Write(sprite.Size.Value.Y);
                }
                if (sprite.Color.HasValue)
                    writer.Write(sprite.Color.Value.PackedValue);
                writer.Write(sprite.FontId ?? string.Empty);
                writer.Write((byte)sprite.Alignment);
                writer.Write(sprite.RotationOrScale);
            }

            writer.Write(frame.Textures.Count);
            foreach (var pair in frame.Textures)
            {
                byte[] data = packed(pair.Key);
                writer.Write(pair.Key);
                writer.Write(pair.Value.Width);
                writer.Write(pair.Value.Height);
                writer.Write(data.Length);
                writer.Write(data);
            }
        }

        internal static Frame Read(BinaryReader reader, Func<byte[], int, byte[]> inflate)
        {
            if (new string(reader.ReadChars(MAGIC.Length)) != MAGIC)
                throw new FormatException("Not an Acrux surface capture.");
            int version = reader.ReadInt32();
            if (version != VERSION)
                throw new FormatException("Unsupported capture version " + version + ".");
            var frame = new Frame
            {
                SurfaceName = reader.ReadString(),
                DisplayName = reader.ReadString(),
                BlockName = reader.ReadString(),
                TextureSize = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                SurfaceSize = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                ScriptBackground = new Color(reader.ReadUInt32())
            };

            int sprites = reader.ReadInt32();
            for (int i = 0; i < sprites; i++)
            {
                var type = (SpriteType)reader.ReadByte();
                string data = reader.ReadString();
                byte present = reader.ReadByte();
                Vector2? position = null, size = null;
                Color? color = null;
                if ((present & 1) != 0)
                    position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                if ((present & 2) != 0)
                    size = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                if ((present & 4) != 0)
                    color = new Color(reader.ReadUInt32());
                string font = reader.ReadString();
                var alignment = (TextAlignment)reader.ReadByte();
                float rotation = reader.ReadSingle();
                frame.Sprites.Add(new MySprite(type, (present & 8) != 0 ? null : data, position, size, color,
                    font.Length == 0 ? null : font, alignment, rotation));
            }

            int textures = reader.ReadInt32();
            for (int i = 0; i < textures; i++)
            {
                string name = reader.ReadString();
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                byte[] packed = reader.ReadBytes(reader.ReadInt32());
                frame.Textures[name] = new Pixels { Width = width, Height = height, Rgba = inflate(packed, width * height * 4) };
            }
            return frame;
        }
    }
}
#endif
