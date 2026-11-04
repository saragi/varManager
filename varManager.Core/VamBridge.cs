using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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

        /// <summary>Removes temporary links once VaM has taken the request, plus a pause for it to finish loading.</summary>
        public static async Task RemoveTempLinksAfterLoad(string vamPath, IReadOnlyList<string> fileNames, TimeSpan pause)
        {
            if (fileNames.Count == 0)
                return;
            string request = RequestFile(vamPath);
            while (File.Exists(request))
                await Task.Delay(pause < TimeSpan.FromSeconds(2) ? pause : TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Task.Delay(pause).ConfigureAwait(false);
            VarLinks.RemoveTempLinks(VarSwitches.AddonPackages(vamPath), fileNames);
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
            JSONClass request = new JSONClass();
            request["rescan"] = "true";
            Write(vamPath, request.ToString());
        }

        /// <summary>Copies a load request and fills in what its resources leave out, up to the first scene.</summary>
        public static JSONClass PrepareLoad(JSONClass request, bool merge, string characterGender, bool ignoreGender, int personOrder, out bool hasScene)
        {
            hasScene = false;
            JSONClass copy = (JSONClass)JSONNode.Parse(request.ToString());
            foreach (JSONClass resource in copy["resources"].AsArray)
            {
                if (!resource.HasKey("merge"))
                    resource["merge"] = merge.ToString().ToLower();
                if (!resource.HasKey("characterGender"))
                    resource["characterGender"] = characterGender;
                if (!resource.HasKey("ignoreGender"))
                    resource["ignoreGender"] = ignoreGender.ToString().ToLower();
                if (!resource.HasKey("personOrder"))
                    resource["personOrder"] = personOrder.ToString();
                if (resource.HasKey("type") && resource["type"].Value == "scenes")
                {
                    hasScene = true;
                    break;
                }
            }
            return copy;
        }

        /// <summary>The var each resource in a load request comes from.</summary>
        public static List<string> ResourceVars(JSONClass request)
        {
            var vars = new List<string>();
            foreach (JSONClass resource in request["resources"].AsArray)
            {
                string saveName = resource["saveName"].Value;
                vars.Add(saveName.Substring(0, saveName.IndexOf(":/")));
            }
            return vars;
        }

        public static void WriteRequest(string vamPath, JSONClass request)
        {
            Write(vamPath, request.ToString("\t"));
        }

        private static void Write(string vamPath, string text)
        {
            string file = RequestFile(vamPath);
            if (File.Exists(file))
                File.Delete(file);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using (StreamWriter writer = new StreamWriter(file))
                writer.Write(text);
        }
    }
}
