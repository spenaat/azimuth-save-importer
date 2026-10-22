using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AzimuthSaveImporter.Core
{
    /// <summary>Finds Azimuth's per-player save folders ("...\saves\player-1").</summary>
    public static class AzimuthTarget
    {
        public const string DefaultProfileName = "User_9000000000000";
        const int MaxSearchDepth = 7;
        const int MaxFoldersVisited = 20000;

        /// <summary>
        /// Accepts the Azimuth folder, its data folder, a "saves" folder, a "player-N" folder or any
        /// folder inside one, and returns the player folders it finds (existing or, for an empty
        /// "saves" folder, "player-1" to be created).
        /// </summary>
        public static List<string> FindPlayerFolders(string path)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return found;
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Inside a player folder (for example a container or its revisions): walk up.
            for (var dir = new DirectoryInfo(full); dir != null; dir = dir.Parent)
            {
                if (IsPlayerFolder(dir))
                {
                    found.Add(dir.FullName);
                    return found;
                }
            }

            if (IsSavesFolder(full))
            {
                found.AddRange(PlayersIn(full));
                if (found.Count == 0) found.Add(Path.Combine(full, "player-1"));
                return found;
            }

            // Search downwards (breadth first, bounded) for "saves\player-N".
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(full, 0));
            var visited = 0;
            var emptySaves = new List<string>();
            while (queue.Count > 0 && visited < MaxFoldersVisited)
            {
                var item = queue.Dequeue();
                visited++;
                string[] children;
                try { children = Directory.GetDirectories(item.Key); }
                catch (Exception e) when (e is UnauthorizedAccessException || e is IOException) { continue; }
                foreach (var child in children)
                {
                    if (IsSavesFolder(child))
                    {
                        var players = PlayersIn(child);
                        if (players.Count == 0) emptySaves.Add(Path.Combine(child, "player-1"));
                        found.AddRange(players);
                        continue;
                    }
                    if (item.Value + 1 < MaxSearchDepth && !IsReparsePoint(child))
                        queue.Enqueue(new KeyValuePair<string, int>(child, item.Value + 1));
                }
            }
            if (found.Count == 0) found.AddRange(emptySaves);
            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static bool IsPlayerFolder(DirectoryInfo dir)
        {
            return dir.Name.StartsWith("player-", StringComparison.OrdinalIgnoreCase) && dir.Parent != null &&
                   dir.Parent.Name.Equals("saves", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsSavesFolder(string path)
        {
            return Path.GetFileName(path).Equals("saves", StringComparison.OrdinalIgnoreCase) &&
                   Directory.GetParent(path) != null &&
                   (PlayersIn(path).Count > 0 || Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? "")
                        .Equals("titles", StringComparison.OrdinalIgnoreCase));
        }

        static List<string> PlayersIn(string saves)
        {
            try
            {
                return Directory.GetDirectories(saves)
                    .Where(d => Path.GetFileName(d).StartsWith("player-", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException) { return new List<string>(); }
        }

        static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        /// <summary>Containers (folders) currently in a player folder.</summary>
        public static List<string> ExistingContainers(string playerFolder)
        {
            if (!Directory.Exists(playerFolder)) return new List<string>();
            return Directory.GetDirectories(playerFolder).Select(Path.GetFileName)
                .Where(n => !n.StartsWith(".")).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Running processes that look like the game, so the user can close them first.</summary>
        public static List<string> RunningGameProcesses()
        {
            var names = new List<string>();
            int self;
            using (var current = Process.GetCurrentProcess()) self = current.Id;
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.Id == self) continue;
                        var name = process.ProcessName;
                        if (name.IndexOf("SaveImporter", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("azimuth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("forza", StringComparison.OrdinalIgnoreCase) >= 0)
                            names.Add(name);
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            catch { }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string DefaultBackupFolder
        {
            get
            {
                var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrEmpty(documents)) documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(documents, "Azimuth Save Importer", "Backups");
            }
        }
    }
}
