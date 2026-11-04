using System;
using System.IO;
using System.Text.Json;

namespace varManager.Ui
{
    /// <summary>The app's two folders, kept in settings.json beside the program.</summary>
    public sealed class AppSettings
    {
        public string VarsPath { get; set; } = "";
        public string VamPath { get; set; } = "";

        public static string DefaultFile => Path.Combine(AppContext.BaseDirectory, "settings.json");

        /// <summary>A file that is missing or can't be read gives empty settings, and the app then asks for the folders.</summary>
        public static AppSettings Load(string file)
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new AppSettings();
            }
        }

        public void Save(string file)
        {
            string part = file + ".part";
            File.WriteAllText(part, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(part, file, true);
        }

        /// <summary>Says what's wrong with the folders, or returns null if the app can use them.</summary>
        public string? Problem()
        {
            if (string.IsNullOrWhiteSpace(VamPath) || !File.Exists(Path.Combine(VamPath, "VaM.exe")))
                return "Choose the folder that holds VaM.exe.";
            if (string.IsNullOrWhiteSpace(VarsPath) || !Directory.Exists(VarsPath))
                return "Choose the folder that holds your vars.";

            string packs = Path.GetFullPath(VarSwitches.AddonPackages(VamPath)).TrimEnd(Path.DirectorySeparatorChar);
            string vars = Path.GetFullPath(VarsPath).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(vars, packs, StringComparison.OrdinalIgnoreCase)
                || vars.StartsWith(packs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "The vars folder can't be AddonPackages or a folder inside it.";

            var folder = new DirectoryInfo(packs);
            if (folder.Exists && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return "AddonPackages is a real folder. Move it into your library with the old varManager first.";
            return null;
        }
    }
}
