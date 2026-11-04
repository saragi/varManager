using SimpleJSON;
using System;
using System.Collections.Generic;
using System.IO;

namespace varManager
{
    /// <summary>A package the Hub offers a link for.</summary>
    public sealed class HubPackage
    {
        public HubPackage(string name, string url, long size)
        {
            Name = name;
            Url = url;
            Size = size;
        }

        /// <summary>The var name, without .var.</summary>
        public string Name { get; }
        public string Url { get; }
        public long Size { get; }
    }

    /// <summary>Reads Hub replies. A reply that isn't what we asked for throws InvalidDataException.</summary>
    public static class HubParser
    {
        private const string VarExtension = ".var";

        /// <summary>Maps var names to download links, from a findPackages reply.</summary>
        public static Dictionary<string, string> DownloadLinks(string response)
        {
            Dictionary<string, string> links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (HubPackage package in Packages(response))
                links[package.Name] = package.Url;
            return links;
        }

        /// <summary>Reads the packages with a link from a findPackages reply.</summary>
        public static List<HubPackage> Packages(string response)
        {
            JSONClass packages = Parse(response)["packages"] as JSONClass;
            if (packages == null)
                throw new InvalidDataException("The Hub returned no package list.");

            List<HubPackage> found = new List<HubPackage>();
            foreach (JSONNode package in packages.Childs)
            {
                string url = package["downloadUrl"];
                string filename = package["filename"];
                if (!IsValue(url) || !IsValue(filename))
                    continue;
                long size;
                long.TryParse(package["file_size"].Value, out size);
                if (filename.EndsWith(VarExtension, StringComparison.OrdinalIgnoreCase))
                    found.Add(new HubPackage(filename.Substring(0, filename.Length - VarExtension.Length), url, size));
            }
            return found;
        }

        /// <summary>Maps creator.package to its highest version, from the Hub's package list.</summary>
        public static Dictionary<string, int> LatestVersions(string response)
        {
            JSONClass packages = Parse(response) as JSONClass;
            if (packages == null || packages.HasKey("error"))
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
