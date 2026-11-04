namespace Acrux.Styling
{
    /// <summary>
    /// XAML declaration for a stylesheet loaded from an application asset.
    /// The view generator resolves it through <see cref="StyleSheet.SourceLoader"/>.
    /// </summary>
    public sealed class StyleInclude
    {
        public string Source { get; set; }
    }
}
