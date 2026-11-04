using System;
using System.Collections.Generic;

[assembly: Harness.AcruxUiToolkitExport]

namespace Harness
{
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class AcruxUiToolkitExportAttribute : Attribute { }

    /// <summary>
    /// A toolkit element of a type clients do not mirror (the Harness namespace is not mirrored), deriving from the
    /// abstract VisualElement, with a styled property of its own: clients wrap it as VisualElement and reach Level as
    /// a custom property.
    /// </summary>
    public sealed class UnmirroredElement : Acrux.Core.VisualElement
    {
        public static readonly Acrux.Core.PropertySystem.StyledProperty<float> LevelProperty =
            Acrux.Core.PropertySystem.StyledProperty.Register<UnmirroredElement, float>(nameof(Level), 3f);

        public float Level
        {
            get { return GetValue(LevelProperty); }
            set { SetValue(LevelProperty, value); }
        }

        // One per codec path: a game type (passes unchanged), a mirrored enum (crosses as int), a toolkit-only enum with
        // an explicit underlying type (crosses as that integer), and one without (ACRUX009: listed, not transferable).
        public static readonly Acrux.Core.PropertySystem.StyledProperty<VRageMath.Color> TintProperty =
            Acrux.Core.PropertySystem.StyledProperty.Register<UnmirroredElement, VRageMath.Color>(nameof(Tint), VRageMath.Color.Red);
        public static readonly Acrux.Core.PropertySystem.StyledProperty<Acrux.Core.HorizontalAlignment> AlignProperty =
            Acrux.Core.PropertySystem.StyledProperty.Register<UnmirroredElement, Acrux.Core.HorizontalAlignment>(nameof(Align), Acrux.Core.HorizontalAlignment.Right);
        public static readonly Acrux.Core.PropertySystem.StyledProperty<MarkerKind> MarkerProperty =
            Acrux.Core.PropertySystem.StyledProperty.Register<UnmirroredElement, MarkerKind>(nameof(Marker), MarkerKind.Star);

        public VRageMath.Color Tint
        {
            get { return GetValue(TintProperty); }
            set { SetValue(TintProperty, value); }
        }

        public Acrux.Core.HorizontalAlignment Align
        {
            get { return GetValue(AlignProperty); }
            set { SetValue(AlignProperty, value); }
        }

        public MarkerKind Marker
        {
            get { return GetValue(MarkerProperty); }
            set { SetValue(MarkerProperty, value); }
        }

        public static readonly Acrux.Core.PropertySystem.StyledProperty<LegacyKind> LegacyProperty =
            Acrux.Core.PropertySystem.StyledProperty.Register<UnmirroredElement, LegacyKind>(nameof(Legacy), LegacyKind.B);

        public LegacyKind Legacy
        {
            get { return GetValue(LegacyProperty); }
            set { SetValue(LegacyProperty, value); }
        }
    }

    /// <summary>A toolkit-only enum with an explicit underlying type: clients see it as a byte.</summary>
    public enum MarkerKind : byte
    {
        Dot,
        Star
    }

    /// <summary>A toolkit-only enum without one: the generator warns (ACRUX009) and the property is not transferable.</summary>
    public enum LegacyKind
    {
        A,
        B
    }

    /// <summary>What the toolkit mod publishes on its request channel.</summary>
    public static class ToolkitApi
    {
        public static Dictionary<string, Delegate> Create()
        {
            var api = new Dictionary<string, Delegate>(StringComparer.Ordinal);
            Acrux.Toolkit.Server.ServerUi.Register(api);
            Acrux.Toolkit.Server.AcruxUiToolkitBridge.RegisterGenerated(api);
            api["Toolkit.ProtocolVersion"] = new Func<int>(() => 1);
            return api;
        }

        public static void InstallSourceLoader(Func<string, string> toolkitContent)
        {
            Acrux.Toolkit.Server.ServerUi.InstallSourceLoader(toolkitContent);
        }
    }
}
