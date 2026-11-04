using System;
using System.Collections.Generic;

namespace Acrux.Dialogs
{
    public enum FilePickerMode
    {
        PickFile,
        PickFolder,
        PickFiles
    }

    public sealed class FolderModel
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public string Subtitle { get; set; }
        public object Tag { get; set; }
        public IList<FolderModel> Folders { get; } = new List<FolderModel>();
        public IList<FileModel> Files { get; } = new List<FileModel>();
        public FolderModel Parent { get; internal set; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(FullPath) ? (Name ?? string.Empty) : FullPath;
        }
    }

    public sealed class FileModel
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public string IconPath { get; set; }
        public string Icon { get; set; }
        public string Subtitle { get; set; }

        /// <summary>Why the file cannot be used, e.g. an unsupported codec. Such files are listed but cannot be picked.</summary>
        public string Warning { get; set; }

        public object Tag { get; set; }

        public bool IsBlocked => !string.IsNullOrEmpty(Warning);

        public override string ToString()
        {
            return string.IsNullOrEmpty(FullPath) ? (Name ?? string.Empty) : FullPath;
        }
    }

    public sealed class FilePickerResult
    {
        public FilePickerMode Mode { get; set; }
        public string RootName { get; set; }
        public string FullPath { get; set; }
        public FolderModel Folder { get; set; }
        public FileModel File { get; set; }
        public object Tag { get; set; }
    }

    public sealed class FilePickerContextAction
    {
        public FilePickerContextAction(string text, Action clicked)
        {
            Text = text;
            Clicked = clicked;
        }

        public string Text { get; set; }
        public Action Clicked { get; set; }
        public bool Enabled { get; set; } = true;
        public bool UseErrorTextStyle { get; set; }
    }
}
