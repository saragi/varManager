using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace varManager
{
    public enum MissingScope
    {
        Library,
        Profile,

        /// <summary>Scenes and presets the user saved, which refer to vars from outside any var.</summary>
        Saves
    }

    /// <summary>What the Missing screen needs from the Hub, so tests can stand in for it.</summary>
    public interface IHubSource
    {
        /// <summary>Raised on the download's thread.</summary>
        event Action<DownloadUpdate> Downloading;

        Task<List<HubPackage>> Find(IEnumerable<string> references);
        Task<string> PackageList();
        Task<DownloadResult> Download(IReadOnlyList<HubPackage> packages, string folder, Action<HubPackage> finished, CancellationToken token);
    }

    public sealed class RealHub : IHubSource
    {
        public event Action<DownloadUpdate> Downloading;

        public async Task<List<HubPackage>> Find(IEnumerable<string> references)
        {
            List<HubPackage> found = await HubClient.FindAll(references).ConfigureAwait(false);
            return found.Where(package => HubBrowse.IsHubUrl(package.Url)).ToList();
        }

        public Task<string> PackageList()
        {
            return HubClient.GetPackageList();
        }

        public async Task<DownloadResult> Download(IReadOnlyList<HubPackage> packages, string folder, Action<HubPackage> finished, CancellationToken token)
        {
            using (var downloader = new HubDownloader { Watch = update => Downloading?.Invoke(update) })
                return await downloader.DownloadAll(packages, folder, finished, token).ConfigureAwait(false);
        }
    }

    /// <summary>The library as Missing sees it: what's owned, what's linked and what's needed.</summary>
    public sealed class MissingLibrary
    {
        private readonly VarIndex vars;
        private readonly DependencyIndex dependencies;
        private readonly List<string> names;
        private readonly List<string> linked;
        private readonly LibraryPaths paths;

        public MissingLibrary(LibraryStore store, LibraryPaths paths)
        {
            this.paths = paths;
            IReadOnlyList<VarRecord> records = store.LoadVars();
            vars = new VarIndex(records.Select(v => new VarEntry(v.VarName, v.CreatorName, v.PackageName, v.Version ?? 0)));
            dependencies = new DependencyIndex(store.LoadDependencies());
            names = records.Select(v => v.VarName).ToList();
            linked = VarLinks.ReadInstalled(paths.AddonPackages).Keys.ToList();
        }

        public IReadOnlyList<string> ScopeVars(MissingScope scope)
        {
            switch (scope)
            {
                case MissingScope.Profile:
                    return linked;
                case MissingScope.Saves:
                    return new string[0];
                default:
                    return names;
            }
        }

        /// <summary>
        /// Owned vars of the packages in scope, in every version. An update is measured against the newest
        /// owned version, linked or not, so an older one is never fetched over it.
        /// </summary>
        public IReadOnlyList<string> UpdateCandidates(MissingScope scope)
        {
            var packages = new HashSet<string>(ScopeVars(scope).Select(MissingPlanner.PackageOf), StringComparer.OrdinalIgnoreCase);
            if (packages.Count == 0)
                return new string[0];
            return names.Where(name => packages.Contains(MissingPlanner.PackageOf(name))).ToList();
        }

        public MissingReport Plan(MissingScope scope)
        {
            if (scope == MissingScope.Saves)
                return PlanSaves();
            return MissingPlanner.Plan(ScopeVars(scope), name => dependencies.DependenciesOf(name), vars, linked);
        }

        // A save is in use by being there, so what it needs counts as needed by something linked.
        private MissingReport PlanSaves()
        {
            var references = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in MoveOut.SaveFiles(paths.VamPath))
            {
                try
                {
                    references[DependencyAnalysis.SavePath(paths.VamPath, file)] = DependencyScanner.Extract("meta.json", File.ReadAllText(file)).ToList();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // A save that can't be read is left out, and the rest still help.
                }
            }
            return MissingPlanner.Plan(references.Keys, name => references[name], vars, linked.Concat(references.Keys));
        }

        public bool Owns(string varName)
        {
            return vars.Contains(varName);
        }

        /// <summary>Links an update when an older version of its package is linked.</summary>
        public List<string> UpdatesToLink(IEnumerable<HubPackage> updates)
        {
            var packages = new HashSet<string>(linked.Select(MissingPlanner.PackageOf), StringComparer.OrdinalIgnoreCase);
            return updates.Select(update => update.Name).Where(name => packages.Contains(MissingPlanner.PackageOf(name))).ToList();
        }
    }

    /// <summary>What's missing, and where each thing can come from.</summary>
    public sealed class MissingState
    {
        public MissingReport Report { get; set; } = new MissingReport();
        public MissingSources Sources { get; set; } = new MissingSources();
        public List<HubPackage> Offered { get; set; } = new List<HubPackage>();
        public List<HubPackage> Updates { get; set; } = new List<HubPackage>();

        /// <summary>Direct links for what's in Sources.Mirror.</summary>
        public List<HubPackage> Mirrored { get; } = new List<HubPackage>();
        public string HubProblem { get; set; }

        public bool IsNeeded(HubPackage package)
        {
            string name = MissingPlanner.PackageOf(package.Name);
            return Sources.OnHub.Any(item => string.Equals(item.Package, name, StringComparison.OrdinalIgnoreCase));
        }

        public long HubBytes => Offered.Where(IsNeeded).Sum(package => package.Size);

        /// <summary>Downloads that something linked needs. The rest stay stored and off.</summary>
        public List<string> LinkedNeeds()
        {
            var wanted = new HashSet<string>(Sources.OnHub.Concat(Sources.Mirror).Where(item => item.NeededByLinkedVar).Select(item => item.Package), StringComparer.OrdinalIgnoreCase);
            return Offered.Concat(Mirrored).Where(package => wanted.Contains(MissingPlanner.PackageOf(package.Name))).Select(package => package.Name).ToList();
        }
    }

    public static class MissingCheck
    {
        // Left free on the drive, so a download can't fill it.
        private const long ReservedBytes = 1L << 30;

        /// <summary>Asks the Hub about what the library lacks. A Hub that can't be reached isn't an error here.</summary>
        public static async Task<MissingState> Run(MissingLibrary library, MissingScope scope, IHubSource hub, CommunityList community, CancellationToken token)
        {
            var view = new MissingState { Report = library.Plan(scope) };
            view.Sources.NotFound.AddRange(view.Report.Needed);
            try
            {
                // Asked directly, because the Hub's package list doesn't hold every package.
                view.Offered = await hub.Find(view.Report.Needed.Select(item => item.Package + ".latest")).ConfigureAwait(false);
                string packageList = await hub.PackageList().ConfigureAwait(false);
                Dictionary<string, int> versions = HubParser.LatestVersions(packageList);
                view.Updates = (await hub.Find(MissingPlanner.UpdateRequests(library.UpdateCandidates(scope), versions)).ConfigureAwait(false))
                    .Where(package => !library.Owns(package.Name)).ToList();
            }
            catch (Exception ex)
            {
                // HttpClient reports a timeout as a cancellation, so only the job's own token counts as one.
                token.ThrowIfCancellationRequested();
                view.HubProblem = "The Hub couldn't be reached: " + ex.Message;
                return view;
            }
            view.Sources = MissingPlanner.Sort(view.Report.Needed, view.Offered, community.Packages);
            foreach (MissingPackage item in view.Sources.Listed.ToList())
            {
                HubPackage link = community.Best(item);
                if (link == null)
                    continue;
                view.Sources.Listed.Remove(item);
                view.Sources.Mirror.Add(item);
                view.Mirrored.Add(link);
            }
            return view;
        }

        /// <summary>The names and links in a local SharpVaMTools install, if there is one.</summary>
        public static CommunityList ReadCommunity(string vamPath)
        {
            string install = SharpVaMToolsData.FindBeside(vamPath);
            if (install == null)
                return new CommunityList();
            try
            {
                return SharpVaMToolsData.Read(install);
            }
            catch (IOException)
            {
                return new CommunityList();
            }
        }

        /// <summary>Downloads what's needed, and keeps going until the downloads need nothing more.</summary>
        public static string GetAll(string storeFile, LibraryPaths paths, MissingScope scope, IHubSource hub, CommunityList community,
            Func<string, long> freeSpace, Action<int, int> progress, CancellationToken token)
        {
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var turnedOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int stored = 0;
            int failed = 0;
            string firstProblem = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                MissingState view = Check(storeFile, paths, scope, hub, community, token);
                if (view.HubProblem != null)
                    return view.HubProblem;

                List<HubPackage> next = view.Offered.Where(view.IsNeeded).Concat(view.Mirrored).Where(package => !attempted.Contains(package.Name)).ToList();
                if (next.Count > 0)
                {
                    attempted.UnionWith(next.Select(package => package.Name));
                    string noRoom = NoRoom(next, paths.VarsPath, freeSpace);
                    if (noRoom != null)
                        return noRoom;
                    DownloadResult result = Store(next, view.LinkedNeeds(), storeFile, paths, hub, progress, token);
                    stored += result.Stored.Count;
                    failed += result.Failed.Count;
                    firstProblem = firstProblem ?? result.Failed.Values.FirstOrDefault();
                    continue;
                }

                // Stored vars the downloads need, and what those need in turn. Other turned-off vars
                // stay off, because they may be off on purpose.
                List<string> needed = view.Report.TurnedOff
                    .Where(off => !turnedOn.Contains(off.Key) && (off.Value.Overlaps(attempted) || off.Value.Overlaps(turnedOn)))
                    .Select(off => off.Key).ToList();
                if (needed.Count == 0)
                    break;
                turnedOn.UnionWith(needed);
                LinkAndUpdate(needed, storeFile, paths, progress, token);
            }

            string text = $"Stored {stored}, failed {failed}.";
            return firstProblem == null ? text : text + " First problem: " + firstProblem;
        }

        /// <summary>Gets the updates the user asked for. Never runs by itself, because a new version can change a scene.</summary>
        public static string GetUpdates(string storeFile, LibraryPaths paths, MissingScope scope, IHubSource hub, CommunityList community,
            Func<string, long> freeSpace, Action<int, int> progress, CancellationToken token)
        {
            MissingState view = Check(storeFile, paths, scope, hub, community, token);
            if (view.HubProblem != null)
                return view.HubProblem;
            string noRoom = NoRoom(view.Updates, paths.VarsPath, freeSpace);
            if (noRoom != null)
                return noRoom;
            List<string> toLink;
            using (LibraryStore store = LibraryStore.Open(storeFile))
                toLink = new MissingLibrary(store, paths).UpdatesToLink(view.Updates);
            DownloadResult result = Store(view.Updates, toLink, storeFile, paths, hub, progress, token);
            return $"Stored {result.Stored.Count}, failed {result.Failed.Count}.";
        }

        /// <summary>Gets packages the user picked, scans them and links them to this profile.</summary>
        /// <param name="alsoLink">Owned vars the pick needs and this profile doesn't link.</param>
        public static string GetPackages(IReadOnlyList<HubPackage> packages, IReadOnlyList<string> alsoLink, string storeFile, LibraryPaths paths, IHubSource hub,
            Func<string, long> freeSpace, Action<int, int> progress, CancellationToken token)
        {
            string noRoom = NoRoom(packages, paths.VarsPath, freeSpace);
            if (noRoom != null)
                return noRoom;
            if (packages.Count == 0)
            {
                LinkAndUpdate(alsoLink.ToList(), storeFile, paths, progress, token);
                return alsoLink.Count == 1 ? "Turned on 1 var." : $"Turned on {alsoLink.Count} vars.";
            }
            DownloadResult result = Store(packages, packages.Select(package => package.Name).ToList(), storeFile, paths, hub, progress, token, alsoLink);
            string text = $"Stored {result.Stored.Count}, failed {result.Failed.Count}.";
            return result.Failed.Count == 0 ? text : text + " First problem: " + result.Failed.Values.First();
        }

        /// <summary>Turns on stored vars that linked vars need.</summary>
        public static string TurnOnAll(string storeFile, LibraryPaths paths, MissingScope scope, Action<int, int> progress, CancellationToken token)
        {
            List<string> off;
            using (LibraryStore store = LibraryStore.Open(storeFile))
                off = new MissingLibrary(store, paths).Plan(scope).TurnedOff.Keys.ToList();
            if (off.Count == 0)
                return "Nothing to turn on.";
            LinkAndUpdate(off, storeFile, paths, progress, token);
            return off.Count == 1 ? "Turned on 1 var." : $"Turned on {off.Count} vars.";
        }

        private static MissingState Check(string storeFile, LibraryPaths paths, MissingScope scope, IHubSource hub, CommunityList community, CancellationToken token)
        {
            MissingLibrary library;
            using (LibraryStore store = LibraryStore.Open(storeFile))
                library = new MissingLibrary(store, paths);
            return Run(library, scope, hub, community, token).GetAwaiter().GetResult();
        }

        private static string NoRoom(IReadOnlyCollection<HubPackage> packages, string folder, Func<string, long> freeSpace)
        {
            long needed = packages.Sum(package => package.Size);
            long free = freeSpace(folder);
            return needed + ReservedBytes > free ? $"This needs {Size(needed)}, but only {Size(free)} is free." : null;
        }

        private static DownloadResult Store(IReadOnlyList<HubPackage> packages, List<string> toLink, string storeFile, LibraryPaths paths,
            IHubSource hub, Action<int, int> progress, CancellationToken token, IReadOnlyList<string> alsoLink = null)
        {
            int done = 0;
            DownloadResult result = hub.Download(packages, paths.VarsPath,
                package => progress(Interlocked.Increment(ref done), packages.Count), token).GetAwaiter().GetResult();
            // Only once something arrived: a Get that stored nothing mustn't turn anything on later.
            if (result.Stored.Count > 0)
                LinkAndUpdate(toLink.Where(name => result.Stored.Contains(name, StringComparer.OrdinalIgnoreCase)).Concat(alsoLink ?? new string[0]).ToList(), storeFile, paths, progress, token);
            return result;
        }

        // Update DB links exactly the vars in this list, without following dependencies.
        private static void LinkAndUpdate(List<string> toLink, string storeFile, LibraryPaths paths, Action<int, int> progress, CancellationToken token)
        {
            File.AppendAllLines(paths.MigratedList, toLink);
            using (LibraryStore store = LibraryStore.Open(storeFile))
                LibraryUpdate.Run(store, paths, progress, token);
        }

        /// <summary>A network folder has no drive to ask, so it counts as having room.</summary>
        public static long FreeSpace(string folder)
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))).AvailableFreeSpace;
            }
            catch (ArgumentException)
            {
                return long.MaxValue;
            }
        }

        public static string Size(long bytes)
        {
            return bytes >= 1L << 30 ? $"{bytes / 1073741824.0:F2} GB" : $"{bytes / 1048576.0:F1} MB";
        }
    }
}
