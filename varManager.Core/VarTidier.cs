using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace varManager
{
    public static class VarTidier
    {
        private static readonly string[] SortedFolders =
        {
            LibraryFolders.Tidied, LibraryFolders.Redundant, LibraryFolders.NotComplyRule,
            LibraryFolders.StaleVars, LibraryFolders.OldVersionVars, LibraryFolders.DeletedVars, LibraryFolders.Holding
        };

        public static bool IsVarName(string name)
        {
            string[] parts = name.Split('.');
            return parts.Length == 3 && Regex.IsMatch(parts[2], "^[0-9]+$");
        }

        public static bool IsVarFile(string path)
        {
            return IsVarName(Path.GetFileNameWithoutExtension(path));
        }

        // Real var files in the repository that aren't in a sorted folder yet.
        /// <summary>A var still being copied can't be opened exclusively.</summary>
        public static bool RepositoryVarsReady(string varsPath)
        {
            foreach (string file in Directory.GetFiles(varsPath, "*.var"))
            {
                try
                {
                    using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    // Update DB logs a var it can't read, so this one needn't hold the others back.
                }
            }
            return true;
        }

        public static List<string> RepositoryVars(string varsPath)
        {
            return Directory.GetFiles(varsPath, "*.var", SearchOption.AllDirectories)
                .Where(path => !SortedFolders.Any(folder => path.IndexOf(folder, StringComparison.Ordinal) != -1))
                .Where(IsRealFile)
                .ToList();
        }

        // Real var files in AddonPackages, which the app wants to be links.
        public static IEnumerable<string> LooseAddonVars(string addonPackagesPath)
        {
            string linkFolder = Path.Combine(addonPackagesPath, LibraryFolders.VarsLink);
            return Directory.GetFiles(addonPackagesPath, "*.var", SearchOption.AllDirectories)
                .Where(path => path.IndexOf(linkFolder, StringComparison.Ordinal) == -1
                    && path.IndexOf(LibraryFolders.MissingVarLink, StringComparison.Ordinal) == -1
                    && path.IndexOf(LibraryFolders.TempVarLink, StringComparison.Ordinal) == -1)
                .Where(IsRealFile);
        }

        // Tidying moves loose vars out of AddonPackages, so their names are kept to link them again afterwards.
        public static List<string> MergeInstallList(IEnumerable<string> saved, IEnumerable<string> looseVars)
        {
            return saved
                .Concat(looseVars.Where(IsVarFile).Select(Path.GetFileNameWithoutExtension))
                .Distinct()
                .ToList();
        }

        // Sorts vars into the tidied folder by creator. A file that can't move is reported and left in place.
        public static void Tidy(IList<string> vars, string varsPath, Action<string> problem, Action<int, int> progress)
        {
            string tidiedPath = Path.Combine(varsPath, LibraryFolders.Tidied);
            string redundantPath = Path.Combine(varsPath, LibraryFolders.Redundant);
            string notComplyPath = Path.Combine(varsPath, LibraryFolders.NotComplyRule);
            Directory.CreateDirectory(tidiedPath);
            Directory.CreateDirectory(redundantPath);
            Directory.CreateDirectory(notComplyPath);

            for (int i = 0; i < vars.Count; i++)
            {
                string file = vars[i];
                string fileName = Path.GetFileName(file);
                string destination;
                if (IsVarFile(file))
                {
                    string creatorPath = Path.Combine(tidiedPath, fileName.Split('.')[0]);
                    Directory.CreateDirectory(creatorPath);
                    destination = Path.Combine(creatorPath, fileName);
                    if (File.Exists(destination))
                    {
                        problem($"{file} has same filename in tidy directory,moved into the {LibraryFolders.Redundant} directory");
                        destination = UnusedPath(Path.Combine(redundantPath, fileName));
                    }
                }
                else
                {
                    problem($"{file} not comply Var filename rule, move into {LibraryFolders.NotComplyRule} directory");
                    destination = UnusedPath(Path.Combine(notComplyPath, fileName));
                }

                try
                {
                    File.Move(file, destination);
                }
                catch (Exception ex)
                {
                    problem($"move {file} failed, {ex.Message}");
                }
                progress(i, vars.Count);
            }
        }

        private static bool IsRealFile(string path)
        {
            return !new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }

        private static string UnusedPath(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            string folder = Path.GetDirectoryName(path);
            string candidate = path;
            for (int count = 1; File.Exists(candidate); count++)
                candidate = Path.Combine(folder, $"{name}({count}){extension}");
            return candidate;
        }
    }
}
