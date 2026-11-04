using System;
using System.Collections.Generic;

namespace Acrux.Dialogs
{
    /// <summary>
    /// One storage of the host mod (its local storage, or its storage in the current world). Space Engineers
    /// scopes storage by the calling mod, so the host mod hands over its own file delegates; the toolkit never
    /// reads or writes another mod's files with its own identity.
    /// </summary>
    /// <remarks>
    /// The game cannot list a storage, so files written through <see cref="WriteText"/> are recorded in an index
    /// file there; that index is what the file picker browses. Files written directly by the mod can be added with
    /// <see cref="Register"/>.
    /// </remarks>
    public sealed class ModStorage
    {
        /// <summary>Index of the files known to the toolkit, one relative path per line.</summary>
        public const string IndexFileName = "acrux-storage-index.txt";

        readonly Func<string, bool> _exists;
        readonly Func<string, string> _readText;
        readonly Action<string, string> _writeText;
        readonly Action<string> _delete;
        List<string> _index;

        public ModStorage(
            string name,
            Func<string, bool> exists,
            Func<string, string> readText,
            Action<string, string> writeText,
            Action<string> delete)
        {
            Name = name ?? "Storage";
            _exists = exists;
            _readText = readText;
            _writeText = writeText;
            _delete = delete;
        }

        /// <summary>Root name shown by the file picker, e.g. "Mod storage" or "World storage".</summary>
        public string Name { get; set; }

        public bool Exists(string path)
        {
            return IsValidPath(path) && _exists != null && _exists(path);
        }

        /// <summary>The file's text, or null when it does not exist.</summary>
        public string ReadText(string path)
        {
            if (!Exists(path) || _readText == null)
                return null;
            return _readText(path);
        }

        public void WriteText(string path, string text)
        {
            if (!IsValidPath(path))
                throw new ArgumentException("Storage paths are relative, use '/' and cannot contain '..'.", "path");
            if (_writeText == null)
                throw new InvalidOperationException("Storage '" + Name + "' is read-only.");
            _writeText(path, text ?? string.Empty);
            Register(path);
        }

        public bool Delete(string path)
        {
            if (!Exists(path) || _delete == null)
                return false;
            _delete(path);
            List<string> index = LoadIndex();
            if (index.Remove(path))
                SaveIndex(index);
            return true;
        }

        /// <summary>Records a file the mod wrote itself, so the file picker lists it.</summary>
        public void Register(string path)
        {
            if (!IsValidPath(path) || string.Equals(path, IndexFileName, StringComparison.OrdinalIgnoreCase))
                return;
            List<string> index = LoadIndex();
            if (index.Contains(path))
                return;
            index.Add(path);
            index.Sort(StringComparer.OrdinalIgnoreCase);
            SaveIndex(index);
        }

        /// <summary>Indexed files that still exist.</summary>
        public List<string> ListFiles()
        {
            var files = new List<string>();
            foreach (string path in LoadIndex())
                if (Exists(path))
                    files.Add(path);
            return files;
        }

        /// <summary>The storage as a picker root: indexed files under folders split on '/'.</summary>
        public FolderModel ToFolder()
        {
            var root = new FolderModel { Name = Name, FullPath = string.Empty, Tag = this };
            var folders = new Dictionary<string, FolderModel>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in ListFiles())
            {
                FolderModel parent = root;
                string[] parts = path.Split('/');
                string prefix = string.Empty;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    prefix = prefix.Length == 0 ? parts[i] : prefix + "/" + parts[i];
                    FolderModel folder;
                    if (!folders.TryGetValue(prefix, out folder))
                    {
                        folder = new FolderModel { Name = parts[i], FullPath = prefix, Parent = parent, Tag = this };
                        parent.Folders.Add(folder);
                        folders[prefix] = folder;
                    }
                    parent = folder;
                }
                parent.Files.Add(new FileModel { Name = parts[parts.Length - 1], FullPath = path, Tag = this });
            }
            return root;
        }

        List<string> LoadIndex()
        {
            if (_index != null)
                return _index;
            _index = new List<string>();
            if (_exists == null || _readText == null || !_exists(IndexFileName))
                return _index;
            string text = _readText(IndexFileName);
            if (text == null)
                return _index;
            foreach (string line in text.Split('\n'))
            {
                string path = line.Trim();
                if (IsValidPath(path) && !_index.Contains(path))
                    _index.Add(path);
            }
            return _index;
        }

        void SaveIndex(List<string> index)
        {
            if (_writeText != null)
                _writeText(IndexFileName, string.Join("\n", index));
        }

        static bool IsValidPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && path.IndexOf("..", StringComparison.Ordinal) < 0 &&
                   path.IndexOf('\\') < 0 && path.IndexOf(':') < 0 && !path.StartsWith("/", StringComparison.Ordinal);
        }
    }

    /// <summary>What a file picker may use from the host mod: its language and its storages.</summary>
    public sealed class FilePickerContext
    {
        readonly List<ModStorage> _storages = new List<ModStorage>();

        /// <summary>See <see cref="DialogViewModel.Localizer"/>.</summary>
        public Func<string, string> Localizer { get; set; }

        public IList<ModStorage> Storages => _storages;

        public void AddStorage(ModStorage storage)
        {
            if (storage != null && !_storages.Contains(storage))
                _storages.Add(storage);
        }

        /// <summary>One picker root per storage.</summary>
        public List<FolderModel> StorageRoots()
        {
            var roots = new List<FolderModel>();
            foreach (ModStorage storage in _storages)
                roots.Add(storage.ToFolder());
            return roots;
        }
    }
}
