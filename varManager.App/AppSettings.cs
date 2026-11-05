using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace varManager.Ui
{
    /// <summary>The app's two folders, kept in settings.json beside the program.</summary>
    public sealed class AppSettings
    {
        public string VarsPath { get; set; } = "";
        public string VamPath { get; set; } = "";

        /// <summary>Off by default, because the window drew white on the user's PC with it on.</summary>
        public bool HardwareRendering { get; set; }

        [JsonIgnore]
        public string PixeldrainKey { get; set; } = "";

        /// <summary>The key as settings.json holds it, which only this Windows account can read back.</summary>
        public string SealedPixeldrainKey
        {
            get => PixeldrainKey.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(PixeldrainKey), null, DataProtectionScope.CurrentUser));
            set => PixeldrainKey = Unseal(value);
        }

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

        // A file copied from another account can't be read, and downloads then stay anonymous until the key is entered again.
        private static string Unseal(string sealedKey)
        {
            try
            {
                return sealedKey.Length == 0 ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(sealedKey), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return "";
            }
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

            if (File.Exists(packs))
                return "AddonPackages is a file. Move or rename it, then try again.";

            var folder = new DirectoryInfo(packs);
            if (folder.Exists && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return "AddonPackages is a real folder. Move it into your library with the old varManager first.";
            return null;
        }
    }
}
