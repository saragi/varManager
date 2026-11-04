using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ICSharpCode.SharpZipLib.Zip;

namespace varManager
{
    public enum VarScanOutcome
    {
        Scanned,
        // The name doesn't have three parts, so the var is left where it is.
        BadName,
        // It isn't a readable zip, or it has no meta.json.
        Invalid
    }

    /// <summary>A scene, preset or other item inside a var.</summary>
    public sealed class VarItem
    {
        public string Type { get; set; }
        public bool IsPreset { get; set; }
        public string EntryName { get; set; }
        public string Preview { get; set; }
    }

    public sealed class VarScan
    {
        public VarScanOutcome Outcome { get; set; }
        public string CreatorName { get; set; }
        public string PackageName { get; set; }
        public int Version { get; set; }
        public long FileSize { get; set; }
        public DateTime VarDate { get; set; }
        public DateTime MetaDate { get; set; }
        public int Scenes { get; set; }
        public int Looks { get; set; }
        public int Clothing { get; set; }
        public int Hairstyle { get; set; }
        public int Plugins { get; set; }
        public int Assets { get; set; }
        public int Morphs { get; set; }
        public int Pose { get; set; }
        public int Skin { get; set; }
        public List<VarItem> Items { get; } = new List<VarItem>();
        public List<string> Dependencies { get; set; } = new List<string>();
    }

    /// <summary>Reads one var without changing it, and extracts its preview images.</summary>
    public static class VarScanner
    {
        private const long MaxScannedEntryBytes = 96L * 1024 * 1024;
        private const string PluginScripts = "pluginScripts";
        private const string PluginLists = "pluginLists";

        private sealed class Rule
        {
            public readonly Regex Pattern;
            public readonly string Counter;
            public readonly bool IsItem;
            private readonly Func<string, bool> preset;

            public Rule(string pattern, string counter, bool isItem, Func<string, bool> preset)
            {
                Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
                Counter = counter;
                IsItem = isItem;
                this.preset = preset;
            }

            public bool Preset(string entryName, bool current)
            {
                return preset == null ? current : preset(entryName);
            }
        }

        private static readonly Func<string, bool> Never = name => false;
        private static readonly Func<string, bool> Always = name => true;
        private static readonly Func<string, bool> IfJson = name => name.EndsWith(".json", StringComparison.Ordinal);
        private static readonly Func<string, bool> IfVap = name => name.EndsWith(".vap", StringComparison.Ordinal);

        // Every rule is tried, in this order, and a later match decides the item's type.
        private static readonly Rule[] Rules =
        {
            new Rule(@"saves/scene/.*?\x2e(?:json)", "scenes", true, null),
            new Rule(@"saves/person/appearance/.*?\x2e(?:json|vac)", "looks", true, IfJson),
            new Rule(@"custom/atom/person/(?:general|appearance)/.*?\x2e(?:json|vap)", "looks", true, Always),
            new Rule(@"custom/clothing/.*?\x2e(?:vam|vap)", "clothing", true, Never),
            new Rule(@"custom/atom/person/clothing/.*?\x2e(?:vam|vap)", "clothing", true, IfVap),
            new Rule(@"custom/hair/.*?\x2e(?:vam|vap)", "hairstyle", true, Never),
            new Rule(@"custom/atom/person/hair/.*?\x2e(?:vam|vap)", "hairstyle", true, IfVap),
            new Rule(@"custom/scripts/.*?\x2e(?:cs)", PluginScripts, false, null),
            new Rule(@"custom/atom/person/scripts/.*?\x2e(?:cs)", PluginScripts, false, null),
            new Rule(@"custom/scripts/.*?\x2e(?:cslist)", PluginLists, false, null),
            new Rule(@"custom/atom/person/scripts/.*?\x2e(?:cslist)", PluginLists, false, null),
            new Rule(@"custom/assets/.*?\x2e(?:assetbundle)", "assets", true, null),
            new Rule(@"custom/atom/person/morphs/.*?\x2e(?:vmi|vap)", "morphs", true, IfVap),
            new Rule(@"custom/atom/person/pose/.*?\x2e(?:vap)", "pose", true, Always),
            new Rule(@"saves/person/pose/.*?\x2e(?:json|vac)", "pose", true, IfJson),
            new Rule(@"custom/atom/person/skin/.*?\x2e(?:vap)", "skin", true, Always),
        };

        /// <param name="warning">Told about a file inside the var that couldn't be read.</param>
        /// <param name="problem">Told about an entry or dependency scan that failed. The scan carries on.</param>
        public static VarScan Read(string varPath, string previewRoot, Action<string> warning, Action<string> problem)
        {
            ZipFile zip;
            try
            {
                zip = new ZipFile(varPath);
            }
            catch (Exception)
            {
                return new VarScan { Outcome = VarScanOutcome.Invalid };
            }

            using (zip)
            {
                string varName = Path.GetFileNameWithoutExtension(varPath);
                string[] parts = varName.Split('.');
                if (parts.Length != 3)
                    return new VarScan { Outcome = VarScanOutcome.BadName };

                ZipEntry meta = zip.GetEntry("meta.json");
                if (meta == null)
                    return new VarScan { Outcome = VarScanOutcome.Invalid };

                FileInfo file = new FileInfo(varPath);
                int version;
                if (!int.TryParse(parts[2], out version))
                    version = 1;
                var scan = new VarScan
                {
                    Outcome = VarScanOutcome.Scanned,
                    CreatorName = parts[0],
                    PackageName = parts[1],
                    Version = version,
                    FileSize = file.Length,
                    VarDate = file.LastWriteTime,
                    MetaDate = meta.DateTime,
                };

                var counts = new Dictionary<string, int>();
                foreach (ZipEntry entry in zip)
                {
                    try
                    {
                        ReadEntry(zip, entry, varName, previewRoot, counts, scan);
                    }
                    catch (ArgumentException ex)
                    {
                        problem(entry.Name + " " + ex.Message);
                    }
                }

                scan.Scenes = Count(counts, "scenes");
                scan.Looks = Count(counts, "looks");
                scan.Clothing = Count(counts, "clothing");
                scan.Hairstyle = Count(counts, "hairstyle");
                scan.Morphs = Count(counts, "morphs");
                scan.Pose = Count(counts, "pose");
                scan.Skin = Count(counts, "skin");
                scan.Assets = Count(counts, "assets");
                int lists = Count(counts, PluginLists);
                scan.Plugins = lists > 0 ? lists : Count(counts, PluginScripts);

                try
                {
                    scan.Dependencies = Dependencies(zip, varName, warning);
                }
                catch (Exception ex)
                {
                    problem(varPath + " get dependencies failed " + ex.Message);
                }
                return scan;
            }
        }

        /// <summary>Which packages each file inside a var references.</summary>
        public static List<KeyValuePair<string, HashSet<string>>> FileReferences(string varPath, Action<string> warning)
        {
            using (ZipFile zip = new ZipFile(varPath))
                return FileReferences(zip, warning);
        }

        private static void ReadEntry(ZipFile zip, ZipEntry entry, string varName, string previewRoot,
            Dictionary<string, int> counts, VarScan scan)
        {
            string type = null;
            bool isItem = false;
            bool isPreset = false;
            foreach (Rule rule in Rules)
            {
                if (!rule.Pattern.IsMatch(entry.Name))
                    continue;
                counts[rule.Counter] = Count(counts, rule.Counter) + 1;
                if (rule.IsItem)
                {
                    type = rule.Counter;
                    isItem = true;
                    isPreset = rule.Preset(entry.Name, isPreset);
                }
            }
            if (!isItem)
                return;

            string preview = ExtractPreview(zip, entry, type, Count(counts, type), varName, previewRoot);
            if (type != "assets")
                scan.Items.Add(new VarItem { Type = type, IsPreset = isPreset, EntryName = entry.Name, Preview = preview });
        }

        private static string ExtractPreview(ZipFile zip, ZipEntry entry, string type, int number, string varName, string previewRoot)
        {
            string jpgEntry = entry.Name.Substring(0, entry.Name.LastIndexOf('.')) + ".jpg";
            ZipEntry jpg = zip.GetEntry(jpgEntry);
            if (jpg == null)
                return "";

            string stem = Path.GetFileNameWithoutExtension(jpg.Name);
            // .NET 10 would otherwise fail later with an IOException, which would cost the whole var.
            if (stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Illegal characters in path.");

            string folder = Path.Combine(previewRoot, type, varName);
            Directory.CreateDirectory(folder);
            string name = type + number.ToString("000") + "_" + stem.ToLower() + ".jpg";
            ScenePreviews.Extract(zip, jpg, Path.Combine(folder, name));
            return name;
        }

        // Reads meta.json and every scene or preset file, because meta.json can be incomplete.
        private static List<string> Dependencies(ZipFile zip, string varName, Action<string> warning)
        {
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, HashSet<string>> file in FileReferences(zip, warning))
                references.UnionWith(file.Value);
            references.Remove(varName);
            return references.ToList();
        }

        private static List<KeyValuePair<string, HashSet<string>>> FileReferences(ZipFile zip, Action<string> warning)
        {
            var files = new List<KeyValuePair<string, HashSet<string>>>();
            foreach (ZipEntry entry in zip)
            {
                if (!entry.IsFile || !DependencyScanner.IsScannable(entry.Name) || entry.Size > MaxScannedEntryBytes)
                    continue;
                try
                {
                    using (StreamReader reader = new StreamReader(zip.GetInputStream(entry)))
                        files.Add(new KeyValuePair<string, HashSet<string>>(entry.Name, DependencyScanner.Extract(entry.Name, reader.ReadToEnd())));
                }
                catch (Exception ex)
                {
                    // One damaged file must not cost the var its other dependencies.
                    warning($"{zip.Name}: skipped {entry.Name}, {ex.Message}");
                }
            }
            return files;
        }

        private static int Count(Dictionary<string, int> counts, string key)
        {
            int count;
            return counts.TryGetValue(key, out count) ? count : 0;
        }
    }
}
