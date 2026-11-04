using System;
using System.IO;
using Acrux.Dialogs;
using Sandbox.ModAPI;

namespace Acrux.Dialogs
{
    /// <summary>
    /// Storage contexts for toolkit file dialogs. This file is compiled into the host mod, so the game sees the
    /// host mod (through <paramref name="modType"/>) reading and writing its own local or world storage.
    /// </summary>
    public static class HostStorage
    {
        /// <summary>The host mod's local storage (per player, shared by all worlds).</summary>
        public static ModStorage Local(Type modType, string name = "Mod storage")
        {
            if (modType == null)
                throw new ArgumentNullException("modType");
            return new ModStorage(
                name,
                path => MyAPIGateway.Utilities.FileExistsInLocalStorage(path, modType),
                path =>
                {
                    using (TextReader reader = MyAPIGateway.Utilities.ReadFileInLocalStorage(path, modType))
                        return reader.ReadToEnd();
                },
                (path, text) =>
                {
                    using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(path, modType))
                        writer.Write(text);
                },
                path => MyAPIGateway.Utilities.DeleteFileInLocalStorage(path, modType));
        }

        /// <summary>The host mod's storage in the current world.</summary>
        public static ModStorage World(Type modType, string name = "World storage")
        {
            if (modType == null)
                throw new ArgumentNullException("modType");
            return new ModStorage(
                name,
                path => MyAPIGateway.Utilities.FileExistsInWorldStorage(path, modType),
                path =>
                {
                    using (TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(path, modType))
                        return reader.ReadToEnd();
                },
                (path, text) =>
                {
                    using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(path, modType))
                        writer.Write(text);
                },
                path => MyAPIGateway.Utilities.DeleteFileInWorldStorage(path, modType));
        }

        /// <summary>A picker context with the host mod's local and world storage and its localizer.</summary>
        public static FilePickerContext PickerContext(Type modType, Func<string, string> localizer = null)
        {
            var context = new FilePickerContext { Localizer = localizer };
            context.AddStorage(Local(modType));
            context.AddStorage(World(modType));
            return context;
        }
    }
}
