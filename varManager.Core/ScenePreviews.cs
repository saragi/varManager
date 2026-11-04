using System.IO;
using ICSharpCode.SharpZipLib.Zip;

namespace varManager
{
    public static class ScenePreviews
    {
        private static readonly string[] Types = { "scenes", "looks", "hairstyle", "clothing", "assets", "morphs", "skin", "pose" };

        public static string PicturePath(string varsPath, string type, string varName, string picture)
        {
            return Path.Combine(varsPath, LibraryFolders.PreviewPics, type, varName, picture);
        }

        /// <summary>Removes every preview extracted from a var.</summary>
        public static void DeleteAll(string varsPath, string varName)
        {
            foreach (string type in Types)
            {
                string folder = Path.Combine(varsPath, LibraryFolders.PreviewPics, type, varName);
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        /// <summary>Extracts a scene's preview again. Returns false if the var or its picture is gone.</summary>
        public static bool Restore(string varFile, string scenePath, string picturePath)
        {
            if (!File.Exists(varFile))
                return false;
            using (ZipFile zip = new ZipFile(varFile))
            {
                ZipEntry jpg = zip.GetEntry(scenePath.Substring(0, scenePath.LastIndexOf('.')) + ".jpg");
                if (jpg == null)
                    return false;
                Directory.CreateDirectory(Path.GetDirectoryName(picturePath));
                Extract(zip, jpg, picturePath);
                return true;
            }
        }

        /// <summary>Writes an entry to a file, unless the file is already there.</summary>
        internal static void Extract(ZipFile zip, ZipEntry entry, string path)
        {
            if (File.Exists(path))
                return;
            using (FileStream output = File.Create(path))
            using (Stream input = zip.GetInputStream(entry))
                input.CopyTo(output);
        }
    }
}
