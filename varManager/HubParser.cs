using SimpleJSON;
using System;
using System.Collections.Generic;
using System.IO;

namespace varManager
{
    /// <summary>Reads Hub replies. A reply that isn't what we asked for throws InvalidDataException.</summary>
    public static class HubParser
    {
        private const string VarExtension = ".var";

        /// <summary>Maps var names to download links, from a findPackages reply.</summary>
        public static Dictionary<string, string> DownloadLinks(string response)
        {
            JSONClass packages = Parse(response)["packages"] as JSONClass;
            if (packages == null)
                throw new InvalidDataException("The Hub returned no package list.");

            Dictionary<string, string> links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JSONNode package in packages.Childs)
            {
                string url = package["downloadUrl"];
                string filename = package["filename"];
                if (!IsValue(url) || !IsValue(filename))
                    continue;
                if (filename.EndsWith(VarExtension, StringComparison.OrdinalIgnoreCase))
                    links[filename.Substring(0, filename.Length - VarExtension.Length)] = url;
            }
            return links;
        }

        /// <summary>Maps creator.package to its highest version, from the Hub's package list.</summary>
        public static Dictionary<string, int> LatestVersions(string response)
        {
            JSONClass packages = Parse(response) as JSONClass;
            if (packages == null)
                throw new InvalidDataException("The Hub returned no package list.");

            Dictionary<string, int> latest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in packages.Keys)
            {
                string name = key.EndsWith(VarExtension, StringComparison.OrdinalIgnoreCase)
                    ? key.Substring(0, key.Length - VarExtension.Length) : key;
                int version = VersionOf(name);
                int versionDot = name.LastIndexOf('.');
                if (version < 0 || versionDot <= 0)
                    continue;
                string package = name.Substring(0, versionDot);
                int known;
                if (!latest.TryGetValue(package, out known) || version > known)
                    latest[package] = version;
            }
            return latest;
        }

        /// <summary>The version at the end of a var name, or -1 when it has none.</summary>
        public static int VersionOf(string varName)
        {
            int version;
            return int.TryParse(varName.Substring(varName.LastIndexOf('.') + 1), out version) ? version : -1;
        }

        private static JSONNode Parse(string response)
        {
            JSONNode node;
            try
            {
                node = string.IsNullOrWhiteSpace(response) ? null : JSON.Parse(response);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The Hub reply could not be read.", ex);
            }
            if (node == null)
                throw new InvalidDataException("The Hub reply could not be read.");
            return node;
        }

        private static bool IsValue(string text)
        {
            return !string.IsNullOrEmpty(text) && text != "null";
        }
    }
}
