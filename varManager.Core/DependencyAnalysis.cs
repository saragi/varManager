using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace varManager
{
    /// <summary>What a reference such as "A.B.latest" points to in the library.</summary>
    public sealed class ResolvedReference
    {
        public ResolvedReference(string reference, string varName, bool isSubstitute)
        {
            Reference = reference;
            VarName = varName;
            IsSubstitute = isSubstitute;
        }

        public string Reference { get; }

        // Null when the library has no version of the package.
        public string VarName { get; }

        // The exact version is missing, so VarName is another version of the package.
        public bool IsSubstitute { get; }
    }

    public static class DependencyAnalysis
    {
        private const int MaxSavePathLength = 255;

        /// <summary>Vars that need this var. A latest var also counts for ".latest" and ".minN" references.</summary>
        public static List<string> DependentVars(string varName, VarIndex vars, DependencyIndex dependencies)
        {
            var dependents = new List<string>(dependencies.DependentsOf(varName));
            if (vars.IsLatest(varName))
            {
                string package = varName.Substring(0, varName.LastIndexOf('.'));
                dependents.AddRange(dependencies.DependentsOf(package + ".latest"));
                int version;
                if (int.TryParse(varName.Substring(package.Length + 1), out version))
                    dependents.AddRange(dependencies.MinimumDependentsOf(package, version));
            }
            return dependents.Distinct().ToList();
        }

        /// <summary>
        /// The given vars and everything that would break without them, following dependents all the way.
        /// A var with another version isn't followed, because references can use that version instead.
        /// </summary>
        public static List<string> ImplicatedVars(IEnumerable<string> varNames, VarIndex vars, DependencyIndex dependencies)
        {
            var processed = new List<string>();
            List<string> frontier = varNames.ToList();
            while (frontier.Count > 0)
            {
                List<string> implicated = frontier
                    .Where(varName => VersionCount(varName, vars) <= 1)
                    .SelectMany(varName => DependentVars(varName, vars, dependencies))
                    .ToList();
                processed.AddRange(frontier);
                frontier = implicated.Except(processed).ToList();
            }
            return processed.Distinct().ToList();
        }

        /// <summary>The library vars the references resolve to, and everything those need, all the way down.</summary>
        public static List<string> WithDependencies(IEnumerable<string> references, VarIndex vars, DependencyIndex dependencies)
        {
            var processed = new List<string>();
            List<string> frontier = references.ToList();
            while (frontier.Count > 0)
            {
                List<string> found = Resolve(frontier, vars)
                    .Where(resolved => resolved.VarName != null)
                    .Select(resolved => resolved.VarName)
                    .Distinct()
                    .Except(processed)
                    .ToList();
                processed.AddRange(found);
                frontier = found.SelectMany(dependencies.DependenciesOf).Except(processed).ToList();
            }
            return processed;
        }

        public static List<ResolvedReference> Resolve(IEnumerable<string> references, VarIndex vars)
        {
            var results = new List<ResolvedReference>();
            foreach (string reference in references)
            {
                string resolved = vars.Resolve(reference);
                bool isSubstitute = resolved.EndsWith("$", StringComparison.Ordinal);
                if (isSubstitute)
                    resolved = resolved.Substring(0, resolved.Length - 1);
                results.Add(new ResolvedReference(reference, resolved == VarIndex.Missing ? null : resolved, isSubstitute));
            }
            return results;
        }

        /// <summary>The list the Missing window takes. A reference ending in $ has only another version.</summary>
        public static List<string> MissingList(IEnumerable<ResolvedReference> resolved)
        {
            var missing = new List<string>();
            foreach (ResolvedReference reference in resolved)
            {
                if (reference.IsSubstitute)
                    missing.Add(reference.Reference + "$");
                if (reference.VarName == null)
                    missing.Add(reference.Reference);
            }
            return missing;
        }

        /// <summary>The highest version of each package that has more than one, keyed by "creator.package".</summary>
        public static Dictionary<string, int> LatestVersions(IEnumerable<VarEntry> vars)
        {
            return vars.GroupBy(PackageOf)
                .Where(package => package.Count() > 1)
                .ToDictionary(package => package.Key, package => package.Max(entry => entry.Version));
        }

        public static List<string> OldVersions(IEnumerable<VarEntry> vars, Dictionary<string, int> latestVersions)
        {
            var old = new List<string>();
            foreach (VarEntry entry in vars)
            {
                int latest;
                if (latestVersions.TryGetValue(PackageOf(entry), out latest) && entry.Version != latest)
                    old.Add(entry.VarName);
            }
            return old;
        }

        /// <summary>Scenes in Saves and presets in Custom, which can reference vars from outside any var.</summary>
        public static List<string> SaveFiles(string vamPath)
        {
            List<string> files = Directory.GetFiles(Path.Combine(vamPath, "Saves"), "*.json", SearchOption.AllDirectories).ToList();
            files.AddRange(Directory.GetFiles(Path.Combine(vamPath, "Custom"), "*.vap", SearchOption.AllDirectories));
            return files;
        }

        /// <summary>A save's path relative to VaM, keeping the end if it won't fit the database column.</summary>
        public static string SavePath(string vamPath, string file)
        {
            string path = file.Substring(vamPath.Length);
            return path.Length > MaxSavePathLength ? path.Substring(path.Length - MaxSavePathLength) : path;
        }

        private static int VersionCount(string varName, VarIndex vars)
        {
            string[] parts = varName.Split('.');
            return parts.Length == 3 ? vars.VersionCount(parts[0], parts[1]) : 0;
        }

        private static string PackageOf(VarEntry entry)
        {
            return entry.CreatorName + "." + entry.PackageName;
        }
    }
}
