using System;
using System.Collections.Generic;
using Acrux.Core;
using VRageMath;

namespace Acrux.Controls.Documents
{
    public enum FontWeight
    {
        Normal,
        Bold
    }

    public enum FontStyle
    {
        Normal,
        Italic
    }

    public enum TextDecorationLocation
    {
        Underline,
        Strikethrough
    }

    public sealed class TextDecoration
    {
        public TextDecorationLocation Location { get; set; }
    }

    public sealed class TextDecorationCollection : List<TextDecoration>
    {
        internal bool Has(TextDecorationLocation location)
        {
            for (int i = 0; i < Count; i++)
                if (this[i].Location == location)
                    return true;
            return false;
        }
    }

    /// <summary>TextDecorations: the stock decoration collections.</summary>
    public static class TextDecorations
    {
        public static TextDecorationCollection Underline =>
            new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline } };

        public static TextDecorationCollection Strikethrough =>
            new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Strikethrough } };
    }

    /// <summary>
    /// Maps a family plus weight/style to a concrete font name. Space Engineers has no synthetic bold or italic, so the
    /// default resolver uses the "-Bold", "-Italic" and "-Bold-Italic" variants for families that define them.
    /// </summary>
    public static class FontResolver
    {
        static readonly HashSet<string> StyledFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "White" };

        public static void RegisterStyledFamily(string family)
        {
            if (!string.IsNullOrWhiteSpace(family))
                StyledFamilies.Add(family);
        }

        public static string Resolve(string family, FontWeight weight, FontStyle style)
        {
            if (string.IsNullOrWhiteSpace(family) || !StyledFamilies.Contains(family))
                return family;
            if (weight == FontWeight.Bold && style == FontStyle.Italic)
                return family + "-Bold-Italic";
            if (weight == FontWeight.Bold)
                return family + "-Bold";
            return style == FontStyle.Italic ? family + "-Italic" : family;
        }
    }

    /// <summary>Inline: a piece of text content with optional formatting overrides.</summary>
    public abstract class Inline
    {
        public string FontFamily { get; set; }
        public float? FontSize { get; set; }
        public Color? Foreground { get; set; }
        public FontWeight? FontWeight { get; set; }
        public FontStyle? FontStyle { get; set; }
        public TextDecorationCollection TextDecorations { get; set; }
    }

    public sealed class InlineCollection : List<Inline>
    {
        public void Add(string text)
        {
            Add(new Run(text));
        }
    }

    public class Run : Inline
    {
        public Run()
        {
        }

        public Run(string text)
        {
            Text = text;
        }

        public string Text { get; set; }
    }

    public class Span : Inline
    {
        public InlineCollection Inlines { get; } = new InlineCollection();
    }

    public sealed class Bold : Span
    {
        public Bold()
        {
            FontWeight = Documents.FontWeight.Bold;
        }
    }

    public sealed class Italic : Span
    {
        public Italic()
        {
            FontStyle = Documents.FontStyle.Italic;
        }
    }

    public sealed class Underline : Span
    {
        public Underline()
        {
            TextDecorations = Documents.TextDecorations.Underline;
        }
    }

    public sealed class LineBreak : Inline
    {
    }

    /// <summary>Hosts a visual (an image, for instance) inside the text flow.</summary>
    public sealed class InlineUIContainer : Inline
    {
        public InlineUIContainer()
        {
        }

        public InlineUIContainer(VisualElement child)
        {
            Child = child;
        }

        public VisualElement Child { get; set; }
    }
}
