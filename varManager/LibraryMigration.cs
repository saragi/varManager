using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace varManager
{
    public enum MigrationActionKind
    {
        /// <summary>An enabled var; it is linked back after Update DB.</summary>
        Var,
        /// <summary>Disabled content, stored as a plain var and left unlinked.</summary>
        DisabledVar,
        /// <summary>A preview image or dependency note kept beside its var.</summary>
        Companion,
        /// <summary>A duplicate or empty marker, kept in the redundant folder.</summary>
        Redundant
    }

    public sealed class MigrationAction
    {
        public MigrationAction(MigrationActionKind kind, string source, string destination, long length)
        {
            Kind = kind;
            Source = source;
            Destination = destination;
            Length = length;
        }

        public MigrationActionKind Kind { get; }
        public string Source { get; }
        public string Destination { get; }
        public long Length { get; }
    }

    public sealed class MigrationPlan
    {
        public List<MigrationAction> Actions { get; } = new List<MigrationAction>();
        public List<string> VarsToInstall { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> LeftInPlace { get; } = new List<string>();
        public bool SameDrive { get; set; }

        public int Count(MigrationActionKind kind)
        {
            return Actions.Count(a => a.Kind == kind);
        }

        private double Gigabytes(MigrationActionKind kind)
        {
            return Actions.Where(a => a.Kind == kind).Sum(a => a.Length) / 1073741824.0;
        }

        public string Describe()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Nothing has been moved yet.");
            text.AppendLine();
            text.AppendLine($"Vars to move and link back: {Count(MigrationActionKind.Var)} ({Gigabytes(MigrationActionKind.Var):F1} GB)");
            text.AppendLine($"Disabled vars to store unlinked: {Count(MigrationActionKind.DisabledVar)} ({Gigabytes(MigrationActionKind.DisabledVar):F1} GB)");
            text.AppendLine($"Images and notes to keep beside their vars: {Count(MigrationActionKind.Companion)}");
            text.AppendLine($"Duplicates and empty markers to set aside: {Count(MigrationActionKind.Redundant)}");
            text.AppendLine($"Other files staying with AddonPackages: {LeftInPlace.Count}");
            text.AppendLine();
            if (!SameDrive)
            {
                text.AppendLine("BLOCKED: the Vars Path must be on the same drive as VaM, so nothing has to be copied.");
                text.AppendLine();
            }
            if (Warnings.Count > 0)
            {
                text.AppendLine("Check these:");
                foreach (string warning in Warnings)
                    text.AppendLine("  " + warning);
                text.AppendLine();
            }
            if (LeftInPlace.Count > 0)
            {
                text.AppendLine("Other files:");
                foreach (string file in LeftInPlace)
                    text.AppendLine("  " + file);
            }
            return text.ToString();
        }
    }

    public sealed class MigrationUndoResult
    {
        public int Restored { get; set; }
        public List<string> Failed { get; } = new List<string>();
    }

    /// <summary>
    /// Moves a real AddonPackages folder into the var repository. Plan touches nothing, Apply
    /// logs each move before making it, and Undo replays that log backwards.
    /// </summary>
    public static class LibraryMigration
    {
        private static readonly string[] CompanionSuffixes = { ".var.depend.txt", ".jpg", ".jpeg", ".png" };

        private sealed class FoundFile
        {
            public string Path;
            public string Stem;
            public long Length;
        }

        public static bool IsVarName(string name)
        {
            string[] parts = name.Split('.');
            return parts.Length == 3 && parts[0].Length > 0 && parts[1].Length > 0
                && parts[2].Length > 0 && parts[2].All(char.IsDigit);
        }

        public static MigrationPlan Plan(string addonPackagesPath, string varsPath, string tidiedDirName,
            string redundantDirName, IEnumerable<string> skipDirNames)
        {
            MigrationPlan plan = new MigrationPlan
            {
                SameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(addonPackagesPath)),
                    Path.GetPathRoot(Path.GetFullPath(varsPath)), StringComparison.OrdinalIgnoreCase)
            };
            string tidiedPath = Path.Combine(varsPath, tidiedDirName);
            string redundantPath = Path.Combine(varsPath, redundantDirName);

            List<FoundFile> vars = new List<FoundFile>();
            List<FoundFile> disabled = new List<FoundFile>();
            List<FoundFile> others = new List<FoundFile>();
            Collect(addonPackagesPath, new HashSet<string>(skipDirNames, StringComparer.OrdinalIgnoreCase), vars, disabled, others, plan);

            HashSet<string> disabledKeys = new HashSet<string>(
                disabled.Select(f => SiblingKey(f.Path, f.Stem)), StringComparer.OrdinalIgnoreCase);
            HashSet<string> plannedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> stored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> install = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Files in creator folders win over stray copies higher up the tree.
            foreach (FoundFile file in vars.OrderByDescending(f => Depth(f.Path)).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (!disabledKeys.Contains(SiblingKey(file.Path, file.Stem)))
                    install.Add(file.Stem);
                string destination = TidiedVarPath(tidiedPath, file.Stem);
                if (stored.Contains(file.Stem) || File.Exists(destination))
                {
                    plan.Warnings.Add($"Duplicate {file.Stem}.var set aside: {file.Path}");
                    AddRedundant(plan, file, redundantPath, plannedDestinations);
                    continue;
                }
                stored.Add(file.Stem);
                plannedDestinations.Add(destination);
                plan.Actions.Add(new MigrationAction(MigrationActionKind.Var, file.Path, destination, file.Length));
            }

            foreach (FoundFile file in disabled.OrderByDescending(f => Depth(f.Path)).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                string destination = TidiedVarPath(tidiedPath, file.Stem);
                if (file.Length == 0 || stored.Contains(file.Stem) || File.Exists(destination))
                {
                    AddRedundant(plan, file, redundantPath, plannedDestinations);
                    continue;
                }
                stored.Add(file.Stem);
                plannedDestinations.Add(destination);
                plan.Actions.Add(new MigrationAction(MigrationActionKind.DisabledVar, file.Path, destination, file.Length));
            }

            foreach (FoundFile file in others.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                string destination = file.Stem == null ? null
                    : Path.Combine(tidiedPath, Creator(file.Stem), Path.GetFileName(file.Path));
                if (destination != null && stored.Contains(file.Stem)
                    && !File.Exists(destination) && plannedDestinations.Add(destination))
                {
                    plan.Actions.Add(new MigrationAction(MigrationActionKind.Companion, file.Path, destination, file.Length));
                }
                else
                {
                    plan.LeftInPlace.Add(file.Path);
                }
            }

            plan.VarsToInstall.AddRange(install.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            return plan;
        }

        private static void Collect(string root, HashSet<string> skipDirNames, List<FoundFile> vars,
            List<FoundFile> disabled, List<FoundFile> others, MigrationPlan plan)
        {
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string subdirectory in Directory.GetDirectories(directory))
                {
                    DirectoryInfo info = new DirectoryInfo(subdirectory);
                    if (!skipDirNames.Contains(info.Name) && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        pending.Push(subdirectory);
                }
                foreach (string path in Directory.GetFiles(directory))
                {
                    FileInfo info = new FileInfo(path);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    string stem;
                    if (TryStrip(info.Name, ".var", out stem))
                    {
                        if (IsVarName(stem))
                            vars.Add(new FoundFile { Path = path, Stem = stem, Length = info.Length });
                        else
                        {
                            plan.Warnings.Add("Name doesn't follow creator.package.version: " + path);
                            plan.LeftInPlace.Add(path);
                        }
                    }
                    else if ((TryStrip(info.Name, ".var.disabled", out stem) || TryStrip(info.Name, ".disabled", out stem))
                        && IsVarName(stem))
                    {
                        disabled.Add(new FoundFile { Path = path, Stem = stem, Length = info.Length });
                    }
                    else
                    {
                        others.Add(new FoundFile { Path = path, Stem = CompanionStem(info.Name), Length = info.Length });
                    }
                }
            }
        }

        private static bool TryStrip(string name, string suffix, out string stem)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                stem = name.Substring(0, name.Length - suffix.Length);
                return true;
            }
            stem = null;
            return false;
        }

        private static string CompanionStem(string name)
        {
            foreach (string suffix in CompanionSuffixes)
            {
                string stem;
                if (TryStrip(name, suffix, out stem) && IsVarName(stem))
                    return stem;
            }
            return null;
        }

        private static string Creator(string stem)
        {
            return stem.Substring(0, stem.IndexOf('.'));
        }

        private static string TidiedVarPath(string tidiedPath, string stem)
        {
            return Path.Combine(tidiedPath, Creator(stem), stem + ".var");
        }

        private static string SiblingKey(string path, string stem)
        {
            return Path.Combine(Path.GetDirectoryName(path), stem);
        }

        private static int Depth(string path)
        {
            return path.Count(c => c == Path.DirectorySeparatorChar);
        }

        private static void AddRedundant(MigrationPlan plan, FoundFile file, string redundantPath, HashSet<string> plannedDestinations)
        {
            string name = Path.GetFileName(file.Path);
            string destination = Path.Combine(redundantPath, name);
            for (int count = 1; File.Exists(destination) || !plannedDestinations.Add(destination); count++)
            {
                destination = Path.Combine(redundantPath, $"{Path.GetFileNameWithoutExtension(name)}({count}){Path.GetExtension(name)}");
            }
            plan.Actions.Add(new MigrationAction(MigrationActionKind.Redundant, file.Path, destination, file.Length));
        }

        /// <summary>Applies a plan. On failure, the moves already made stay listed in the log.</summary>
        public static void Apply(MigrationPlan plan, string logPath, string installListPath)
        {
            if (!plan.SameDrive)
                throw new InvalidOperationException("The Vars Path must be on the same drive as VaM.");

            foreach (MigrationAction action in plan.Actions)
            {
                if (!File.Exists(action.Source))
                    throw new FileNotFoundException("A planned file no longer exists. Nothing was moved.", action.Source);
                if (File.Exists(action.Destination))
                    throw new IOException("A planned destination already exists. Nothing was moved: " + action.Destination);
            }

            List<string> install = File.Exists(installListPath)
                ? File.ReadAllLines(installListPath).ToList()
                : new List<string>();
            File.WriteAllLines(installListPath, install.Concat(plan.VarsToInstall).Distinct(StringComparer.OrdinalIgnoreCase));

            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            using (StreamWriter log = new StreamWriter(logPath, true, new UTF8Encoding(false)))
            {
                log.AutoFlush = true;
                foreach (MigrationAction action in plan.Actions)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(action.Destination));
                    log.WriteLine(action.Source + "\t" + action.Destination);
                    File.Move(action.Source, action.Destination);
                }
            }
        }

        public static MigrationUndoResult Undo(string logPath)
        {
            MigrationUndoResult result = new MigrationUndoResult();
            foreach (string line in File.ReadAllLines(logPath).Reverse())
            {
                string[] paths = line.Split('\t');
                if (paths.Length != 2)
                    continue;
                string source = paths[0], destination = paths[1];
                // The last logged move may not have happened if Apply stopped there.
                if (!File.Exists(destination) && File.Exists(source))
                    continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(source));
                    File.Move(destination, source);
                    result.Restored++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.Failed.Add(destination + ": " + ex.Message);
                }
            }
            return result;
        }
    }
}
