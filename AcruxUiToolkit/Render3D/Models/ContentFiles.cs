using System;
using System.Collections.Generic;
using System.IO;
using Adk.Utils;
using Sandbox.ModAPI;
using VRage.Game;

namespace Acrux.Render3D.Models
{
    /// <summary>Content files (models and their textures) by content path, from mods or the game.</summary>
    internal static class ContentFiles
    {
        // Every active mod first, then the game's content; null when none has it.
        public static BinaryReader Open(string content)
        {
            var utilities = MyAPIGateway.Utilities;
            if (string.IsNullOrWhiteSpace(content))
                return null;

            var candidates = BuildContentPathCandidates(content);
            if (MyAPIGateway.Session.Mods != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    foreach (var mod in MyAPIGateway.Session.Mods)
                    {
                        var reader = TryOpenMod(candidates[i], mod);
                        if (reader != null)
                            return reader;
                    }
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                try
                {
                    var reader = utilities.ReadBinaryFileInGameContent(candidate);
                    if (reader != null)
                        return reader;
                }
                catch
                {
                }

                try
                {
                    if (utilities.FileExistsInGameContent(candidate))
                        return utilities.ReadBinaryFileInGameContent(candidate);
                }
                catch (Exception e)
                {
                    LogHelper.LogOnce("fail:file:game:exists:" + candidate,
                        "Game content file read failed: " + candidate + "\n" + e);
                }
            }

            LogHelper.LogOnce("fail:file:" + content, "Unable to locate content file: " + content);
            return null;
        }

        static BinaryReader TryOpenMod(string content, MyObjectBuilder_Checkpoint.ModItem mod)
        {
            var utilities = MyAPIGateway.Utilities;
            try
            {
                return utilities.ReadBinaryFileInModLocation(content, mod);
            }
            catch (FileNotFoundException)
            {
                // Not in this mod: every active mod is tried in turn.
            }
            catch (Exception e)
            {
                LogHelper.LogOnce("fail:file:mod:exists:" + content + ":" + mod.PublishedFileId,
                    $"Content file read failed for mod ({mod.Name} {mod.PublishedServiceName}): " +
                    content + $"\n{e}");
            }
            return null;
        }

        static List<string> BuildContentPathCandidates(string content)
        {
            var candidates = new List<string>();
            var forward = content.Replace('\\', '/');
            AddContentPathCandidate(candidates, content);
            AddContentPathCandidate(candidates, forward);
            AddContentPathCandidate(candidates, content.Replace('/', '\\'));

            while (forward.StartsWith("/", StringComparison.Ordinal))
                forward = forward.Substring(1);

            if (forward.StartsWith("data/", StringComparison.OrdinalIgnoreCase) && forward.Length > 5)
            {
                AddContentPathCandidate(candidates, "Data/" + forward.Substring(5));
                AddContentPathCandidate(candidates, "data/" + forward.Substring(5));
            }

            return candidates;
        }

        static void AddContentPathCandidate(List<string> candidates, string content)
        {
            if (candidates == null || string.IsNullOrWhiteSpace(content))
                return;

            var normalized = content.Trim();
            while (normalized.StartsWith("/", StringComparison.Ordinal) ||
                   normalized.StartsWith("\\", StringComparison.Ordinal))
                normalized = normalized.Substring(1);

            for (int i = 0; i < candidates.Count; i++)
                if (string.Equals(candidates[i], normalized, StringComparison.Ordinal))
                    return;

            candidates.Add(normalized);
        }

        public static string ToModelPath(string assetName)
        {
            var path = assetName.Replace('\\', '/');

            const string contentMarker = "/Content/";

            var contentIndex = path.IndexOf(contentMarker, StringComparison.OrdinalIgnoreCase);
            if (contentIndex >= 0)
                path = path.Substring(contentIndex + contentMarker.Length);

            while (path.StartsWith("/", StringComparison.Ordinal))
                path = path.Substring(1);

            // A workshop mod's path: drop its "244850/<mod id>/" prefix.
            if (path.StartsWith("244850/", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring("244850/".Length);
                path = path.Substring(path.IndexOf("/", StringComparison.Ordinal) + 1);
            }

            return path.EndsWith(".mwm", StringComparison.OrdinalIgnoreCase) ? path : null;
        }
    }
}
