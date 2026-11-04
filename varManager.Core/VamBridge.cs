using System.Diagnostics;
using System.IO;
using SimpleJSON;

namespace varManager
{
    /// <summary>Messages to the plugin running inside VaM, which polls for loadscene.json and deletes it.</summary>
    public static class VamBridge
    {
        public static string RequestFile(string vamPath)
        {
            return Path.Combine(vamPath, "Custom", "PluginData", "feelfar", "loadscene.json");
        }

        public static bool IsRunning()
        {
            return Process.GetProcessesByName("vam").Length > 0;
        }

        /// <summary>Asks a running VaM to scan its packages again. Does nothing if VaM isn't running.</summary>
        public static void RequestRescan(string vamPath)
        {
            if (IsRunning())
                WriteRescan(vamPath);
        }

        public static void WriteRescan(string vamPath)
        {
            string file = RequestFile(vamPath);
            if (File.Exists(file))
                File.Delete(file);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            JSONClass request = new JSONClass();
            request["rescan"] = "true";
            using (StreamWriter writer = new StreamWriter(file))
                writer.Write(request.ToString());
        }
    }
}
