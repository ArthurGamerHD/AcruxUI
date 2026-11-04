namespace Acrux.Diagnostics
{
    /// <summary>
    /// What a mod shows on its crash screen. The mod fills it with already-localized text; anything left
    /// empty falls back to English.
    /// </summary>
    public sealed class CrashReport
    {
        public CrashReport(string modName)
        {
            ModName = modName;
        }

        /// <summary>Name used in log lines and notifications.</summary>
        public string ModName { get; set; }

        /// <summary>Headline under the sad face.</summary>
        public string Header { get; set; }

        /// <summary>Text next to the QR code, normally pointing at <see cref="SupportUrl"/>.</summary>
        public string Info { get; set; }

        /// <summary>Where users report the problem; appended to the default info text.</summary>
        public string SupportUrl { get; set; }

        /// <summary>Line above the exception details.</summary>
        public string SupportIntro { get; set; }

        /// <summary>Label of the exception details.</summary>
        public string ExceptionLabel { get; set; }

        /// <summary>
        /// The QR code as rectangles in modules: center x, center y, width, height, light (1) or dark (0), relative
        /// to the code's center. Null hides the code.
        /// </summary>
        public int[] QrRectangles { get; set; }

        /// <summary>Side of the QR code, in modules (including the quiet zone).</summary>
        public int QrModules { get; set; }

        internal string HeaderOrDefault =>
            string.IsNullOrEmpty(Header)
                ? "Your screen ran into a problem and needs to restart. We're just collecting some error info, and then we'll restart for you."
                : Header;

        internal string InfoOrDefault =>
            string.IsNullOrEmpty(Info)
                ? "For more information about this issue, " +
                  (string.IsNullOrEmpty(SupportUrl) ? string.Empty : "visit " + SupportUrl + " ") +
                  (QrRectangles == null ? string.Empty : "or read this QR code.")
                : Info;

        internal string SupportIntroOrDefault =>
            string.IsNullOrEmpty(SupportIntro) ? "If you call a support person, give them this info:" : SupportIntro;

        internal string ExceptionLabelOrDefault =>
            string.IsNullOrEmpty(ExceptionLabel) ? "Exception code:" : ExceptionLabel;
    }
}
