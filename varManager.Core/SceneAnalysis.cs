using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimpleJSON;

namespace varManager
{
    /// <summary>Unpacks a scene or look into the Cache folder, so its atoms can be listed and loaded one by one.</summary>
    public static class SceneAnalysis
    {
        private const string DependFile = "depend.txt";
        private const string GenderFile = "gender.txt";

        private static readonly string[] BaseAtoms = { "CoreControl", "PlayerNavigationPanel", "VRController", "WindowCamera" };

        /// <summary>Splits "var:/entry". A save outside any var gives the var name "save".</summary>
        public static (string varName, string entryName) SplitSaveName(string saveName)
        {
            string varName = "save", entryName = saveName;
            if (saveName.IndexOf(":/", StringComparison.Ordinal) > 1)
            {
                string[] parts = saveName.Split(new[] { ":/" }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    varName = parts[0];
                    entryName = parts[1];
                }
            }
            return (varName, entryName);
        }

        public static string CacheFolder(string varName, string entryName)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "Cache",
                Comm.ValidFileName(varName), Comm.ValidFileName(entryName.Replace('\\', '_').Replace('/', '_')));
        }

        public static string CharacterGender(string character)
        {
            string gender = "Female";
            character = character.ToLower();
            // If the person atom is not "On", then we can't determine their gender it seems as GetComponentInChildren<DAZCharacter> just returns null
            if (character.StartsWith("male") ||
                character.StartsWith("lee") ||
                character.StartsWith("jarlee") ||
                character.StartsWith("julian") ||
                character.StartsWith("jarjulian"))
            {
                gender = "Male";
            }
            if (character.StartsWith("futa"))
                gender = "Futa";
            return gender;
        }

        /// <summary>Writes a save's dependency and gender files, and its atoms when asked.</summary>
        /// <param name="varFile">The var holding the save, or null for a save in the VaM folder.</param>
        public static void Prepare(string saveName, string varFile, string vamPath, string characterGender, bool analysis)
        {
            (string varName, string entryName) = SplitSaveName(saveName);
            var depends = new List<string>();
            string json = "";
            if (varName != "save")
            {
                depends.Add(varName);
                json = VarScanner.ReadEntry(varFile, entryName);
            }
            else
            {
                string jsonFile = Path.Combine(vamPath, saveName);
                if (File.Exists(jsonFile))
                    json = File.ReadAllText(jsonFile);
            }

            if (characterGender == "unknown")
            {
                characterGender = json.IndexOf("/Female/", StringComparison.Ordinal) > 0
                    || saveName.IndexOf("/Female/", StringComparison.Ordinal) > 0 ? "female" : "male";
            }

            depends.AddRange(DependencyScanner.Extract("meta.json", json));
            string folder = CacheFolder(varName, entryName);
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, DependFile), depends);
            File.WriteAllLines(Path.Combine(folder, GenderFile), new[] { characterGender });

            if (analysis)
                SplitAtoms(json.Replace("\"SELF:/", "\"" + varName + ":/"), folder, true);
        }

        public static List<string> ReadDepends(string cacheFolder)
        {
            return File.ReadAllLines(Path.Combine(cacheFolder, DependFile)).ToList();
        }

        public static string ReadGender(string cacheFolder)
        {
            return File.ReadAllLines(Path.Combine(cacheFolder, GenderFile)).FirstOrDefault();
        }

        private static string AtomId(JSONNode atom, bool isPerson)
        {
            if (!isPerson)
                return atom["id"].Value;

            string gender = "unknown";
            foreach (JSONNode storable in atom["storables"].AsArray)
            {
                if (storable["id"].Value == "geometry")
                {
                    gender = CharacterGender(storable["character"].Value);
                    break;
                }
            }
            return string.Format("({1}){0}", atom["id"], gender);
        }

        private static void SplitAtoms(string json, string folder, bool isPerson)
        {
            JSONClass root = (JSONClass)JSON.Parse(json);
            if (!root.HasKey("atoms"))
            {
                string atomFolder = folder;
                if (isPerson)
                {
                    atomFolder = Path.Combine(folder, "atoms", "Person");
                    Directory.CreateDirectory(atomFolder);
                }
                root.SaveToFile(Path.Combine(atomFolder, Comm.ValidFileName(AtomId(root, isPerson) + ".bin")));
                return;
            }

            JSONClass position = new JSONClass();
            foreach (KeyValuePair<string, JSONNode> item in root)
            {
                if (item.Key != "atoms")
                    position.Add(item.Key, item.Value);
            }
            position.SaveToFile(Path.Combine(folder, "posinfo.bin"));

            JSONArray atoms = root["atoms"].AsArray;
            if (atoms.Count == 0)
                return;

            var types = new List<string>();
            var parents = new Dictionary<string, List<string>>();
            foreach (JSONClass atom in atoms)
            {
                string type = atom["type"];
                if (BaseAtoms.Contains(type))
                    type = "(base)" + type;
                if (string.IsNullOrEmpty(type))
                    continue;

                string typeFolder = Path.Combine(folder, "atoms", type);
                if (!types.Contains(type))
                {
                    types.Add(type);
                    Directory.CreateDirectory(typeFolder);
                }
                if (type == "SubScene")
                {
                    SplitAtoms(atom.ToString(), typeFolder, false);
                    continue;
                }

                string id = AtomId(atom, type == "Person");
                if (atom.HasKey("parentAtom") && !string.IsNullOrEmpty(atom["parentAtom"]))
                {
                    string parent = Comm.ValidFileName(atom["parentAtom"]);
                    if (!parents.ContainsKey(parent))
                        parents.Add(parent, new List<string>());
                    parents[parent].Add(Comm.ValidFileName(id));
                }
                atom.SaveToFile(Path.Combine(typeFolder, Comm.ValidFileName(id + ".bin")));
            }

            if (parents.Count > 0)
            {
                File.WriteAllLines(Path.Combine(folder, "parentAtom.txt"),
                    parents.Select(parent => parent.Key + "\t" + string.Join(",", parent.Value)));
            }
        }
    }
}
