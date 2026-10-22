using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AzimuthSaveImporter.Gui
{
    /// <summary>Remembers the last folders used, in %APPDATA%\Azimuth Save Importer\settings.ini.</summary>
    sealed class Settings
    {
        public string Source = "";
        public string Target = "";
        public string Player = "";
        public string BackupFolder = Core.AzimuthTarget.DefaultBackupFolder;

        static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Azimuth Save Importer", "settings.ini");
            }
        }

        public static Settings Load()
        {
            var settings = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return settings;
                var values = File.ReadAllLines(FilePath)
                    .Select(l => l.Split(new[] { '=' }, 2)).Where(p => p.Length == 2)
                    .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim());
                string value;
                if (values.TryGetValue("source", out value)) settings.Source = value;
                if (values.TryGetValue("target", out value)) settings.Target = value;
                if (values.TryGetValue("player", out value)) settings.Player = value;
                if (values.TryGetValue("backups", out value) && value.Length > 0) settings.BackupFolder = value;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new List<string>
                {
                    "source=" + Source, "target=" + Target, "player=" + Player, "backups=" + BackupFolder
                });
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
    }
}
