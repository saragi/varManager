using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace varManager
{
    public enum SwitchChange
    {
        Unchanged,
        Switched,
        Failed
    }

    /// <summary>The folders under the VaM folder that AddonPackages can link to.</summary>
    public static class VarSwitches
    {
        public const string Default = "default";

        private const Comm.SYMBOLIC_LINK_FLAG FolderLink = Comm.SYMBOLIC_LINK_FLAG.Directory | Comm.SYMBOLIC_LINK_FLAG.AllowUnprivilegedCreate;

        public static string AddonPackages(string vamPath)
        {
            return Path.Combine(vamPath, "AddonPackages");
        }

        public static string LinkFolder(string vamPath, string name)
        {
            return Path.Combine(vamPath, LibraryFolders.AddonPacksSwitch, name, LibraryFolders.VarsLink);
        }

        /// <summary>Switch names with default first. Creates default if it's missing.</summary>
        public static List<string> Names(string vamPath)
        {
            string root = Root(vamPath);
            Directory.CreateDirectory(Path.Combine(root, Default));
            var names = new List<string> { Default };
            names.AddRange(new DirectoryInfo(root).GetDirectories("*", SearchOption.TopDirectoryOnly)
                .Select(folder => folder.Name)
                .Where(name => name != Default));
            return names;
        }

        /// <summary>Makes AddonPackages a working link, and returns the name of the switch it points to.</summary>
        public static string EnsureLink(string vamPath)
        {
            string root = Root(vamPath);
            string defaultSwitch = Path.Combine(root, Default);
            string packPath = new DirectoryInfo(AddonPackages(vamPath)).FullName;

            if (IsRealFolder(packPath))
                Comm.DirectoryMoveAll(packPath, defaultSwitch);
            if (IsRealFolder(packPath))
                new DirectoryInfo(packPath).MoveTo(Path.Combine(vamPath, $"AddonPackages_{DateTime.Now:yyyyMMddHHmmss}"));
            if (!Directory.Exists(packPath))
                Comm.CreateSymbolicLink(packPath, defaultSwitch, FolderLink);

            var target = new DirectoryInfo(Comm.ReparsePoint(packPath));
            if (!target.Exists)
            {
                new DirectoryInfo(packPath).Delete();
                Comm.CreateSymbolicLink(packPath, defaultSwitch, FolderLink);
                target = new DirectoryInfo(Comm.ReparsePoint(packPath));
            }
            return target.Name;
        }

        /// <summary>Points AddonPackages at a switch, creating the switch if it's new.</summary>
        /// <param name="errorCode">The Windows error, when the result is Failed.</param>
        public static SwitchChange Point(string vamPath, string name, out int errorCode)
        {
            errorCode = 0;
            var target = new DirectoryInfo(Path.Combine(Root(vamPath), name));
            if (!target.Exists)
                target.Create();

            var pack = new DirectoryInfo(AddonPackages(vamPath));
            if (pack.Exists)
            {
                if (string.Equals(Comm.ReparsePoint(pack.FullName).ToLower(), target.FullName.ToLower(), StringComparison.Ordinal))
                    return SwitchChange.Unchanged;
                pack.Delete();
            }
            if (!Comm.CreateSymbolicLink(pack.FullName, target.FullName, FolderLink))
            {
                errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return SwitchChange.Failed;
            }
            return SwitchChange.Switched;
        }

        public static void Delete(string vamPath, string name)
        {
            new DirectoryInfo(Path.Combine(Root(vamPath), name)).Delete(true);
        }

        /// <summary>Renames a switch. If AddonPackages points to it, call Point with the new name next.</summary>
        public static void Rename(string vamPath, string oldName, string newName)
        {
            string root = Root(vamPath);
            new DirectoryInfo(Path.Combine(root, oldName)).MoveTo(Path.Combine(root, newName));
        }

        /// <summary>A name not in use, adding a number if needed. Names ignore case.</summary>
        public static string UnusedName(IEnumerable<string> existing, string name)
        {
            var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            string candidate = name;
            for (int number = 2; used.Contains(candidate); number++)
                candidate = name + " " + number;
            return candidate;
        }

        private static bool IsRealFolder(string path)
        {
            var folder = new DirectoryInfo(path);
            return folder.Exists && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }

        private static string Root(string vamPath)
        {
            string root = new DirectoryInfo(Path.Combine(vamPath, LibraryFolders.AddonPacksSwitch)).FullName.ToLower();
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
