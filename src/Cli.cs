using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AzimuthSaveImporter.Core;

namespace AzimuthSaveImporter
{
    /// <summary>Command-line mode, used when the exe is started with arguments.</summary>
    static class Cli
    {
        const string Usage =
@"Azimuth Save Importer {0}

Imports Forza Horizon 2 saves downloaded from the Xbox cloud with XBsync into Azimuth.
Run without arguments to open the window.

Usage:
  AzimuthSaveImporter import  --source <folder|zip> --target <folder> [options]
  AzimuthSaveImporter verify  --target <folder>
  AzimuthSaveImporter restore --backup <zip> --target <folder> [--yes]

  --source <path>       Folder or ZIP downloaded with XBsync.
  --target <path>       Azimuth's folder, its saves folder or a saves\player-N folder.
  --player <name>       Player folder to use when there are several (for example player-1).
  --mode replace|merge  Required when Azimuth already has saves:
                          replace  back up, then replace all existing saves
                          merge    back up, then replace only containers with the same name
  --backup-dir <path>   Where backups go (default: {1}).
  --allow-unverified    Import even when there is no Azimuth save to verify the format against.
  --dry-run             Show what would happen and change nothing.
  --yes                 Do not ask for confirmation.
";

        public static int Run(string[] args)
        {
            try
            {
                if (args.Length == 0 || args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
                {
                    Console.WriteLine(Usage, Program.Version, AzimuthTarget.DefaultBackupFolder);
                    return 0;
                }
                if (args[0] == "--version") { Console.WriteLine(Program.Version); return 0; }
                var options = Parse(args.Skip(1).ToArray());
                switch (args[0])
                {
                    case "import": return Import(options);
                    case "verify": return Verify(options);
                    case "restore": return Restore(options);
                    default:
                        Console.Error.WriteLine("Unknown command '" + args[0] + "'. Use --help.");
                        return 2;
                }
            }
            catch (ImportException e)
            {
                Console.Error.WriteLine("Error: " + e.Message);
                return 1;
            }
            catch (UsageException e)
            {
                Console.Error.WriteLine(e.Message + " Use --help.");
                return 2;
            }
        }

        static int Import(Dictionary<string, string> o)
        {
            var player = ResolvePlayer(o);
            using (var source = XboxSource.Open(Required(o, "source")))
            {
                var plan = Importer.Plan(source, player);
                Console.WriteLine("Source:  " + source.Path);
                Console.WriteLine("Target:  " + plan.PlayerFolder);
                Console.WriteLine("Import:  " + plan.Summary());
                foreach (var item in plan.Items)
                    Console.WriteLine(string.Format("  {0,-48} -> {1}{2}", item.Source.OriginalName, item.TargetName,
                        item.Status == ItemStatus.ReplacesExisting ? "  (replaces existing)" :
                        item.Status == ItemStatus.AlreadyImported ? "  (already imported, identical)" : ""));
                foreach (var skipped in plan.Items.SelectMany(i => i.Source.SkippedFiles.Select(f => i.Source.OriginalName + "/" + f)))
                    Console.WriteLine("  skipped " + skipped);
                Console.WriteLine(plan.FormatCheck.Describe());
                foreach (var warning in plan.Warnings) Console.WriteLine("Warning: " + warning);

                var opts = new ImportOptions
                {
                    AllowUnverifiedFormat = o.ContainsKey("allow-unverified"),
                    Log = Console.WriteLine
                };
                if (o.ContainsKey("backup-dir")) opts.BackupFolder = o["backup-dir"];

                if (plan.HasExistingSaves)
                {
                    string mode;
                    if (!o.TryGetValue("mode", out mode) && o.ContainsKey("dry-run"))
                        mode = "replace";
                    else if (mode == null)
                        throw new UsageException(string.Format(
                            "Azimuth already has {0} saved container{1} in {2}. Choose --mode replace or --mode merge (a backup is made either way).",
                            plan.ExistingContainers.Count, plan.ExistingContainers.Count == 1 ? "" : "s", plan.PlayerFolder));
                    if (mode == "replace") opts.ExistingSaves = ExistingSaves.ReplaceAll;
                    else if (mode == "merge") opts.ExistingSaves = ExistingSaves.Merge;
                    else throw new UsageException("--mode must be replace or merge.");
                    Console.WriteLine(opts.ExistingSaves == ExistingSaves.ReplaceAll
                        ? "Existing saves: back up, then replace all " + plan.ExistingContainers.Count
                        : "Existing saves: back up, then replace " + plan.Conflicts + " with the same name");
                }
                if (plan.FormatCheck.Failed) throw new ImportException(plan.FormatCheck.Describe());
                if (plan.FormatCheck.NothingToCompare && !opts.AllowUnverifiedFormat)
                    throw new ImportException(plan.FormatCheck.Describe() + " (Or pass --allow-unverified.)");
                if (o.ContainsKey("dry-run")) { Console.WriteLine("Dry run: nothing was changed."); return 0; }

                var running = AzimuthTarget.RunningGameProcesses();
                if (running.Count > 0)
                    Console.WriteLine("Warning: close the game first. Running: " + string.Join(", ", running));
                if (!o.ContainsKey("yes") && !Confirm("Import now?")) { Console.WriteLine("Cancelled."); return 1; }

                var result = Importer.Execute(plan, opts);
                if (result.BackupPath != null) Console.WriteLine("Backup: " + result.BackupPath);
                return 0;
            }
        }

        static int Verify(Dictionary<string, string> o)
        {
            var player = ResolvePlayer(o);
            var check = SaveFormat.VerifyExistingCommits(player);
            Console.WriteLine(player);
            Console.WriteLine(check.Describe());
            return check.Failed ? 1 : 0;
        }

        static int Restore(Dictionary<string, string> o)
        {
            var player = ResolvePlayer(o);
            var backup = Required(o, "backup");
            Console.WriteLine("Restore " + backup + " into " + player + " (the current saves are backed up first).");
            if (!o.ContainsKey("yes") && !Confirm("Restore now?")) { Console.WriteLine("Cancelled."); return 1; }
            var dir = o.ContainsKey("backup-dir") ? o["backup-dir"] : AzimuthTarget.DefaultBackupFolder;
            Importer.Restore(backup, player, dir, Console.WriteLine);
            return 0;
        }

        static string ResolvePlayer(Dictionary<string, string> o)
        {
            var target = Required(o, "target");
            if (!Directory.Exists(target)) throw new ImportException("Folder not found: " + target);
            var players = AzimuthTarget.FindPlayerFolders(target);
            string wanted;
            if (o.TryGetValue("player", out wanted))
                players = players.Where(p => Path.GetFileName(p).Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (players.Count == 0)
                throw new ImportException("No Azimuth save folder (saves\\player-N) was found in " + target + ".");
            if (players.Count > 1)
                throw new UsageException("Several save folders were found; pick one with --player:\n  " + string.Join("\n  ", players));
            return players[0];
        }

        static bool Confirm(string question)
        {
            Console.Write(question + " [y/N] ");
            var answer = Console.ReadLine();
            return answer != null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        }

        static string Required(Dictionary<string, string> o, string key)
        {
            string value;
            if (!o.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value)) throw new UsageException("Missing --" + key + ".");
            return value;
        }

        static readonly string[] Flags = { "yes", "dry-run", "allow-unverified" };
        static readonly string[] Values = { "source", "target", "player", "mode", "backup-dir", "backup" };

        static Dictionary<string, string> Parse(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) throw new UsageException("Unexpected argument '" + args[i] + "'.");
                var key = args[i].Substring(2);
                if (Flags.Contains(key)) { result[key] = "true"; continue; }
                if (!Values.Contains(key)) throw new UsageException("Unknown option '" + args[i] + "'.");
                if (i + 1 >= args.Length) throw new UsageException("Missing value for " + args[i] + ".");
                result[key] = args[++i];
            }
            return result;
        }

        sealed class UsageException : Exception
        {
            public UsageException(string message) : base(message) { }
        }
    }
}
