using System.Collections.Generic;

namespace Acrux.Dialogs
{
    /// <summary>English texts of the toolkit dialogs; host mods translate the keys through a localizer.</summary>
    internal static class DialogTexts
    {
        static readonly Dictionary<string, string> Texts = new Dictionary<string, string>
        {
            { "FilePicker_BackToRoots", "Back to roots" },
            { "FilePicker_Loading", "Loading..." },
            { "FilePicker_NoFilesFound", "No files found" },
            { "FilePicker_NoFilesMatchFormat", "No files match \"{0}\"" },
            { "FilePicker_SelectFolder", "Select Folder" },
            { "FilePicker_SelectFile", "Select File" },
            { "FilePicker_PickFolder", "Pick folder" },
            { "FilePicker_PickFile", "Pick file" },
            { "FilePicker_SearchPlaceholder", "Search files" },
            { "SpritePicker_MultiSelectHint", "Ctrl+click adds a sprite, Shift+click selects a range" },
            { "SpritePicker_SelectedFormat", "{0} selected" },
            { "Common_Button_Apply", "Apply" },
        };

        public static string English(string key)
        {
            string text;
            return key != null && Texts.TryGetValue(key, out text) ? text : key;
        }
    }
}
