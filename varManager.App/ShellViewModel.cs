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

    public sealed class NavPage
    {
        public NavPage(string name, string icon)
        {
            Name = name;
            Icon = icon;
        }

        public string Name { get; }
        public string Icon { get; }
    }

    /// <summary>The parts of the window that are always there: profile, buttons, side nav and job bar.</summary>
    public sealed class ShellViewModel : INotifyPropertyChanged
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

        public ShellViewModel(string settingsFile, JobRunner jobs)
            : this(settingsFile, jobs, VamBridge.IsRunning, StartVam)
        {
        }

        public ShellViewModel(string settingsFile, JobRunner jobs, Func<bool> vamRunning, Action<string> startVam)
        {
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
            CancelCommand = new Command(jobs.Cancel);
            SaveSetupCommand = new Command(SaveSetup);
            Open();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

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

        public ICommand LaunchCommand { get; }
        public ICommand UpdateCommand { get; }
        public ICommand GoToMissingCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand SaveSetupCommand { get; }

        public NavPage SelectedPage
        {
            get => selectedPage;
            set
            {
                Set(ref selectedPage, value);
                Changed(nameof(IsLibraryPage));
                Changed(nameof(IsScenesPage));
                Changed(nameof(IsOtherPage));
            }
        }

        public bool IsLibraryPage => selectedPage == Pages[0];
        public bool IsScenesPage => selectedPage == Pages[1];
        public bool IsOtherPage => !IsLibraryPage && !IsScenesPage;

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
            _ = VamBridge.RemoveTempLinksAfterLoad(VamPath, fileNames, TimeSpan.FromSeconds(20));
        }

        private async Task ReadLibrary(LibraryViewModel reading)
        {
            try
            {
                await Task.WhenAll(reading.Reload(), Scenes?.Reload() ?? Task.CompletedTask).ConfigureAwait(true);
                // After a failed switch nothing is linked, and a read that started earlier mustn't say otherwise.
                if (selectedSwitch != null)
                    LinkedText = reading.LinkedCount == 1 ? "1 package linked" : $"{reading.LinkedCount:N0} packages linked";
            }
            catch (Exception ex)
            {
                if (selectedSwitch != null)
                    Jobs.Say("Could not read the library: " + ex.Message);
            }
        }

        // Without this, a failed import would leave Update DB to create an empty store, and the import would never run again.
        private void ImportOldDatabase()
        {
            importFailed = false;
            string mdb = Path.Combine(dataFolder, "varManager.mdb");
            if (!File.Exists(mdb) || File.Exists(StoreFile))
                return;
            Jobs.Start("Importing library", context =>
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
        }

        private void UpdateDb()
        {
            if (importFailed)
            {
                Jobs.Say("The old database wasn't imported, so Update DB is off. Restart to try the import again.");
                return;
            }
            var paths = new LibraryPaths(VarsPath, VamPath, dataFolder);
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
            Library = new LibraryViewModel(StoreFile, paths, Jobs, () => VarLinks.Installed(packs).Keys, ForgetTempLinks, SynchronizationContext.Current);
            Scenes = new ScenesViewModel(StoreFile, paths, Jobs, () => VarLinks.Installed(packs).Keys,
                () => SelectedPage = Pages[^1], ForgetTempLinks, SynchronizationContext.Current);
            ImportOldDatabase();
            if (!Jobs.IsRunning)
                ReloadLibrary();
        }

        private void SaveSetup()
        {
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
            }
            catch (Win32Exception ex)
            {
                Jobs.Say("Could not start VaM: " + ex.Message);
            }
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
