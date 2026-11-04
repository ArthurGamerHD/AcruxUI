using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace AcruxPreview
{
    /// <summary>
    /// The installed game's Bin64. A host built from an Acrux checkout records it at build time; the host shipped in the
    /// Acrux.Sdk package does not carry the game's files and finds the user's install at run time instead:
    /// ACRUX_SE_BIN, then the recorded path, then the Steam libraries.
    /// </summary>
    static class GameInstall
    {
        const string ProbeFile = "VRage.Math.dll";

        static readonly Lazy<string> BinValue = new Lazy<string>(FindBin);

        public static string Bin => BinValue.Value;

        // Runs before Main is compiled: Main and the types it touches already need the game's assemblies.
        [ModuleInitializer]
        internal static void Install()
        {
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                string bin = Bin;
                if (bin == null)
                    return null;
                string path = Path.Combine(bin, name.Name + ".dll");
                return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
            };
        }

        static string FindBin()
        {
            foreach (string candidate in Candidates())
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(Path.Combine(candidate, ProbeFile)))
                    return Path.GetFullPath(candidate);
            }
            Console.Error.WriteLine("Acrux preview: Space Engineers was not found; set ACRUX_SE_BIN to its Bin64 folder.");
            return null;
        }

        static IEnumerable<string> Candidates()
        {
            yield return Environment.GetEnvironmentVariable("ACRUX_SE_BIN");
            yield return typeof(GameInstall).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(a => a.Key == "AcruxPreview.GameBin").Select(a => a.Value).FirstOrDefault();
            foreach (string library in SteamLibraries())
                yield return Path.Combine(library, "steamapps", "common", "SpaceEngineers", "Bin64");
        }

        static IEnumerable<string> SteamLibraries()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new List<string>
            {
                Path.Combine(home, ".local", "share", "Steam"),
                Path.Combine(home, ".steam", "steam"),
                Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    continue;
                if (seen.Add(root))
                    yield return root;
                // Other library folders are listed as "path" entries in steamapps/libraryfolders.vdf.
                string folders = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(folders))
                    continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"path\"\\s*\"([^\"]+)\""))
                {
                    string library = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (seen.Add(library))
                        yield return library;
                }
            }
        }
    }
}
