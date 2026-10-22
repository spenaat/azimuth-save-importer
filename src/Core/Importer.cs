using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace AzimuthSaveImporter.Core
{
    public enum ItemStatus { New, ReplacesExisting, AlreadyImported }

    /// <summary>What happens to the saves already in the player folder.</summary>
    public enum ExistingSaves
    {
        /// <summary>Back up, delete everything in the player folder, then import.</summary>
        ReplaceAll,
        /// <summary>Back up, replace only containers with the same name, keep the rest.</summary>
        Merge
    }

    public sealed class PlanItem
    {
        public SourceContainer Source { get; internal set; }
        public string TargetName { get; internal set; }
        public ItemStatus Status { get; internal set; }
    }

    public sealed class ImportPlan
    {
        public XboxSource Source { get; internal set; }
        public string PlayerFolder { get; internal set; }
        public List<PlanItem> Items { get; internal set; }
        public List<string> ExistingContainers { get; internal set; }
        public CommitCheck FormatCheck { get; internal set; }
        public List<string> Warnings { get; internal set; }

        public bool HasExistingSaves { get { return ExistingContainers.Count > 0; } }
        public int Conflicts { get { return Items.Count(i => i.Status != ItemStatus.New); } }
        public bool AllAlreadyImported { get { return Items.Count > 0 && Items.All(i => i.Status == ItemStatus.AlreadyImported); } }
        public long TotalSize { get { return Items.Sum(i => i.Source.TotalSize); } }

        /// <summary>Existing containers that "Replace all" would delete (after the backup).</summary>
        public List<string> RemovedByReplaceAll
        {
            get { return ExistingContainers.ToList(); }
        }

        public string Summary()
        {
            var parts = Items.GroupBy(i => i.Source.Kind)
                .OrderBy(g => g.Key == "Profile" ? 0 : 1).ThenBy(g => g.Key)
                .Select(g => g.Count() + " " + Plural(g.Key, g.Count()));
            return string.Format("{0} container{1} ({2}), {3}", Items.Count, Items.Count == 1 ? "" : "s",
                string.Join(", ", parts), Importer.FormatSize(TotalSize));
        }

        static string Plural(string kind, int count)
        {
            var lower = kind.ToLowerInvariant();
            if (count == 1) return lower;
            return lower.EndsWith("y") ? lower.Substring(0, lower.Length - 1) + "ies" : lower + "s";
        }
    }

    public sealed class ImportResult
    {
        public int Imported;
        public long Bytes;
        public string BackupPath;
        public List<string> Removed = new List<string>();
    }

    public sealed class ImportOptions
    {
        public ExistingSaves ExistingSaves = ExistingSaves.ReplaceAll;
        public string BackupFolder = AzimuthTarget.DefaultBackupFolder;
        /// <summary>Allow importing when there is no Azimuth commit to verify the format against.</summary>
        public bool AllowUnverifiedFormat;
        public Action<string> Log = delegate { };
        public Action<int> Progress = delegate { };
    }

    public static class Importer
    {
        const string StagingPrefix = ".azimuth-importer-";

        public static ImportPlan Plan(XboxSource source, string playerFolder)
        {
            if (string.IsNullOrWhiteSpace(playerFolder)) throw new ImportException("Choose Azimuth's save folder.");
            playerFolder = Path.GetFullPath(playerFolder);
            var saves = Path.GetDirectoryName(playerFolder);
            if (saves == null || !Directory.Exists(saves))
                throw new ImportException("Azimuth's save folder was not found: " + playerFolder);

            var existing = AzimuthTarget.ExistingContainers(playerFolder);
            var plan = new ImportPlan
            {
                Source = source,
                PlayerFolder = playerFolder,
                ExistingContainers = existing,
                FormatCheck = SaveFormat.VerifyExistingCommits(playerFolder),
                Items = new List<PlanItem>(),
                Warnings = new List<string>(source.Warnings)
            };

            // The Xbox profile is named after the Xbox account (User_<XUID>); Azimuth uses its own name.
            var existingProfiles = existing.Where(n => n.StartsWith("User_", StringComparison.OrdinalIgnoreCase)).ToList();
            var profileName = AzimuthTarget.DefaultProfileName;
            if (existingProfiles.Count == 1) profileName = existingProfiles[0];
            else if (existingProfiles.Count > 1)
            {
                profileName = existingProfiles
                    .OrderByDescending(n => Directory.GetLastWriteTimeUtc(Path.Combine(playerFolder, n))).First();
                plan.Warnings.Add("Azimuth has more than one profile here (" + string.Join(", ", existingProfiles) +
                                  "). The Xbox profile will be imported as the most recently used one, " + profileName + ".");
            }

            foreach (var container in source.Containers)
            {
                var target = container.IsProfile ? profileName : container.BaseName;
                var status = ItemStatus.New;
                if (existing.Contains(target, StringComparer.OrdinalIgnoreCase))
                    status = IsIdentical(container, Path.Combine(playerFolder, target)) ? ItemStatus.AlreadyImported : ItemStatus.ReplacesExisting;
                plan.Items.Add(new PlanItem { Source = container, TargetName = target, Status = status });
            }

            if (!source.Containers.Any(c => c.IsProfile))
                plan.Warnings.Add("The Xbox save has no profile (User_...) container, so your progress will not be imported, " +
                                  "only the other containers.");

            // Photos are numbered; two photos with the same number may clash in the game.
            var importedPhotos = plan.Items.Select(i => PhotoNumber(i.TargetName)).Where(n => n != null).ToList();
            foreach (var name in existing)
            {
                var number = PhotoNumber(name);
                if (number != null && importedPhotos.Contains(number) && !plan.Items.Any(i => i.TargetName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    plan.Warnings.Add("Azimuth already has photo " + name + " with the same number as an Xbox photo. " +
                                      "Choose \"replace everything\" when importing (or delete that photo), or one of them may not show up.");
            }
            return plan;
        }

        /// <summary>True when the container holds exactly one revision with the same files as the source.</summary>
        static bool IsIdentical(SourceContainer source, string containerFolder)
        {
            try
            {
                var revisions = Path.Combine(containerFolder, SaveFormat.RevisionsFolder);
                if (!Directory.Exists(revisions)) return false;
                var all = Directory.GetDirectories(revisions);
                if (all.Length != 1) return false;
                var data = Path.Combine(all[0], SaveFormat.DataFolder);
                if (!Directory.Exists(data)) return false;
                var files = Directory.GetFiles(data);
                if (files.Length != source.Files.Count) return false;
                foreach (var file in source.Files)
                {
                    var path = Path.Combine(data, file.Name);
                    if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
                }
                foreach (var file in source.Files)
                {
                    string a, b;
                    using (var stream = file.Open()) a = SaveFormat.Sha256Hex(stream);
                    using (var stream = File.OpenRead(Path.Combine(data, file.Name))) b = SaveFormat.Sha256Hex(stream);
                    if (a != b) return false;
                }
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        static string PhotoNumber(string name)
        {
            if (!name.StartsWith("Photo_", StringComparison.OrdinalIgnoreCase)) return null;
            var parts = name.Split('_');
            return parts.Length >= 2 ? parts[1] : null;
        }

        public static ImportResult Execute(ImportPlan plan, ImportOptions options)
        {
            var log = options.Log;
            var result = new ImportResult();
            var check = SaveFormat.VerifyExistingCommits(plan.PlayerFolder);
            log(check.Describe());
            if (check.Failed) throw new ImportException(check.Describe());
            if (check.NothingToCompare && !options.AllowUnverifiedFormat)
                throw new ImportException(check.Describe());

            var saves = Path.GetDirectoryName(plan.PlayerFolder);
            var staging = Path.Combine(saves, StagingPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var expectedCommits = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var applied = false;
            try
            {
                // 1. Write every container into a staging folder next to the player folder.
                Directory.CreateDirectory(staging);
                var total = Math.Max(1L, plan.TotalSize);
                long written = 0;
                foreach (var item in plan.Items)
                {
                    var revision = Path.Combine(staging, item.TargetName, SaveFormat.RevisionsFolder, SaveFormat.FirstRevision);
                    var data = Path.Combine(revision, SaveFormat.DataFolder);
                    Directory.CreateDirectory(data);
                    var blobs = new List<Blob>();
                    foreach (var file in item.Source.Files)
                    {
                        blobs.Add(CopyAndHash(file, Path.Combine(data, file.Name)));
                        written += file.Size;
                        options.Progress((int)(written * 70 / total));
                    }
                    var commit = SaveFormat.BuildCommit(blobs);
                    File.WriteAllBytes(Path.Combine(revision, SaveFormat.CommitFile), commit);
                    if (!SaveFormat.BuildCommitForDataFolder(data).SequenceEqual(commit))
                        throw new ImportException("Verification failed while writing " + item.TargetName + ". Nothing was changed.");
                    expectedCommits[item.TargetName] = commit;
                    log(string.Format("Prepared {0} -> {1} ({2} file{3})", item.Source.OriginalName, item.TargetName,
                        blobs.Count, blobs.Count == 1 ? "" : "s"));
                }

                // 2. Back up the player folder before anything in it changes.
                var hasContent = Directory.Exists(plan.PlayerFolder) &&
                                 Directory.EnumerateFileSystemEntries(plan.PlayerFolder).Any();
                if (hasContent)
                {
                    result.BackupPath = Backup(plan.PlayerFolder, options.BackupFolder);
                    log("Backed up the current saves to " + result.BackupPath);
                }
                options.Progress(80);

                // 3. Apply.
                Directory.CreateDirectory(plan.PlayerFolder);
                applied = true;
                var importedNames = new HashSet<string>(plan.Items.Select(i => i.TargetName), StringComparer.OrdinalIgnoreCase);
                if (options.ExistingSaves == ExistingSaves.ReplaceAll)
                {
                    foreach (var entry in Directory.GetDirectories(plan.PlayerFolder))
                    {
                        if (Path.GetFileName(entry).StartsWith(StagingPrefix)) continue;
                        Directory.Delete(entry, true);
                        result.Removed.Add(Path.GetFileName(entry));
                    }
                    foreach (var file in Directory.GetFiles(plan.PlayerFolder))
                    {
                        File.Delete(file);
                        result.Removed.Add(Path.GetFileName(file));
                    }
                }
                else
                {
                    foreach (var entry in Directory.GetDirectories(plan.PlayerFolder))
                    {
                        if (!importedNames.Contains(Path.GetFileName(entry))) continue;
                        Directory.Delete(entry, true);
                        result.Removed.Add(Path.GetFileName(entry));
                    }
                }
                foreach (var item in plan.Items)
                    Directory.Move(Path.Combine(staging, item.TargetName), Path.Combine(plan.PlayerFolder, item.TargetName));
                options.Progress(90);

                // 4. Verify what is now on disk.
                foreach (var item in plan.Items)
                {
                    var revision = Path.Combine(plan.PlayerFolder, item.TargetName, SaveFormat.RevisionsFolder, SaveFormat.FirstRevision);
                    var onDisk = File.ReadAllBytes(Path.Combine(revision, SaveFormat.CommitFile));
                    if (!onDisk.SequenceEqual(expectedCommits[item.TargetName]) ||
                        !SaveFormat.BuildCommitForDataFolder(Path.Combine(revision, SaveFormat.DataFolder)).SequenceEqual(onDisk))
                        throw new ImportException("Verification failed for " + item.TargetName + " after import.");
                    result.Imported++;
                    result.Bytes += item.Source.TotalSize;
                }
                options.Progress(100);
                log(string.Format("Imported and verified {0} container{1} ({2}).", result.Imported,
                    result.Imported == 1 ? "" : "s", FormatSize(result.Bytes)));
                return result;
            }
            catch (Exception error)
            {
                if (applied && result.BackupPath != null)
                {
                    log("Import failed: " + error.Message + " Restoring the backup...");
                    try
                    {
                        RestoreInto(result.BackupPath, plan.PlayerFolder);
                    }
                    catch (Exception restoreError)
                    {
                        throw new ImportException("The import failed AND restoring the backup failed (" + restoreError.Message +
                            "). Your previous saves are in " + result.BackupPath + ". Reason: " + error.Message, error);
                    }
                    throw new ImportException("The import failed and your previous saves were restored from the backup. " +
                                              "Reason: " + error.Message, error);
                }
                if (applied && result.BackupPath == null)
                    throw new ImportException("The import failed: " + error.Message, error);
                if (error is ImportException) throw;
                throw new ImportException("The import failed before anything was changed: " + error.Message, error);
            }
            finally
            {
                TryDeleteFolder(staging);
            }
        }

        static Blob CopyAndHash(SourceFile file, string destination)
        {
            using (var input = file.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                long size = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    output.Write(buffer, 0, read);
                    size += read;
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                if (size != file.Size)
                    throw new ImportException(string.Format("{0} is {1} bytes but {2} were read. The download may be incomplete.",
                        file.Name, file.Size, size));
                var hex = new System.Text.StringBuilder(64);
                foreach (var b in sha.Hash) hex.Append(b.ToString("x2"));
                return new Blob(file.Name, size, hex.ToString());
            }
        }

        // ---------------------------------------------------------------- backups

        /// <summary>Zips the whole player folder and checks the ZIP before returning its path.</summary>
        public static string Backup(string playerFolder, string backupFolder)
        {
            Directory.CreateDirectory(backupFolder);
            var title = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(playerFolder)) ?? "") ?? "";
            var name = string.Format("{0}{1}-{2}", title.Length > 0 ? title + "-" : "", Path.GetFileName(playerFolder),
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var path = Path.Combine(backupFolder, name + ".zip");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(backupFolder, name + " (" + n + ").zip");

            ZipFile.CreateFromDirectory(playerFolder, path, CompressionLevel.Optimal, false);

            // Check that every file made it into the ZIP with the right size.
            var files = Directory.GetFiles(playerFolder, "*", SearchOption.AllDirectories);
            using (var zip = ZipFile.OpenRead(path))
            {
                var entries = zip.Entries.Where(e => !e.FullName.EndsWith("/") && !e.FullName.EndsWith("\\"))
                    .ToDictionary(e => e.FullName.Replace('\\', '/'), e => e.Length, StringComparer.Ordinal);
                foreach (var file in files)
                {
                    var relative = file.Substring(playerFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');
                    long length;
                    if (!entries.TryGetValue(relative, out length) || length != new FileInfo(file).Length)
                        throw new ImportException("The backup could not be verified (" + relative + "). Nothing was changed.");
                }
            }
            return path;
        }

        /// <summary>
        /// Restores a backup made by this tool into a player folder. The current content is backed
        /// up first, then replaced.
        /// </summary>
        public static string Restore(string backupZip, string playerFolder, string backupFolder, Action<string> log)
        {
            if (!File.Exists(backupZip)) throw new ImportException("Backup not found: " + backupZip);
            using (var zip = ZipFile.OpenRead(backupZip))
            {
                if (!zip.Entries.Any(e => e.FullName.Replace('\\', '/').Split('/').Contains(SaveFormat.RevisionsFolder)))
                    throw new ImportException("This ZIP does not look like an Azimuth save backup.");
            }
            string safety = null;
            if (Directory.Exists(playerFolder) && Directory.EnumerateFileSystemEntries(playerFolder).Any())
            {
                safety = Backup(playerFolder, backupFolder);
                log("Backed up the current saves to " + safety);
            }
            RestoreInto(backupZip, playerFolder);
            log("Restored " + Path.GetFileName(backupZip) + " into " + playerFolder);
            return safety;
        }

        static void RestoreInto(string backupZip, string playerFolder)
        {
            var saves = Path.GetDirectoryName(playerFolder);
            var staging = Path.Combine(saves, StagingPrefix + "restore-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(staging);
                var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                using (var zip = ZipFile.OpenRead(backupZip))
                {
                    foreach (var entry in zip.Entries)
                    {
                        var relative = entry.FullName.Replace('\\', '/');
                        var target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new ImportException("The backup contains an unsafe path: " + entry.FullName);
                        if (relative.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, false);
                    }
                }
                if (Directory.Exists(playerFolder))
                {
                    foreach (var entry in Directory.GetDirectories(playerFolder)) Directory.Delete(entry, true);
                    foreach (var file in Directory.GetFiles(playerFolder)) File.Delete(file);
                }
                else Directory.CreateDirectory(playerFolder);
                foreach (var entry in Directory.GetDirectories(staging))
                    Directory.Move(entry, Path.Combine(playerFolder, Path.GetFileName(entry)));
                foreach (var file in Directory.GetFiles(staging))
                    File.Move(file, Path.Combine(playerFolder, Path.GetFileName(file)));
            }
            finally
            {
                TryDeleteFolder(staging);
            }
        }

        static void TryDeleteFolder(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
        }
    }
}
