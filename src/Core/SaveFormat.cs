using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AzimuthSaveImporter.Core
{
    /// <summary>
    /// The on-disk layout Azimuth uses for a save container:
    ///
    ///   &lt;player folder&gt;/&lt;container&gt;/revisions/&lt;16-digit revision&gt;/commit
    ///   &lt;player folder&gt;/&lt;container&gt;/revisions/&lt;16-digit revision&gt;/data/&lt;blob files&gt;
    ///
    /// and the "commit" manifest:
    ///
    ///   Azimuth complete save revision v2\n
    ///   blobs &lt;count&gt;\n
    ///   &lt;sha256 hex&gt; &lt;size&gt; &lt;name&gt;\n     (one line per blob, names in ordinal order)
    ///
    /// This was inferred from saves Azimuth wrote, not from Azimuth's code. That is why the
    /// importer checks its own output against every commit Azimuth already wrote before it
    /// touches anything (see <see cref="VerifyExistingCommits"/>).
    /// </summary>
    public static class SaveFormat
    {
        public const string CommitHeader = "Azimuth complete save revision v2";
        public const string RevisionsFolder = "revisions";
        public const string DataFolder = "data";
        public const string CommitFile = "commit";
        public const string FirstRevision = "0000000000000001";

        /// <summary>Builds the commit manifest for the given blobs.</summary>
        public static byte[] BuildCommit(IEnumerable<Blob> blobs)
        {
            var sorted = blobs.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
            var text = new StringBuilder();
            text.Append(CommitHeader).Append('\n');
            text.Append("blobs ").Append(sorted.Count).Append('\n');
            foreach (var blob in sorted)
                text.Append(blob.Sha256).Append(' ').Append(blob.Size).Append(' ').Append(blob.Name).Append('\n');
            return new UTF8Encoding(false).GetBytes(text.ToString());
        }

        /// <summary>Builds the commit manifest for the files currently in a revision's data folder.</summary>
        public static byte[] BuildCommitForDataFolder(string dataFolder)
        {
            var blobs = Directory.GetFiles(dataFolder).Select(Blob.FromFile);
            return BuildCommit(blobs);
        }

        public static string Sha256Hex(Stream stream)
        {
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(stream));
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(bytes));
        }

        static string ToHex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Regenerates the commit of every revision under <paramref name="playerFolder"/> and
        /// compares it byte for byte with the commit Azimuth wrote.
        /// </summary>
        public static CommitCheck VerifyExistingCommits(string playerFolder)
        {
            var result = new CommitCheck();
            if (!Directory.Exists(playerFolder)) return result;
            foreach (var container in Directory.GetDirectories(playerFolder))
            {
                var revisions = Path.Combine(container, RevisionsFolder);
                if (!Directory.Exists(revisions)) continue;
                foreach (var revision in Directory.GetDirectories(revisions))
                {
                    var commitPath = Path.Combine(revision, CommitFile);
                    var dataPath = Path.Combine(revision, DataFolder);
                    if (!File.Exists(commitPath) || !Directory.Exists(dataPath)) continue;
                    var label = Path.GetFileName(container) + "/" + Path.GetFileName(revision);
                    var actual = File.ReadAllBytes(commitPath);
                    if (!StartsWithHeader(actual))
                    {
                        result.UnknownFormat.Add(label);
                        continue;
                    }
                    var expected = BuildCommitForDataFolder(dataPath);
                    if (expected.SequenceEqual(actual)) result.Matched.Add(label);
                    else result.Mismatched.Add(label);
                }
            }
            return result;
        }

        static bool StartsWithHeader(byte[] commit)
        {
            var header = Encoding.ASCII.GetBytes(CommitHeader + "\n");
            return commit.Length >= header.Length && commit.Take(header.Length).SequenceEqual(header);
        }
    }

    public sealed class Blob
    {
        public string Name { get; private set; }
        public long Size { get; private set; }
        public string Sha256 { get; private set; }

        public Blob(string name, long size, string sha256)
        {
            Name = name; Size = size; Sha256 = sha256;
        }

        public static Blob FromFile(string path)
        {
            using (var stream = File.OpenRead(path))
                return new Blob(Path.GetFileName(path), stream.Length, SaveFormat.Sha256Hex(stream));
        }
    }

    public sealed class CommitCheck
    {
        public readonly List<string> Matched = new List<string>();
        public readonly List<string> Mismatched = new List<string>();
        public readonly List<string> UnknownFormat = new List<string>();

        /// <summary>True when at least one Azimuth commit was reproduced and none failed.</summary>
        public bool Verified { get { return Matched.Count > 0 && Mismatched.Count == 0 && UnknownFormat.Count == 0; } }
        public bool Failed { get { return Mismatched.Count > 0 || UnknownFormat.Count > 0; } }
        public bool NothingToCompare { get { return Matched.Count == 0 && !Failed; } }

        public string Describe()
        {
            if (Failed)
                return string.Format(
                    "The save format check FAILED: {0} existing commit file(s) in this save folder could not be reproduced ({1}). " +
                    "Azimuth may have changed its save format, so nothing will be imported.",
                    Mismatched.Count + UnknownFormat.Count,
                    string.Join(", ", Mismatched.Concat(UnknownFormat).Take(5)));
            if (NothingToCompare)
                return "No existing Azimuth saves to check the format against. Start the game once and save, " +
                       "so the importer can confirm it writes saves exactly like Azimuth does.";
            return string.Format("Save format verified: reproduced {0} existing commit file{1} in this save folder byte for byte.",
                Matched.Count, Matched.Count == 1 ? "" : "s");
        }
    }
}
