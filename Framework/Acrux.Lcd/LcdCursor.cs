using System;
using System.Collections.Generic;
using Acrux.Core.Input;
using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace Acrux.Lcd
{
    /// <summary>
    /// Draws the pointer on an LCD surface: one sprite set per <see cref="PointerCursor"/> style, from the
    /// Acrux_Cursor* LCD textures this mod ships.
    /// </summary>
    public static class LcdCursor
    {
        /// <summary>Cursor size in surface pixels at scale 1.</summary>
        public const float DefaultSize = 32f;

        // One LCD texture per style, indexed by the style's value: Acrux_Cursor + its name. Styles without art of their
        // own are defined as copies of the arrow (Acrux_LcdTextures.sbc), so each can get its own later.
        static readonly string[] Sprites =
        {
            "Acrux_CursorArrow",
            "Acrux_CursorIbeam",
            "Acrux_CursorWait",
            "Acrux_CursorCross",
            "Acrux_CursorUpArrow",
            "Acrux_CursorSizeWestEast",
            "Acrux_CursorSizeNorthSouth",
            "Acrux_CursorSizeAll",
            "Acrux_CursorNo",
            "Acrux_CursorHand",
            "Acrux_CursorAppStarting",
            "Acrux_CursorHelp",
            "Acrux_CursorTopSide",
            "Acrux_CursorBottomSide",
            "Acrux_CursorLeftSide",
            "Acrux_CursorRightSide",
            "Acrux_CursorTopLeftCorner",
            "Acrux_CursorTopRightCorner",
            "Acrux_CursorBottomLeftCorner",
            "Acrux_CursorBottomRightCorner",
            "Acrux_CursorDragMove",
            "Acrux_CursorDragCopy",
            "Acrux_CursorDragLink",
        };

        /// <summary>The LCD texture of a style; the arrow for Inherit and anything unknown, null for None.</summary>
        public static string GetSprite(PointerCursor cursor)
        {
            if (cursor == PointerCursor.None)
                return null;
            int index = (int)cursor;
            return index >= 0 && index < Sprites.Length ? Sprites[index] : Sprites[(int)PointerCursor.Arrow];
        }

        /// <summary>
        /// Appends the sprites of <paramref name="cursor"/> at <paramref name="position"/> and returns whether
        /// anything was drawn. Inherit draws the arrow; None, a zero scale or a NaN position draw nothing.
        /// </summary>
        public static bool Draw(List<MySprite> sprites, PointerCursor cursor, Vector2 position, float scale = 1f)
        {
            return Draw(sprites, cursor, position, new Vector2(DefaultSize), scale);
        }

        public static bool Draw(
            List<MySprite> sprites,
            PointerCursor cursor,
            Vector2 position,
            Vector2 size,
            float scale)
        {
            if (sprites == null)
                throw new ArgumentNullException(nameof(sprites));
            if (cursor == PointerCursor.None || scale <= 0f || float.IsNaN(scale) ||
                float.IsNaN(position.X) || float.IsNaN(position.Y))
                return false;

            if (cursor == PointerCursor.AppStarting)
            {
                // The arrow with a small busy wheel beside it.
                sprites.Add(Sprite(GetSprite(PointerCursor.AppStarting), position, size * scale, 0f));
                sprites.Add(Sprite(
                    GetSprite(PointerCursor.Wait),
                    position + new Vector2(size.X * scale / 4f, -size.Y * scale / 8f),
                    size * scale / 2f,
                    BusyRotation()));
                return true;
            }

            string sprite = GetSprite(cursor);
            float rotation = cursor == PointerCursor.Wait ? BusyRotation() : 0f;
            bool symbol = cursor == PointerCursor.Wait || cursor == PointerCursor.Cross ||
                          cursor == PointerCursor.No || cursor == PointerCursor.Help;

            if (symbol)
            {
                // Symbols have no hot spot corner: draw them smaller, beside the pointed position.
                position += size * scale / 4f;
                scale *= 0.8f;
            }

            sprites.Add(Sprite(sprite, position, size * scale, rotation));
            return true;
        }

        static float BusyRotation()
        {
            var session = MyAPIGateway.Session;
            return session == null ? 0f : (float)(session.GameplayFrameCounter / 60d % (Math.PI * 2d));
        }

        static MySprite Sprite(string data, Vector2 position, Vector2 size, float rotation)
        {
            return new MySprite
            {
                Type = SpriteType.TEXTURE,
                Data = data,
                Position = position,
                Size = size,
                Alignment = TextAlignment.CENTER,
                RotationOrScale = rotation
            };
        }
    }
}
