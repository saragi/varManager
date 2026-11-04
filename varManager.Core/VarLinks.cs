using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace varManager
{
    public enum VarLinkResult
    {
        Failed,
        Linked,
        AlreadyLinked
    }

    /// <summary>The links in AddonPackages that make vars visible to VaM.</summary>
    public static class VarLinks
    {
        private const Comm.SYMBOLIC_LINK_FLAG FileLink = Comm.SYMBOLIC_LINK_FLAG.File | Comm.SYMBOLIC_LINK_FLAG.AllowUnprivilegedCreate;

        public static bool IsLink(string path)
        {
            return new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }

        /// <summary>Links an existing file. Returns false if the target is missing or the link can't be made.</summary>
        public static bool Create(string link, string target)
        {
            if (!File.Exists(target))
                return false;
            if (!Comm.CreateSymbolicLink(link, target, FileLink))
                return false;
            try
            {
                CopyTimes(link, target);
            }
            catch (Win32Exception)
            {
                // The link works without its timestamp.
            }
            return true;
        }

        /// <param name="errorCode">The Windows error, when the result is Failed.</param>
        public static VarLinkResult Install(string addonPackagesPath, string varName, string varFile, bool temporary, out int errorCode)
        {
            errorCode = 0;
            string folder = temporary ? LibraryFolders.TempVarLink : LibraryFolders.VarsLink;
            string link = Path.Combine(addonPackagesPath, folder, varName + ".var");
            if (File.Exists(link + ".disabled"))
                File.Delete(link + ".disabled");
            if (File.Exists(link))
                return VarLinkResult.AlreadyLinked;

            if (!Comm.CreateSymbolicLink(link, varFile, FileLink))
            {
                errorCode = Marshal.GetLastWin32Error();
                return VarLinkResult.Failed;
            }
            CopyTimes(link, varFile);
            return VarLinkResult.Linked;
        }

        /// <summary>Linked vars by name: everything in the link folder, plus links directly in AddonPackages.</summary>
        public static Dictionary<string, string> Installed(string addonPackagesPath)
        {
            var installed = new Dictionary<string, string>();
            string linkFolder = Directory.CreateDirectory(Path.Combine(addonPackagesPath, LibraryFolders.VarsLink)).FullName;
            IEnumerable<string> files = Directory.GetFiles(linkFolder, "*.var", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(addonPackagesPath, "*.var", SearchOption.TopDirectoryOnly));
            foreach (string file in files)
            {
                if (IsLink(file))
                    installed[Path.GetFileNameWithoutExtension(file)] = file;
            }
            return installed;
        }

        public static bool Remove(Dictionary<string, string> installed, string varName)
        {
            string link;
            if (!installed.TryGetValue(varName, out link) || !File.Exists(link))
                return false;
            File.Delete(link);
            return true;
        }

        /// <summary>Points a link at its var's current file, or removes it if that var is gone.</summary>
        /// <param name="varFile">Gives a var's file from its name, or null if the library doesn't have it.</param>
        public static void Rebuild(string link, Func<string, string> varFile)
        {
            string file = varFile(Path.GetFileNameWithoutExtension(Comm.ReparsePoint(link)));
            File.Delete(link);
            if (file == null)
                return;
            Comm.CreateSymbolicLink(link, file, FileLink);
            CopyTimes(link, file);
        }

        /// <summary>File names of the temporary links there are now, in lower case.</summary>
        public static List<string> TempLinks(string addonPackagesPath)
        {
            DirectoryInfo folder = Directory.CreateDirectory(Path.Combine(addonPackagesPath, LibraryFolders.TempVarLink));
            return folder.GetFiles()
                .Where(file => file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(file => file.Name.ToLower())
                .ToList();
        }

        public static void RemoveTempLinks(string addonPackagesPath, IEnumerable<string> fileNames)
        {
            foreach (string fileName in fileNames)
            {
                string link = Path.Combine(addonPackagesPath, LibraryFolders.TempVarLink, fileName);
                try
                {
                    if (File.Exists(link))
                        File.Delete(link);
                }
                catch (Exception)
                {
                    // VaM may still hold it, and the next load clears it.
                }
            }
        }

        private static void CopyTimes(string link, string target)
        {
            Comm.SetSymboLinkFileTime(link, File.GetCreationTime(target), File.GetLastWriteTime(target));
        }
    }
}
