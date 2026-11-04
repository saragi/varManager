using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace varManager.Ui
{
    /// <summary>The packages picked in the Library, kept across reloads and profile changes.</summary>
    public sealed class PickList : Observable
    {
        private readonly List<string> names = new List<string>();

        public event EventHandler? PicksChanged;

        public IReadOnlyList<string> Names => names;
        public bool HasPicks => names.Count > 0;
        public string CountText => names.Count + " picked";

        public bool Contains(string name)
        {
            return names.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        public void Toggle(string name)
        {
            if (Contains(name))
                names.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            else
                names.Add(name);
            Tell();
        }

        public void Add(IEnumerable<string> more)
        {
            names.AddRange(more.Where(name => !Contains(name)).ToList());
            Tell();
        }

        public void Clear()
        {
            names.Clear();
            Tell();
        }

        private void Tell()
        {
            Changed(nameof(HasPicks));
            Changed(nameof(CountText));
            PicksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class ProfileTile
    {
        private const string NotMeasured = "Not measured";

        public ProfileTile(ProfileSummary profile, bool isActive)
        {
            Profile = profile;
            IsActive = isActive;
        }

        public ProfileSummary Profile { get; }
        public bool IsActive { get; }
        public string Name => Profile.Name;
        public bool IsInactive => !IsActive;
        public bool CanCheck => Profile.Kind != ProfileKind.Plain;
        public bool CanChange => Profile.Name != VarSwitches.Default;
        public string UseLabel => IsActive ? "In use" : "Switch to this profile";
        public string Startup => Profile.Timing == null ? NotMeasured : $"{Profile.Timing.StartupSeconds:F0} s";
        public string Ram => Profile.Timing == null ? NotMeasured : $"{Profile.Timing.RamBytes / 1073741824.0:F1} GB";

        // The plugin doesn't say when a scene has finished loading, so the app can't time it.
        public string SceneLoad => NotMeasured;

        public string CountText
        {
            get
            {
                string links = Profile.LinkCount == 1 ? "1 var linked" : $"{Profile.LinkCount:N0} vars linked";
                return Profile.Kind switch
                {
                    ProfileKind.Lean => Profile.PickCount == 1 ? links + " from 1 pick" : $"{links} from {Profile.PickCount:N0} picks",
                    ProfileKind.Exclude => $"{links}, leaving out {Profile.PickCount:N0}",
                    _ => links,
                };
            }
        }
    }

    /// <summary>Compares, switches, checks and builds profiles. Every change goes through the job bar.</summary>
    public sealed class ProfilesViewModel : Observable
    {
        private const string LeanLabel = "Keep only these";
        private const string ExcludeLabel = "Leave these out";

        private readonly LibraryPaths paths;
        private readonly string storeFile;
        private readonly JobRunner jobs;
        private readonly Func<string?> currentProfile;
        private readonly Action<string> switchTo;
        private readonly object planLock = new object();
        private IReadOnlyList<ProfileTile> cards = new ProfileTile[0];
        private IReadOnlyList<string> pickRows = new string[0];
        private ProfileBuilder? builder;
        private int builtFor;
        private int libraryVersion;
        private string plannedFor = "";
        private ProfilePlan? plan;
        private bool excluding;
        private string name = "lean";
        private string planText = "";
        private string previewText = "";
        private string nameProblem = "";
        private ProfileTile? renaming;
        private ProfileTile? deleting;
        private string newName = "";
        private ProfileCheck? check;
        private int reloads;
        private int previews;

        public ProfilesViewModel(string storeFile, LibraryPaths paths, JobRunner jobs, PickList picks,
            Func<string?> currentProfile, Action<string> switchTo, Action goToLibrary)
        {
            this.storeFile = storeFile;
            this.paths = paths;
            this.jobs = jobs;
            this.currentProfile = currentProfile;
            this.switchTo = switchTo;
            Picks = picks;
            picks.PicksChanged += OnPicksChanged;
            UseCommand = new ArgCommand<ProfileTile>(tile => switchTo(tile.Name));
            CheckCommand = new ArgCommand<ProfileTile>(tile => Work = Check(tile.Name));
            RenameCommand = new ArgCommand<ProfileTile>(StartRename);
            DeleteCommand = new ArgCommand<ProfileTile>(StartDelete);
            ConfirmRenameCommand = new Command(() => Work = Rename());
            ConfirmDeleteCommand = new Command(() => Work = Delete());
            CancelChangeCommand = new Command(EndChange);
            RepairCommand = new Command(() => Work = Repair());
            RestoreCommand = new Command(() => Work = Restore());
            CloseCheckCommand = new Command(() => CheckResult = null);
            SaveCommand = new Command(() => Work = Build());
            MakeEmptyCommand = new Command(MakeEmpty);
            RemovePickCommand = new ArgCommand<string>(picks.Toggle);
            GoToLibraryCommand = new Command(goToLibrary);
            ModePills = new[]
            {
                new Pill(LeanLabel, new Command(() => SetMode(false))) { IsActive = true },
                new Pill(ExcludeLabel, new Command(() => SetMode(true))),
            };
            PicksMoved();
        }

        public PickList Picks { get; }
        public IReadOnlyList<Pill> ModePills { get; }

        /// <summary>The last action from a button, including what follows its job.</summary>
        public Task Work { get; private set; } = Task.CompletedTask;

        public ICommand UseCommand { get; }
        public ICommand CheckCommand { get; }
        public ICommand RenameCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand ConfirmRenameCommand { get; }
        public ICommand ConfirmDeleteCommand { get; }
        public ICommand CancelChangeCommand { get; }
        public ICommand RepairCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand CloseCheckCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand MakeEmptyCommand { get; }
        public ICommand RemovePickCommand { get; }
        public ICommand GoToLibraryCommand { get; }

        public IReadOnlyList<ProfileTile> Cards
        {
            get => cards;
            private set
            {
                cards = value;
                Changed();
            }
        }

        public IReadOnlyList<string> PickRows
        {
            get => pickRows;
            private set
            {
                pickRows = value;
                Changed();
                Changed(nameof(HasPicks));
                Changed(nameof(NoPicks));
            }
        }

        public bool HasPicks => pickRows.Count > 0;
        public bool NoPicks => pickRows.Count == 0;

        public string Name
        {
            get => name;
            set
            {
                name = value;
                Changed();
                _ = Preview();
            }
        }

        public string PlanText
        {
            get => planText;
            private set
            {
                planText = value;
                Changed();
            }
        }

        public string PreviewText
        {
            get => previewText;
            private set
            {
                previewText = value;
                Changed();
            }
        }

        public string NameProblem
        {
            get => nameProblem;
            private set
            {
                nameProblem = value;
                Changed();
                Changed(nameof(CanSave));
                Changed(nameof(CanMakeEmpty));
            }
        }

        public bool CanSave => HasPicks && nameProblem.Length == 0;
        public bool CanMakeEmpty => NoPicks && nameProblem.Length == 0 && !IsTaken(name.Trim());

        public bool IsRenaming => renaming != null;
        public string RenameTitle => renaming == null ? "" : "Rename " + renaming.Name;

        public string NewName
        {
            get => newName;
            set
            {
                newName = value;
                Changed();
                Changed(nameof(CanRename));
            }
        }

        public bool CanRename => renaming != null && LeanProfiles.IsValidName(newName.Trim()) && !IsTaken(newName.Trim());

        public bool IsDeleting => deleting != null;
        public string DeleteText => deleting == null ? "" : $"Delete {deleting.Name}? This removes its links and saved picks. Your var files stay.";

        public bool HasCheck => check != null;
        public string CheckTitle => check == null ? "" : "Profile check: " + check.Name;
        public string CheckText => check?.Text ?? "";
        public bool CanRepair => check?.CanRepair ?? false;
        public bool CanRestore => check?.CanRestore ?? false;

        private ProfileCheck? CheckResult
        {
            get => check;
            set
            {
                check = value;
                Changed(nameof(HasCheck));
                Changed(nameof(CheckTitle));
                Changed(nameof(CheckText));
                Changed(nameof(CanRepair));
                Changed(nameof(CanRestore));
            }
        }

        public async Task Reload()
        {
            int reload = ++reloads;
            List<ProfileSummary> loaded = await Task.Run(() => ProfileCatalog.Load(paths.VamPath, paths.VarsPath)).ConfigureAwait(true);
            if (reload != reloads)
                return;
            string? current = currentProfile();
            Cards = loaded.Select(profile => new ProfileTile(profile, string.Equals(profile.Name, current, StringComparison.OrdinalIgnoreCase))).ToList();
            Changed(nameof(CanMakeEmpty));
            // Not a lock: a plan in progress holds it, and this runs on the screen's thread.
            Interlocked.Increment(ref libraryVersion);
            await Preview().ConfigureAwait(true);
        }

        /// <summary>Stops following the picks, once this page is replaced.</summary>
        public void Detach()
        {
            Picks.PicksChanged -= OnPicksChanged;
        }

        private void OnPicksChanged(object? sender, EventArgs e)
        {
            PicksMoved();
        }

        private bool IsTaken(string candidate)
        {
            return cards.Any(card => string.Equals(card.Name, candidate, StringComparison.OrdinalIgnoreCase));
        }

        private void PicksMoved()
        {
            PickRows = Picks.Names.ToList();
            _ = Preview();
        }

        private void SetMode(bool exclude)
        {
            excluding = exclude;
            foreach (Pill pill in ModePills)
                pill.IsActive = pill.Label == (exclude ? ExcludeLabel : LeanLabel);

            string? current = currentProfile();
            string file = current == null ? "" : LeanProfiles.ProfilePath(paths.VarsPath, current);
            LeanProfile? saved = exclude && File.Exists(file) ? LeanProfiles.Load(file) : null;
            if (saved != null && saved.Exclude.Count > 0)
            {
                Name = current!;
                Picks.Add(saved.Exclude);
                return;
            }
            Name = VarSwitches.UnusedName(cards.Select(card => card.Name), exclude ? "trimmed" : "lean");
        }

        private void CheckName()
        {
            string typed = name.Trim();
            NameProblem = LeanProfiles.IsValidName(typed) ? "" : "Use a name other than default, without characters Windows doesn't allow in file names.";
        }

        // Plans off the screen's thread. Typing a name only changes what is compared, so the plan is kept.
        private async Task Preview()
        {
            int preview = ++previews;
            CheckName();
            List<string> picked = Picks.Names.ToList();
            if (picked.Count == 0 || nameProblem.Length > 0 || !File.Exists(storeFile))
            {
                PlanText = "";
                PreviewText = "";
                return;
            }

            bool exclude = excluding;
            string typed = name.Trim();
            string summary;
            string details;
            try
            {
                (summary, details) = await Task.Run(() => Describe(picked, exclude, typed)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                summary = "Could not plan this profile: " + ex.Message;
                details = "";
            }
            if (preview != previews)
                return;
            PlanText = summary;
            PreviewText = details;
        }

        private (string Summary, string Details) Describe(List<string> picked, bool exclude, string profileName)
        {
            int version = Volatile.Read(ref libraryVersion);
            lock (planLock)
            {
                if (builder == null || builtFor != version)
                {
                    using LibraryStore store = LibraryStore.Open(storeFile);
                    builder = new ProfileBuilder(store, paths);
                    builtFor = version;
                    plannedFor = "";
                }
                string key = exclude + "\n" + string.Join("\n", picked);
                if (key != plannedFor || plan == null)
                {
                    plan = builder.Plan(exclude ? builder.Excluding(picked) : builder.Lean(picked));
                    plannedFor = key;
                }

                string vars = plan.Closure.Vars.Count == 1 ? "1 var" : $"{plan.Closure.Vars.Count:N0} vars";
                string size = $"{plan.Bytes / 1073741824.0:F2} GB";
                string picks = picked.Count == 1 ? "1 pick" : $"{picked.Count} picks";
                string summary = exclude
                    ? $"Links {vars} from default, {size}, leaving out {picks}."
                    : $"{picks} plus dependencies and the base set link {vars}, {size}.";
                if (IsTaken(profileName))
                    summary += $" This replaces the links in {profileName}.";
                return (summary, builder.Preview(profileName, plan));
            }
        }

        // What the old app's Add did: a profile with no links, to fill by hand.
        private void MakeEmpty()
        {
            if (!CanMakeEmpty || jobs.IsRunning)
                return;
            string typed = name.Trim();
            try
            {
                Directory.CreateDirectory(VarSwitches.LinkFolder(paths.VamPath, typed));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                jobs.Say("Could not make the profile: " + ex.Message);
                return;
            }
            switchTo(typed);
        }

        /// <summary>Saves the vars this profile links to a text file.</summary>
        public void ExportLinked(string file)
        {
            jobs.Start("Saving the list", context =>
            {
                int count = ProfileCatalog.ExportLinked(paths.AddonPackages, file);
                return count == 1 ? "Saved 1 var to the list." : $"Saved {count:N0} vars to the list.";
            });
        }

        /// <summary>Turns on the vars a text file names, with their dependencies.</summary>
        public void TurnOnFromList(string file)
        {
            if (NoStore())
                return;
            jobs.Start("Turning on from a list", context =>
            {
                using LibraryStore store = LibraryStore.Open(storeFile);
                ListResult result = ProfileCatalog.LinkFromList(store, paths, file);
                VamBridge.RequestRescan(paths.VamPath);
                string text = result.Linked == 1 ? "Turned on 1 var." : $"Turned on {result.Linked:N0} vars.";
                if (result.Failed > 0)
                    text += $" {result.Failed:N0} could not be linked.";
                if (result.Unknown > 0)
                    text += result.Unknown == 1 ? " 1 name isn't in the library." : $" {result.Unknown:N0} names aren't in the library.";
                return text;
            });
        }

        private async Task Build()
        {
            if (!CanSave || NoStore())
                return;
            string typed = name.Trim();
            List<string> picked = Picks.Names.ToList();
            bool exclude = excluding;
            string? built = null;
            Task? job = jobs.Start("Building profile", context =>
            {
                using LibraryStore store = LibraryStore.Open(storeFile);
                var maker = new ProfileBuilder(store, paths);
                LeanProfile profile = exclude ? maker.Excluding(picked) : maker.Lean(picked);
                built = maker.Apply(typed, maker.Plan(profile), profile);
                return $"Built {built}.";
            });
            if (job == null)
                return;
            await job.ConfigureAwait(true);
            if (built == null)
                return;
            Picks.Clear();
            Name = VarSwitches.UnusedName(cards.Select(card => card.Name).Append(built), excluding ? "trimmed" : "lean");
            switchTo(built);
        }

        // Opening a store creates it, and an empty one stops the Access import from ever running.
        private bool NoStore()
        {
            if (File.Exists(storeFile))
                return false;
            jobs.Say("Run Update DB first.");
            return true;
        }

        private async Task Check(string profile)
        {
            if (NoStore())
                return;
            ProfileCheck? result = null;
            Task? job = jobs.Start("Checking " + profile, context =>
            {
                using LibraryStore store = LibraryStore.Open(storeFile);
                result = new ProfileBuilder(store, paths).Check(profile);
                return "Checked " + profile + ".";
            });
            if (job == null)
                return;
            await job.ConfigureAwait(true);
            if (result != null)
                CheckResult = result;
        }

        private Task Repair()
        {
            return Change("Repairing", (maker, profile) => maker.Repair(profile));
        }

        private Task Restore()
        {
            return Change("Restoring", (maker, profile) => maker.Restore(profile));
        }

        private async Task Change(string verb, Action<ProfileBuilder, string> change)
        {
            if (check == null)
                return;
            string profile = check.Name;
            bool done = false;
            Task? job = jobs.Start(verb + " " + profile, context =>
            {
                using LibraryStore store = LibraryStore.Open(storeFile);
                change(new ProfileBuilder(store, paths), profile);
                done = true;
                return $"{verb} {profile} finished.";
            });
            if (job == null)
                return;
            await job.ConfigureAwait(true);
            if (!done)
                return;
            CheckResult = null;
            if (string.Equals(profile, currentProfile(), StringComparison.OrdinalIgnoreCase))
                switchTo(profile);
        }

        private void StartRename(ProfileTile tile)
        {
            if (!tile.CanChange)
                return;
            deleting = null;
            renaming = tile;
            NewName = tile.Name;
            TellChange();
        }

        private void StartDelete(ProfileTile tile)
        {
            if (!tile.CanChange)
                return;
            renaming = null;
            deleting = tile;
            TellChange();
        }

        private void EndChange()
        {
            renaming = null;
            deleting = null;
            TellChange();
        }

        private void TellChange()
        {
            Changed(nameof(IsRenaming));
            Changed(nameof(RenameTitle));
            Changed(nameof(CanRename));
            Changed(nameof(IsDeleting));
            Changed(nameof(DeleteText));
        }

        private async Task Rename()
        {
            if (renaming == null || !CanRename || !KnowsCurrent())
                return;
            string oldName = renaming.Name;
            string typed = newName.Trim();
            string current = currentProfile()!;
            bool wasCurrent = string.Equals(oldName, current, StringComparison.OrdinalIgnoreCase);
            EndChange();
            bool moved = false;
            Task? job = jobs.Start("Renaming " + oldName, context =>
            {
                try
                {
                    ProfileCatalog.Rename(paths.VamPath, paths.VarsPath, oldName, typed);
                }
                finally
                {
                    // The folder moves first, so even a failure after that must follow it. This happens
                    // inside the job, because the reload when a job ends mustn't meet a link to nothing.
                    moved = VarSwitches.Names(paths.VamPath).Contains(typed, StringComparer.OrdinalIgnoreCase);
                    if (moved && wasCurrent)
                        VarSwitches.Point(paths.VamPath, typed, out _);
                }
                return $"Renamed {oldName} to {typed}.";
            });
            if (job == null)
                return;
            await job.ConfigureAwait(true);
            switchTo(moved && wasCurrent ? typed : current);
        }

        // After a failed switch the app can't tell what AddonPackages points at, so changing a profile isn't safe.
        private bool KnowsCurrent()
        {
            if (currentProfile() != null)
                return true;
            jobs.Say("Choose a profile first.");
            return false;
        }

        private async Task Delete()
        {
            if (deleting == null || jobs.IsRunning || !KnowsCurrent())
                return;
            string profile = deleting.Name;
            EndChange();
            if (string.Equals(profile, currentProfile(), StringComparison.OrdinalIgnoreCase))
            {
                switchTo(VarSwitches.Default);
                if (!string.Equals(currentProfile(), VarSwitches.Default, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            bool deleted = false;
            Task? job = jobs.Start("Deleting " + profile, context =>
            {
                int kept = ProfileCatalog.Delete(paths.VamPath, paths.VarsPath, profile);
                deleted = true;
                return kept == 0 ? "Deleted " + profile + "." : $"Deleted the links in {profile}. Its folder stays, because it holds {kept} real files.";
            });
            if (job == null)
                return;
            await job.ConfigureAwait(true);
            if (deleted)
                switchTo(currentProfile() ?? VarSwitches.Default);
        }
    }
}
