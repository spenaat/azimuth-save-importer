using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AzimuthSaveImporter.Core
{
    /// <summary>One file inside an Xbox save container.</summary>
    public sealed class SourceFile
    {
        public string Name { get; private set; }
        public long Size { get; private set; }
        readonly Func<Stream> _open;

        public SourceFile(string name, long size, Func<Stream> open)
        {
            Name = name; Size = size; _open = open;
        }

        public Stream Open() { return _open(); }
    }

    /// <summary>One Xbox save container (for example "Livery_0289_20150618180709,savedgame").</summary>
    public sealed class SourceContainer
    {
        public string OriginalName { get; private set; }
        public List<SourceFile> Files { get; private set; }
        public List<string> SkippedFiles { get; private set; }

        public SourceContainer(string originalName)
        {
            OriginalName = originalName;
            Files = new List<SourceFile>();
            SkippedFiles = new List<string>();
        }

        public long TotalSize { get { return Files.Sum(f => f.Size); } }

        /// <summary>The container name without the Xbox ",savedgame" suffix.</summary>
        public string BaseName { get { return XboxSource.StripSuffix(OriginalName); } }

        public bool IsProfile { get { return BaseName.StartsWith("User_", StringComparison.OrdinalIgnoreCase); } }

        public string Kind { get { return XboxSource.KindOf(BaseName); } }
    }

    /// <summary>
    /// Reads Xbox saves downloaded with XBsync (https://github.com/d3fekt/xbsync), either as the
    /// extracted folder ("Download all") or as the ZIP ("Download ZIP"). Every container is a
    /// folder of flat files; the folder name is the container name.
    /// </summary>
    public sealed class XboxSource : IDisposable
    {
        public const string SavedGameSuffix = ",savedgame";

        /// <summary>Files the Xbox keeps per container that Azimuth never writes.</summary>
        public static readonly string[] XboxOnlyFiles = { "TransactionLogFile" };

        public string Path { get; private set; }
        public bool IsZip { get; private set; }
        public List<SourceContainer> Containers { get; private set; }
        public List<string> Warnings { get; private set; }

        ZipArchive _zip;

        XboxSource(string path)
        {
            Path = path;
            Containers = new List<SourceContainer>();
            Warnings = new List<string>();
        }

        public static XboxSource Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ImportException("Choose the folder or ZIP you downloaded with XBsync.");
            var source = new XboxSource(System.IO.Path.GetFullPath(path));
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                source.ReadZip();
            else if (Directory.Exists(path))
                source.ReadFolder();
            else
                throw new ImportException("The Xbox save was not found: " + path);

            if (source.Containers.Count == 0)
                throw new ImportException("No Xbox save containers were found in " + path +
                    ". Choose the folder (or ZIP) XBsync created for Forza Horizon 2.");
            source.Containers.Sort((a, b) => string.Compare(a.BaseName, b.BaseName, StringComparison.OrdinalIgnoreCase));

            var duplicates = source.Containers.GroupBy(c => c.BaseName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
            if (duplicates.Count > 0)
                throw new ImportException("The Xbox save contains the same container more than once: " +
                    string.Join(", ", duplicates.Select(d => d.Key)));
            if (source.Containers.Count(c => c.IsProfile) > 1)
                throw new ImportException("The Xbox save contains more than one profile (User_...) container. " +
                    "Download one Xbox profile at a time.");
            return source;
        }

        void ReadFolder()
        {
            var subfolders = Directory.GetDirectories(Path);
            if (subfolders.Any(d => Directory.Exists(System.IO.Path.Combine(d, SaveFormat.RevisionsFolder))))
                throw new ImportException("This folder looks like an Azimuth save (it contains 'revisions' folders), not an Xbox download. " +
                    "Choose the folder XBsync created.");

            if (subfolders.Length == 0 && Directory.GetFiles(Path).Length > 0)
            {
                // The user picked a single container folder.
                AddFolderContainer(Path);
                return;
            }
            foreach (var folder in subfolders)
            {
                if (System.IO.Path.GetFileName(folder).StartsWith(".")) continue;
                if (Directory.GetFiles(folder).Length == 0)
                {
                    if (Directory.GetDirectories(folder).Length > 0)
                        Warnings.Add("Skipped folder '" + System.IO.Path.GetFileName(folder) + "': it has subfolders but no save files.");
                    continue;
                }
                AddFolderContainer(folder);
            }
        }

        void AddFolderContainer(string folder)
        {
            var container = new SourceContainer(System.IO.Path.GetFileName(folder));
            foreach (var file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = System.IO.Path.GetFileName(file);
                var size = new FileInfo(file).Length;
                var captured = file;
                AddFile(container, name, size, () => File.OpenRead(captured));
            }
            foreach (var nested in Directory.GetDirectories(folder))
                Warnings.Add("Ignored subfolder '" + System.IO.Path.GetFileName(folder) + "/" + System.IO.Path.GetFileName(nested) +
                             "': Xbox save containers only hold files.");
            Finish(container);
        }

        void ReadZip()
        {
            IsZip = true;
            _zip = ZipFile.OpenRead(Path);
            var byFolder = new Dictionary<string, SourceContainer>(StringComparer.Ordinal);
            var fileName = System.IO.Path.GetFileNameWithoutExtension(Path);
            foreach (var entry in _zip.Entries)
            {
                var full = entry.FullName.Replace('\\', '/');
                if (full.EndsWith("/")) continue; // directory entry
                var slash = full.LastIndexOf('/');
                var folder = slash < 0 ? "" : full.Substring(0, slash);
                var name = full.Substring(slash + 1);
                if (folder.Split('/').Contains(SaveFormat.RevisionsFolder))
                    throw new ImportException("This ZIP looks like an Azimuth save (it contains 'revisions' folders), not an Xbox download.");
                SourceContainer container;
                if (!byFolder.TryGetValue(folder, out container))
                {
                    var containerName = folder.Length == 0 ? fileName : folder.Substring(folder.LastIndexOf('/') + 1);
                    container = new SourceContainer(containerName);
                    byFolder.Add(folder, container);
                }
                var captured = entry;
                AddFile(container, name, entry.Length, () => captured.Open());
            }
            foreach (var container in byFolder.Values) Finish(container);
        }

        void AddFile(SourceContainer container, string name, long size, Func<Stream> open)
        {
            if (name.StartsWith(".xbsync-") || name.EndsWith(".partial"))
            {
                container.SkippedFiles.Add(name + " (unfinished download)");
                return;
            }
            if (XboxOnlyFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                container.SkippedFiles.Add(name + " (Xbox only)");
                return;
            }
            if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..")
                throw new ImportException("Unsupported file name in " + container.OriginalName + ": " + name);
            container.Files.Add(new SourceFile(name, size, open));
        }

        void Finish(SourceContainer container)
        {
            if (container.Files.Count == 0)
            {
                Warnings.Add("Skipped '" + container.OriginalName + "': it has no save files.");
                return;
            }
            if (container.BaseName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                throw new ImportException("Unsupported container name: " + container.OriginalName);
            Containers.Add(container);
        }

        public static string StripSuffix(string name)
        {
            return name.EndsWith(SavedGameSuffix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - SavedGameSuffix.Length)
                : name;
        }

        public static string KindOf(string baseName)
        {
            var underscore = baseName.IndexOf('_');
            var prefix = underscore < 0 ? baseName : baseName.Substring(0, underscore);
            switch (prefix.ToLowerInvariant())
            {
                case "user": return "Profile";
                case "livery": return "Livery";
                case "soulboundlivery": return "Bound livery";
                case "garagethumbnail": return "Garage thumbnail";
                case "tuning": return "Tuning setup";
                case "photo": return "Photo";
                default: return prefix;
            }
        }

        public void Dispose()
        {
            if (_zip != null) { _zip.Dispose(); _zip = null; }
        }
    }

    public sealed class ImportException : Exception
    {
        public ImportException(string message) : base(message) { }
        public ImportException(string message, Exception inner) : base(message, inner) { }
    }
}
