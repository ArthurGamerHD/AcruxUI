using System;
using System.IO;
using Acrux.Rendering;
using Adk.Image;
using Adk.Image.Dds;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Utils;

namespace Acrux.Bridge
{
    // A texture key is an LCD texture definition's sprite on LCDs and a transparent material in screen space. The
    // lookup runs on the render thread (definitions can be added at run time); the read and decode it returns run in
    // the background. Only DDS decodes.
    internal static class TexturePixelLoader
    {
        public static Func<TexturePixels> Load(string name, int maxSide)
        {
            MyDefinitionManager definitions = MyDefinitionManager.Static;
            if (string.IsNullOrWhiteSpace(name) || definitions == null)
                return null;

            MyStringHash id = MyStringHash.GetOrCompute(name);
            MyLCDTextureDefinition lcd = definitions.GetDefinition<MyLCDTextureDefinition>(id);
            if (lcd != null)
            {
                string path = string.IsNullOrWhiteSpace(lcd.SpritePath) ? lcd.TexturePath : lcd.SpritePath;
                MyModContext context = lcd.Context;
                if (!string.IsNullOrWhiteSpace(path))
                    return () => Read(path, context, maxSide);
            }

            foreach (MyTransparentMaterialDefinition material in definitions.GetTransparentMaterialDefinitions())
            {
                if (material != null && string.Equals(material.Id.SubtypeName, name, StringComparison.Ordinal))
                {
                    string path = material.Texture;
                    MyModContext context = material.Context;
                    return () => Read(path, context, maxSide);
                }
            }
            return null;
        }

        static TexturePixels Read(string path, MyModContext context, int maxSide)
        {
            string relative;
            string failure;
            if (string.IsNullOrWhiteSpace(path) || !ContentTexturePath.TryNormalize(path, out relative, out failure))
                return null;
            var utilities = MyAPIGateway.Utilities;
            if (utilities == null)
                return null;

            if (context != null && !context.IsBaseGame && utilities.FileExistsInModLocation(relative, context.ModItem))
            {
                using (BinaryReader reader = utilities.ReadBinaryFileInModLocation(relative, context.ModItem))
                {
                    TexturePixels pixels = Decode(reader, maxSide);
                    if (pixels != null)
                        return pixels;
                }
            }
            if (!utilities.FileExistsInGameContent(relative))
                return null;
            using (BinaryReader reader = utilities.ReadBinaryFileInGameContent(relative))
                return Decode(reader, maxSide);
        }

        static TexturePixels Decode(BinaryReader reader, int maxSide)
        {
            if (reader == null)
                return null;
            RawRgbaBitmap bitmap;
            string failure;
            if (!DdsDecoder.TryDecodeMipAtMost(reader.BaseStream, maxSide, out bitmap, out failure) ||
                bitmap.Width < 1 || bitmap.Height < 1)
                return null;
            // The bitmap is tightly packed (stride = width * 4), the layout TexturePixels takes.
            return new TexturePixels(bitmap.Width, bitmap.Height, bitmap.Pixels);
        }
    }
}
