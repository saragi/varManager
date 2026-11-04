using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace varManager.Ui
{
    public sealed class Command : ICommand
    {
        private readonly Action run;

        public Command(Action run)
        {
            this.run = run;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run();
    }

    public sealed class NavPage : Observable
    {
        private string badge = "";

        public NavPage(string name, string icon)
        {
            Name = name;
            Icon = icon;
        }

        public string Name { get; }
        public string Icon { get; }

        public string Badge
        {
            get => badge;
            set
            {
                badge = value;
                Changed();
            }
        }
    }

    /// <summary>The parts of the window that are always there: profile, buttons, side nav and job bar.</summary>
    public sealed class ShellViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly string settingsFile;
        private readonly Func<bool> vamRunning;
        private readonly Action<string> startVam;
        private readonly string dataFolder;
        private AppSettings settings;
        private bool importFailed;
        private NavPage selectedPage;
        private string? selectedSwitch;
        private string setupProblem = "";
        private string linkedText = "";
        private bool needsSetup;
        private LibraryViewModel? library;
        private ScenesViewModel? scenes;
        private ProfilesViewModel? profiles;
        private HealthViewModel? health;
        private MissingViewModel? missing;
        private HubViewModel? hubPage;
        private bool browsingHub;
        private bool canCancelSetup;
        private readonly IHubBrowser browser;
        private RepositoryWatch? watch;
        private readonly IHubSource hub;
        private readonly TimeSpan watchWait;
        private readonly TempLinkRemoval tempLinks = new TempLinkRemoval();
        private readonly Func<long> vamMemory;
        private CancellationTokenSource? launchTimer;

        public ShellViewModel(string settingsFile, JobRunner jobs)
            : this(settingsFile, jobs, VamBridge.IsRunning, StartVam)
        {
        }

        public ShellViewModel(string settingsFile, JobRunner jobs, Func<bool> vamRunning, Action<string> startVam, Func<long>? vamMemory = null,
            IHubSource? hub = null, TimeSpan? watchWait = null, IHubBrowser? browser = null)
        {
            this.browser = browser ?? new RealHubBrowser();
            this.vamMemory = vamMemory ?? StartupProbe.VamMemory;
            this.hub = hub ?? new RealHub();
            this.watchWait = watchWait ?? TimeSpan.FromSeconds(5);
            this.settingsFile = settingsFile;
            this.vamRunning = vamRunning;
            this.startVam = startVam;
            Jobs = jobs;
            dataFolder = Path.GetDirectoryName(settingsFile) ?? AppContext.BaseDirectory;
            jobs.PropertyChanged += JobChanged;
            settings = AppSettings.Load(settingsFile);
            selectedPage = Pages[0];
            LaunchCommand = new Command(Launch);
            UpdateCommand = new Command(UpdateDb);
            GoToMissingCommand = new Command(() => SelectedPage = Pages[^1]);
            BuildProfileCommand = new Command(() => SelectedPage = Pages[2]);
            BrowseHubCommand = new Command(() => BrowseHub(true));
            LeaveHubCommand = new Command(() => BrowseHub(false));
            CancelCommand = new Command(jobs.Cancel);
            SaveSetupCommand = new Command(SaveSetup);
            OpenSettingsCommand = new Command(OpenSettings);
            CancelSetupCommand = new Command(CancelSetup);
            Open();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Dispose()
        {
            watch?.Dispose();
        }

        public JobRunner Jobs { get; }

        public ObservableCollection<NavPage> Pages { get; } = new ObservableCollection<NavPage>
        {
            new NavPage("Library", "M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h6v6h-6z"),
            new NavPage("Scenes", "M3 5h18v14H3z M10 9l5 3-5 3z"),
            new NavPage("Profiles", "M4 7h16 M4 12h16 M4 17h10"),
            new NavPage("Health", "M3 12h4l3-7 4 14 3-7h4"),
            new NavPage("Missing", "M12 4v11 M7 11l5 5 5-5 M5 20h14"),
        };

        public ObservableCollection<string> Switches { get; } = new ObservableCollection<string>();

        public PickList Picks { get; } = new PickList();

        public ICommand LaunchCommand { get; }
        public ICommand UpdateCommand { get; }
        public ICommand GoToMissingCommand { get; }
        public ICommand BuildProfileCommand { get; }
        public ICommand BrowseHubCommand { get; }
        public ICommand LeaveHubCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand SaveSetupCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand CancelSetupCommand { get; }

        public NavPage SelectedPage
        {
            get => selectedPage;
            set
            {
                // Missing always opens on its list, so a later visit isn't left on the Hub.
                browsingHub = false;
                Set(ref selectedPage, value);
                Changed(nameof(IsLibraryPage));
                Changed(nameof(IsScenesPage));
                Changed(nameof(IsProfilesPage));
                Changed(nameof(IsHealthPage));
                Changed(nameof(IsMissingPage));
                Changed(nameof(IsMissingList));
                Changed(nameof(IsHubPage));
                if (IsHealthPage)
                    _ = ReadHealth();
                if (IsMissingList)
                    _ = CheckMissing();
                if (IsHubPage)
                    _ = Hub?.Open();
            }
        }

        public bool IsLibraryPage => selectedPage == Pages[0];
        public bool IsScenesPage => selectedPage == Pages[1];
        public bool IsProfilesPage => selectedPage == Pages[2];
        public bool IsHealthPage => selectedPage == Pages[3];
        public bool IsMissingPage => selectedPage == Pages[4];
        public bool IsMissingList => IsMissingPage && !browsingHub;
        public bool IsHubPage => IsMissingPage && browsingHub;

        public LibraryViewModel? Library
        {
            get => library;
            private set => Set(ref library, value);
        }

        public ScenesViewModel? Scenes
        {
            get => scenes;
            private set => Set(ref scenes, value);
        }

        public ProfilesViewModel? Profiles
        {
            get => profiles;
            private set => Set(ref profiles, value);
        }

        public HealthViewModel? Health
        {
            get => health;
            private set => Set(ref health, value);
        }

        public MissingViewModel? Missing
        {
            get => missing;
            private set => Set(ref missing, value);
        }

        public HubViewModel? Hub
        {
            get => hubPage;
            private set => Set(ref hubPage, value);
        }

        /// <summary>The latest library read, which also brings the linked count up to date.</summary>
        public Task LibraryLoaded { get; private set; } = Task.CompletedTask;

        public bool NeedsSetup
        {
            get => needsSetup;
            private set
            {
                Set(ref needsSetup, value);
                Changed(nameof(IsReady));
            }
        }

        public bool IsReady => !needsSetup;

        public bool CanCancelSetup
        {
            get => canCancelSetup;
            private set => Set(ref canCancelSetup, value);
        }

        public string SetupProblem
        {
            get => setupProblem;
            private set => Set(ref setupProblem, value);
        }

        public string VamPath
        {
            get => settings.VamPath;
            set
            {
                settings.VamPath = value;
                Changed();
            }
        }

        public string VarsPath
        {
            get => settings.VarsPath;
            set
            {
                settings.VarsPath = value;
                Changed();
            }
        }

        public string LinkedText
        {
            get => linkedText;
            private set => Set(ref linkedText, value);
        }

        public string? SelectedSwitch
        {
            get => selectedSwitch;
            set
            {
                if (value == null || value == selectedSwitch)
                    return;
                if (Jobs.IsRunning)
                {
                    Jobs.Say("Wait for the current job to finish before switching.");
                    RevertSwitchBox();
                    return;
                }
                string? failure = PointAt(value);
                if (failure != null)
                {
                    Jobs.Say($"Could not switch to {value}. Choose a profile to try again. ({failure})");
                    selectedSwitch = null;
                    LinkedText = "Nothing linked";
                    RevertSwitchBox();
                    return;
                }
                selectedSwitch = value;
                Changed();
                ReloadLibrary();
                VamBridge.RequestRescan(VamPath);
                Jobs.Say("Switched to " + value + ".");
            }
        }

        private string StoreFile => Path.Combine(dataFolder, "library.db");

        private string? PointAt(string name)
        {
            try
            {
                int errorCode;
                return VarSwitches.Point(VamPath, name, out errorCode) == SwitchChange.Failed ? "Error " + errorCode : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        private void JobChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(JobRunner.IsRunning) && !Jobs.IsRunning && IsReady && selectedSwitch != null)
                ReloadLibrary();
        }

        private void ReloadLibrary()
        {
            if (Library != null)
                LibraryLoaded = ReadLibrary(Library);
        }

        private void ForgetTempLinks(IReadOnlyList<string> fileNames)
        {
            _ = tempLinks.Schedule(VamPath, fileNames, TimeSpan.FromSeconds(20));
        }

        private async Task ReadLibrary(LibraryViewModel reading)
        {
            Task scenesRead = ReadScenes();
            Task profilesRead = ReadProfiles();
            // Health reads every save, so it runs only while someone is looking at it.
            Task healthRead = IsHealthPage ? ReadHealth() : Task.CompletedTask;
            Task missingRead = ReadMissing();
            try
            {
                await reading.Reload().ConfigureAwait(true);
                // After a failed switch nothing is linked, and a read that started earlier mustn't say otherwise.
                if (selectedSwitch != null)
                    LinkedText = reading.LinkedCount == 1 ? "1 package linked" : $"{reading.LinkedCount:N0} packages linked";
            }
            catch (Exception ex)
            {
                if (selectedSwitch != null)
                    Jobs.Say("Could not read the library: " + ex.Message);
            }
            await scenesRead.ConfigureAwait(true);
            await profilesRead.ConfigureAwait(true);
            await healthRead.ConfigureAwait(true);
            await missingRead.ConfigureAwait(true);
        }

        private async Task ReadMissing()
        {
            if (Missing == null)
                return;
            try
            {
                await Missing.Count().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Jobs.Say("Could not check what's missing: " + ex.Message);
            }
            if (IsMissingList)
                await CheckMissing().ConfigureAwait(true);
            if (IsHubPage && Hub != null)
                await Hub.Refresh().ConfigureAwait(true);
        }

        private void BrowseHub(bool browse)
        {
            browsingHub = browse;
            Changed(nameof(IsMissingList));
            Changed(nameof(IsHubPage));
            if (browse)
                _ = Hub?.Open();
            else
                _ = CheckMissing();
        }

        private async Task CheckMissing()
        {
            if (Missing == null)
                return;
            try
            {
                await Missing.Check().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Jobs.Say("Could not check what's missing: " + ex.Message);
            }
        }

        private void ShowMissingCount(int count)
        {
            Pages[^1].Badge = count > 0 ? count.ToString("N0") : "";
        }

        private async Task ReadHealth()
        {
            if (Health == null)
                return;
            try
            {
                await Health.Reload().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Jobs.Say("Could not check the library: " + ex.Message);
            }
        }

        private async Task ReadProfiles()
        {
            if (Profiles == null)
                return;
            try
            {
                await Profiles.Reload().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (selectedSwitch != null)
                    Jobs.Say("Could not read the profiles: " + ex.Message);
            }
        }

        // Brings the list up to date without emptying it, because the box would lose its selection.
        private void SwitchTo(string name)
        {
            List<string> names = VarSwitches.Names(VamPath);
            foreach (string gone in Switches.Except(names, StringComparer.OrdinalIgnoreCase).ToList())
                Switches.Remove(gone);
            foreach (string added in names.Except(Switches, StringComparer.OrdinalIgnoreCase))
                Switches.Add(added);

            if (string.Equals(name, selectedSwitch, StringComparison.OrdinalIgnoreCase))
            {
                ReloadLibrary();
                VamBridge.RequestRescan(VamPath);
                return;
            }
            SelectedSwitch = name;
        }

        private async Task MeasureLaunch(string? profile)
        {
            if (profile == null)
                return;
            string vars = VarsPath;
            var timer = new CancellationTokenSource();
            Interlocked.Exchange(ref launchTimer, timer)?.Cancel();
            try
            {
                StartupReading? reading = await StartupProbe.Measure(VamPath, vamMemory, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), timer.Token).ConfigureAwait(true);
                if (reading == null)
                    return;
                ProfileTimings.Record(vars, profile, reading.Startup, reading.RamBytes);
                await ReadProfiles().ConfigureAwait(true);
            }
            catch (Exception)
            {
                // A timing is only a nicety, so it never gets in the way of VaM.
            }
        }

        private async Task ReadScenes()
        {
            if (Scenes == null)
                return;
            try
            {
                await Scenes.Reload().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (selectedSwitch != null)
                    Jobs.Say("Could not read the scenes: " + ex.Message);
            }
        }

        // Without this, a failed import would leave Update DB to create an empty store, and the import would never run again.
        private void ImportOldDatabase()
        {
            importFailed = false;
            string mdb = Path.Combine(dataFolder, "varManager.mdb");
            if (!File.Exists(mdb) || File.Exists(StoreFile))
                return;
            Task? started = Jobs.Start("Importing library", context =>
            {
                try
                {
                    AccessDatabase.ImportOnce(mdb, StoreFile);
                }
                catch (Exception)
                {
                    importFailed = true;
                    throw;
                }
                return "Library imported.";
            });
            // Another job kept it from starting, and Update DB mustn't make an empty store meanwhile.
            if (started == null)
                importFailed = true;
        }

        private void UpdateDb()
        {
            UpdateDb(new LibraryPaths(VarsPath, VamPath, dataFolder));
        }

        private void UpdateDb(LibraryPaths paths)
        {
            if (importFailed)
            {
                Jobs.Say("The old database wasn't imported, so Update DB is off. Restart to try the import again.");
                return;
            }
            Jobs.Start("Update DB", context =>
            {
                using LibraryStore store = LibraryStore.Open(StoreFile);
                return LibraryUpdate.Run(store, paths, context.Report, context.Token).Summary;
            });
        }

        // A ComboBox ignores a change raised while it is still setting the value, so this waits until it has finished.
        private void RevertSwitchBox()
        {
            SynchronizationContext? context = SynchronizationContext.Current;
            if (context == null)
                Changed(nameof(SelectedSwitch));
            else
                context.Post(_ => Changed(nameof(SelectedSwitch)), null);
        }

        private void Open()
        {
            watch?.Dispose();
            watch = null;
            CanCancelSetup = false;
            string? problem = settings.Problem();
            SetupProblem = problem ?? "";
            NeedsSetup = problem != null;
            if (problem != null)
                return;

            Switches.Clear();
            foreach (string name in VarSwitches.Names(VamPath))
                Switches.Add(name);
            selectedSwitch = VarSwitches.EnsureLink(VamPath);
            Changed(nameof(SelectedSwitch));
            string packs = VarSwitches.AddonPackages(VamPath);
            var paths = new LibraryPaths(VarsPath, VamPath, dataFolder);
            Library?.Detach();
            Profiles?.Detach();
            Library = new LibraryViewModel(StoreFile, paths, Jobs, () => VarLinks.ReadInstalled(packs).Keys, ForgetTempLinks, SynchronizationContext.Current, Picks);
            Scenes = new ScenesViewModel(StoreFile, paths, Jobs, () => VarLinks.ReadInstalled(packs).Keys,
                () => SelectedPage = Pages[^1], ForgetTempLinks, SynchronizationContext.Current);
            Profiles = new ProfilesViewModel(StoreFile, paths, Jobs, Picks, () => selectedSwitch, SwitchTo, () => SelectedPage = Pages[0]);
            Health = new HealthViewModel(StoreFile, paths, Jobs, () => SelectedPage = Pages[^1]);
            Missing = new MissingViewModel(StoreFile, paths, Jobs, hub, text => System.Windows.Clipboard.SetText(text), ShowMissingCount);
            Hub = new HubViewModel(StoreFile, paths, Jobs, browser, hub, OpenUrl, ShowInLibrary, SynchronizationContext.Current);
            Watch(paths);
            ImportOldDatabase();
            if (!Jobs.IsRunning)
                ReloadLibrary();
        }

        // Editing writes straight into the settings, so leaving reads them back from the file.
        private void OpenSettings()
        {
            if (Jobs.IsRunning)
            {
                Jobs.Say("Wait for the current job to finish before changing settings.");
                return;
            }
            // A scan started now would run under folders about to change.
            watch?.Dispose();
            watch = null;
            CanCancelSetup = true;
            SetupProblem = "";
            NeedsSetup = true;
        }

        private void CancelSetup()
        {
            settings = AppSettings.Load(settingsFile);
            Changed(nameof(VamPath));
            Changed(nameof(VarsPath));
            // Checks the folders again, because one can go while this panel is open.
            Open();
        }

        private void SaveSetup()
        {
            if (Jobs.IsRunning)
            {
                SetupProblem = "Wait for the current job to finish.";
                return;
            }
            string? problem = settings.Problem();
            SetupProblem = problem ?? "";
            if (problem != null)
                return;
            try
            {
                settings.Save(settingsFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetupProblem = "Could not save settings: " + ex.Message;
                return;
            }
            Open();
        }

        private void Launch()
        {
            if (vamRunning())
            {
                Jobs.Say("VaM is already running.");
                return;
            }
            try
            {
                startVam(VamPath);
                Jobs.Say("VaM is starting.");
                _ = MeasureLaunch(selectedSwitch);
            }
            catch (Win32Exception ex)
            {
                Jobs.Say("Could not start VaM: " + ex.Message);
            }
        }

        // Scans the folders given here, not ones typed into setup and not saved.
        private void Watch(LibraryPaths paths)
        {
            watch = new RepositoryWatch(paths.VarsPath, () => Jobs.IsRunning, () => UpdateDb(paths), SynchronizationContext.Current, watchWait);
        }

        private void ShowInLibrary(string package)
        {
            SelectedPage = Pages[0];
            if (Library != null)
                Library.Query = package;
        }

        private static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private static void StartVam(string vamPath)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(vamPath, "VaM.exe"), "-vrmode None")
            {
                WorkingDirectory = vamPath,
                UseShellExecute = false,
            });
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string property = "")
        {
            if (Equals(field, value))
                return;
            field = value;
            Changed(property);
        }

        private void Changed([CallerMemberName] string property = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
