using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace varManager
{
    /// <summary>What the Missing screen needs from the Hub, so tests can stand in for it.</summary>
    public interface IHubSource
    {
        Task<List<HubPackage>> Find(IEnumerable<string> references);
        Task<string> PackageList();
        Task<DownloadResult> Download(IReadOnlyList<HubPackage> packages, string folder, Action<HubPackage> finished, CancellationToken token);
    }

    public sealed class RealHub : IHubSource
    {
        public Task<List<HubPackage>> Find(IEnumerable<string> references)
        {
            return HubClient.FindAll(references);
        }

        public Task<string> PackageList()
        {
            return HubClient.GetPackageList();
        }

        public async Task<DownloadResult> Download(IReadOnlyList<HubPackage> packages, string folder, Action<HubPackage> finished, CancellationToken token)
        {
            using (var downloader = new HubDownloader())
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

        public MissingLibrary(LibraryStore store, LibraryPaths paths)
        {
            IReadOnlyList<VarRecord> records = store.LoadVars();
            vars = new VarIndex(records.Select(v => new VarEntry(v.VarName, v.CreatorName, v.PackageName, v.Version ?? 0)));
            dependencies = new DependencyIndex(store.LoadDependencies());
            names = records.Select(v => v.VarName).ToList();
            linked = VarLinks.ReadInstalled(paths.AddonPackages).Keys.ToList();
        }

        public IReadOnlyList<string> ScopeVars(bool profileOnly)
        {
            return profileOnly ? linked : names;
        }

        public MissingReport Plan(bool profileOnly)
        {
            return MissingPlanner.Plan(ScopeVars(profileOnly), name => dependencies.DependenciesOf(name), vars, linked);
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
            var wanted = new HashSet<string>(Sources.OnHub.Where(item => item.NeededByLinkedVar).Select(item => item.Package), StringComparer.OrdinalIgnoreCase);
            return Offered.Where(package => wanted.Contains(MissingPlanner.PackageOf(package.Name))).Select(package => package.Name).ToList();
        }
    }

    public static class MissingCheck
    {
        // Left free on the drive, so a download can't fill it.
        private const long ReservedBytes = 1L << 30;

        /// <summary>Asks the Hub about what the library lacks. A Hub that can't be reached isn't an error here.</summary>
        public static async Task<MissingState> Run(MissingLibrary library, bool profileOnly, IHubSource hub, ISet<string> listed)
        {
            var view = new MissingState { Report = library.Plan(profileOnly) };
            view.Sources.NotFound.AddRange(view.Report.Needed);
            try
            {
                // Asked directly, because the Hub's package list doesn't hold every package.
                view.Offered = await hub.Find(view.Report.Needed.Select(item => item.Package + ".latest")).ConfigureAwait(false);
                string packageList = await hub.PackageList().ConfigureAwait(false);
                Dictionary<string, int> versions = HubParser.LatestVersions(packageList);
                view.Updates = (await hub.Find(MissingPlanner.UpdateRequests(library.ScopeVars(profileOnly), versions)).ConfigureAwait(false))
                    .Where(package => !library.Owns(package.Name)).ToList();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                view.HubProblem = "The Hub couldn't be reached: " + ex.Message;
                return view;
            }
            view.Sources = MissingPlanner.Sort(view.Report.Needed, view.Offered, listed);
            return view;
        }

        /// <summary>Package names from a local SharpVaMTools install, if there is one. Names only.</summary>
        public static HashSet<string> ListedPackages(string vamPath)
        {
            string install = SharpVaMToolsData.FindBeside(vamPath);
            if (install == null)
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                return SharpVaMToolsData.Packages(install);
            }
            catch (IOException)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>Downloads what's needed, and keeps going until the downloads need nothing more.</summary>
        public static string GetAll(string storeFile, LibraryPaths paths, bool profileOnly, IHubSource hub, ISet<string> listed,
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
                MissingState view = Check(storeFile, paths, profileOnly, hub, listed);
                if (view.HubProblem != null)
                    return view.HubProblem;

                List<HubPackage> next = view.Offered.Where(view.IsNeeded).Where(package => !attempted.Contains(package.Name)).ToList();
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
        public static string GetUpdates(string storeFile, LibraryPaths paths, bool profileOnly, IHubSource hub, ISet<string> listed,
            Func<string, long> freeSpace, Action<int, int> progress, CancellationToken token)
        {
            MissingState view = Check(storeFile, paths, profileOnly, hub, listed);
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

        /// <summary>Turns on stored vars that linked vars need.</summary>
        public static string TurnOnAll(string storeFile, LibraryPaths paths, bool profileOnly, Action<int, int> progress, CancellationToken token)
        {
            List<string> off;
            using (LibraryStore store = LibraryStore.Open(storeFile))
                off = new MissingLibrary(store, paths).Plan(profileOnly).TurnedOff.Keys.ToList();
            if (off.Count == 0)
                return "Nothing to turn on.";
            LinkAndUpdate(off, storeFile, paths, progress, token);
            return off.Count == 1 ? "Turned on 1 var." : $"Turned on {off.Count} vars.";
        }

        private static MissingState Check(string storeFile, LibraryPaths paths, bool profileOnly, IHubSource hub, ISet<string> listed)
        {
            MissingLibrary library;
            using (LibraryStore store = LibraryStore.Open(storeFile))
                library = new MissingLibrary(store, paths);
            return Run(library, profileOnly, hub, listed).GetAwaiter().GetResult();
        }

        private static string NoRoom(IReadOnlyCollection<HubPackage> packages, string folder, Func<string, long> freeSpace)
        {
            long needed = packages.Sum(package => package.Size);
            long free = freeSpace(folder);
            return needed + ReservedBytes > free ? $"This needs {Size(needed)}, but only {Size(free)} is free." : null;
        }

        private static DownloadResult Store(IReadOnlyList<HubPackage> packages, List<string> toLink, string storeFile, LibraryPaths paths,
            IHubSource hub, Action<int, int> progress, CancellationToken token)
        {
            int done = 0;
            DownloadResult result = hub.Download(packages, paths.VarsPath,
                package => progress(Interlocked.Increment(ref done), packages.Count), token).GetAwaiter().GetResult();
            if (result.Stored.Count > 0)
                LinkAndUpdate(toLink.Where(name => result.Stored.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList(), storeFile, paths, progress, token);
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
