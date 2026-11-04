using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
        private AppSettings settings;
        private NavPage selectedPage;
        private string? selectedSwitch;
        private string setupProblem = "";
        private string linkedText = "";
        private bool needsSetup;

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
            settings = AppSettings.Load(settingsFile);
            selectedPage = Pages[0];
            LaunchCommand = new Command(Launch);
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
        public ICommand CancelCommand { get; }
        public ICommand SaveSetupCommand { get; }

        public NavPage SelectedPage
        {
            get => selectedPage;
            set => Set(ref selectedPage, value);
        }

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
                    Changed();
                    return;
                }
                int errorCode;
                if (VarSwitches.Point(VamPath, value, out errorCode) == SwitchChange.Failed)
                {
                    Jobs.Say($"Could not switch to {value}. AddonPackages has no link now, so choose a profile to try again. (Error {errorCode})");
                    selectedSwitch = null;
                    Changed();
                    return;
                }
                selectedSwitch = value;
                Changed();
                RefreshLinked();
                VamBridge.RequestRescan(VamPath);
                Jobs.Say("Switched to " + value + ".");
            }
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
            RefreshLinked();
        }

        private void RefreshLinked()
        {
            int count = VarLinks.Installed(VarSwitches.AddonPackages(VamPath)).Count;
            LinkedText = $"{count:N0} packages linked";
        }

        private void SaveSetup()
        {
            string? problem = settings.Problem();
            SetupProblem = problem ?? "";
            if (problem != null)
                return;
            settings.Save(settingsFile);
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
