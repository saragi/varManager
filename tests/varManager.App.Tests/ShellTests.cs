using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using varManager.Ui;
using Xunit;

namespace varManager.App.Tests
{
    public sealed class ShellTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "vmshell-" + Guid.NewGuid().ToString("N"));
        private readonly string vam;
        private readonly string vars;
        private readonly string settingsFile;
        private readonly JobRunner jobs = new JobRunner(null);

        public ShellTests()
        {
            vam = Path.Combine(root, "VaM");
            vars = Path.Combine(root, "vars");
            settingsFile = Path.Combine(root, "settings.json");
            Directory.CreateDirectory(vam);
            Directory.CreateDirectory(vars);
            File.WriteAllText(Path.Combine(vam, "VaM.exe"), "");
        }

        public void Dispose()
        {
            string packs = Path.Combine(vam, "AddonPackages");
            if (Directory.Exists(packs) && new DirectoryInfo(packs).Attributes.HasFlag(FileAttributes.ReparsePoint))
                new DirectoryInfo(packs).Delete();
            Directory.Delete(root, true);
        }

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private sealed class QueueContext : SynchronizationContext
        {
            private readonly Queue<Action> queue = new Queue<Action>();

            public int Count
            {
                get { lock (queue) return queue.Count; }
            }

            public override void Post(SendOrPostCallback d, object? state)
            {
                lock (queue) queue.Enqueue(() => d(state));
            }

            public void Run()
            {
                while (true)
                {
                    Action next;
                    lock (queue)
                    {
                        if (queue.Count == 0)
                            return;
                        next = queue.Dequeue();
                    }
                    next();
                }
            }
        }

        private void SaveSettings()
        {
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settingsFile);
        }

        private ShellViewModel Shell(Func<bool>? running = null, Action<string>? start = null)
        {
            return new ShellViewModel(settingsFile, jobs, running ?? (() => false), start ?? (_ => { }), () => 0);
        }

        [Fact]
        public void Missing_settings_load_empty()
        {
            AppSettings settings = AppSettings.Load(settingsFile);

            Assert.Equal("", settings.VamPath);
            Assert.NotNull(settings.Problem());
        }

        [Fact]
        public void Broken_settings_load_empty()
        {
            File.WriteAllText(settingsFile, "{ not json");

            Assert.Equal("", AppSettings.Load(settingsFile).VarsPath);
        }

        [Fact]
        public void An_import_that_could_not_start_keeps_update_db_off()
        {
            SaveSettings();
            File.WriteAllText(Path.Combine(root, "varManager.mdb"), "not read in this test");
            using var release = new ManualResetEventSlim();
            Task job = jobs.Start("Busy", "Done.", _ => release.Wait(Timeout))!;

            ShellViewModel shell = Shell();
            release.Set();
            job.Wait(Timeout);
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (jobs.IsRunning && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            shell.UpdateCommand.Execute(null);

            Assert.Contains("wasn't imported", jobs.Note);
            Assert.False(File.Exists(Path.Combine(root, "library.db")));

            // A store that appeared some other way doesn't mean the import ran.
            LibraryStore.Open(Path.Combine(root, "library.db")).Dispose();
            shell.UpdateCommand.Execute(null);
            Assert.Contains("wasn't imported", jobs.Note);
        }

        [Fact]
        public void Hardware_rendering_stays_off_unless_asked_for()
        {
            SaveSettings();
            Assert.False(AppSettings.Load(settingsFile).HardwareRendering);

            new AppSettings { VamPath = vam, VarsPath = vars, HardwareRendering = true }.Save(settingsFile);

            Assert.True(AppSettings.Load(settingsFile).HardwareRendering);
        }

        [Fact]
        public void Settings_round_trip_and_leave_no_part_file()
        {
            SaveSettings();

            AppSettings settings = AppSettings.Load(settingsFile);

            Assert.Equal(vam, settings.VamPath);
            Assert.Equal(vars, settings.VarsPath);
            Assert.False(File.Exists(settingsFile + ".part"));
        }

        [Fact]
        public void Good_folders_have_no_problem()
        {
            Assert.Null(new AppSettings { VamPath = vam, VarsPath = vars }.Problem());
        }

        [Fact]
        public void A_vam_folder_without_the_program_is_refused()
        {
            Assert.Contains("VaM.exe", new AppSettings { VamPath = vars, VarsPath = vars }.Problem());
        }

        [Fact]
        public void A_missing_vars_folder_is_refused()
        {
            Assert.Contains("vars", new AppSettings { VamPath = vam, VarsPath = Path.Combine(root, "gone") }.Problem());
        }

        [Theory]
        [InlineData("AddonPackages")]
        [InlineData(@"AddonPackages\sub")]
        public void Vars_inside_addon_packages_are_refused(string relative)
        {
            string inside = Path.Combine(vam, relative);
            Directory.CreateDirectory(inside);

            Assert.Contains("inside", new AppSettings { VamPath = vam, VarsPath = inside }.Problem());
        }

        [Fact]
        public void A_real_addon_packages_folder_is_refused_and_left_alone()
        {
            string packs = Path.Combine(vam, "AddonPackages");
            Directory.CreateDirectory(packs);
            File.WriteAllText(Path.Combine(packs, "A.B.1.var"), "x");
            SaveSettings();

            ShellViewModel shell = Shell();

            Assert.True(shell.NeedsSetup);
            Assert.Contains("real folder", shell.SetupProblem);
            Assert.True(File.Exists(Path.Combine(packs, "A.B.1.var")));
            Assert.False(new DirectoryInfo(packs).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }

        [Fact]
        public void First_run_asks_for_folders_and_touches_nothing()
        {
            ShellViewModel shell = Shell();

            Assert.True(shell.NeedsSetup);
            Assert.False(shell.IsReady);
            Assert.False(File.Exists(settingsFile));
            Assert.False(Directory.Exists(Path.Combine(vam, "AddonPackages")));
        }

        [Fact]
        public void Bad_setup_shows_the_problem_and_saves_nothing()
        {
            ShellViewModel shell = Shell();
            shell.VamPath = vars;

            shell.SaveSetupCommand.Execute(null);

            Assert.True(shell.NeedsSetup);
            Assert.Contains("VaM.exe", shell.SetupProblem);
            Assert.False(File.Exists(settingsFile));
        }

        [Fact]
        public void Good_setup_saves_and_opens_the_library()
        {
            ShellViewModel shell = Shell();
            shell.VamPath = vam;
            shell.VarsPath = vars;

            shell.SaveSetupCommand.Execute(null);

            Assert.False(shell.NeedsSetup);
            Assert.True(File.Exists(settingsFile));
            Assert.Equal(new[] { "default" }, shell.Switches);
            Assert.Equal("default", shell.SelectedSwitch);
        }

        [Fact]
        public void Pages_match_the_design_and_start_on_library()
        {
            SaveSettings();

            ShellViewModel shell = Shell();

            Assert.Equal(new[] { "Library", "Scenes", "Profiles", "Health", "Missing" }, shell.Pages.Select(p => p.Name));
            Assert.Equal("Library", shell.SelectedPage.Name);
        }

        [Fact]
        public void Opening_shows_the_current_switch_and_link_count()
        {
            SaveSettings();
            Directory.CreateDirectory(Path.Combine(vam, LibraryFolders.AddonPacksSwitch, "lean", LibraryFolders.VarsLink));
            int error;
            VarSwitches.Point(vam, "lean", out error);
            string var = Path.Combine(vars, "A.B.1.var");
            File.WriteAllText(var, "x");
            VarLinks.Create(Path.Combine(vam, "AddonPackages", LibraryFolders.VarsLink, "A.B.1.var"), var);

            ShellViewModel shell = Shell();
            Assert.True(shell.LibraryLoaded.Wait(Timeout));

            Assert.Equal("lean", shell.SelectedSwitch);
            Assert.Equal(new[] { "default", "lean" }, shell.Switches);
            Assert.Equal("1 package linked", shell.LinkedText);
        }

        [Fact]
        public void Choosing_a_profile_switches_the_link()
        {
            SaveSettings();
            ShellViewModel shell = Shell();
            string marker = Path.Combine(vam, LibraryFolders.AddonPacksSwitch, "lean", "marker.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "lean");

            shell.SelectedSwitch = "lean";

            Assert.Equal("lean", File.ReadAllText(Path.Combine(vam, "AddonPackages", "marker.txt")));
            Assert.Contains("lean", jobs.Note);
            Assert.True(shell.LibraryLoaded.Wait(Timeout));
            Assert.Equal("0 packages linked", shell.LinkedText);
        }

        [Fact]
        public void A_running_job_blocks_switching()
        {
            SaveSettings();
            ShellViewModel shell = Shell();
            using var release = new ManualResetEventSlim();
            Task job = jobs.Start("Scan", "Done.", _ => release.Wait(Timeout))!;

            shell.SelectedSwitch = "lean";

            Assert.Equal("default", shell.SelectedSwitch);
            Assert.False(jobs.IsIdle);
            Assert.Contains("Wait", jobs.Note);
            release.Set();
            job.Wait(Timeout);
            Assert.True(jobs.IsIdle);
        }

        [Fact]
        public void Failing_to_switch_says_so_and_clears_the_choice()
        {
            SaveSettings();
            ShellViewModel shell = Shell();
            Assert.True(shell.LibraryLoaded.Wait(Timeout));
            new DirectoryInfo(Path.Combine(vam, "AddonPackages")).Delete();
            File.WriteAllText(Path.Combine(vam, "AddonPackages"), "a file in the way");

            shell.SelectedSwitch = "lean";

            Assert.Null(shell.SelectedSwitch);
            Assert.Contains("Could not switch", jobs.Note);
            Assert.Equal("Nothing linked", shell.LinkedText);
            File.Delete(Path.Combine(vam, "AddonPackages"));
        }

        [Fact]
        public void A_cleared_profile_box_changes_nothing()
        {
            SaveSettings();
            ShellViewModel shell = Shell();

            shell.SelectedSwitch = null;

            Assert.Equal("default", shell.SelectedSwitch);
        }

        [Fact]
        public void A_refused_switch_tells_the_box_after_it_finishes_setting()
        {
            SaveSettings();
            ShellViewModel shell = Shell();
            // The startup read raises changes from another thread, which an unlocked list can't take.
            Assert.True(shell.LibraryLoaded.Wait(Timeout));
            using var release = new ManualResetEventSlim();
            Task job = jobs.Start("Scan", "Done.", _ => release.Wait(Timeout))!;
            var changes = new List<string?>();
            shell.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            SynchronizationContext? before = SynchronizationContext.Current;
            var context = new QueueContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                shell.SelectedSwitch = "lean";

                Assert.DoesNotContain(nameof(ShellViewModel.SelectedSwitch), changes);
                context.Run();
                Assert.Contains(nameof(ShellViewModel.SelectedSwitch), changes);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(before);
                release.Set();
                job.Wait(Timeout);
            }
        }

        [Fact]
        public void An_addon_packages_file_is_refused_and_left_alone()
        {
            string packs = Path.Combine(vam, "AddonPackages");
            File.WriteAllText(packs, "a file in the way");
            SaveSettings();

            ShellViewModel shell = Shell();

            Assert.True(shell.NeedsSetup);
            Assert.Contains("is a file", shell.SetupProblem);
            Assert.Equal("a file in the way", File.ReadAllText(packs));
            File.Delete(packs);
        }

        [Fact]
        public void Setup_can_be_retried_after_a_mistake()
        {
            ShellViewModel shell = Shell();
            shell.VamPath = vars;
            shell.SaveSetupCommand.Execute(null);

            shell.VamPath = vam;
            shell.VarsPath = vars;
            shell.SaveSetupCommand.Execute(null);

            Assert.False(shell.NeedsSetup);
            Assert.True(shell.IsReady);
            Assert.Equal("", shell.SetupProblem);
        }

        [Fact]
        public void A_settings_file_that_cant_be_written_is_reported_in_setup()
        {
            Directory.CreateDirectory(settingsFile);
            ShellViewModel shell = Shell();
            shell.VamPath = vam;
            shell.VarsPath = vars;

            shell.SaveSetupCommand.Execute(null);

            Assert.True(shell.NeedsSetup);
            Assert.Contains("Could not save", shell.SetupProblem);
        }

        [Fact]
        public void Saving_replaces_older_settings()
        {
            SaveSettings();

            new AppSettings { VamPath = "other", VarsPath = "other" }.Save(settingsFile);

            Assert.Equal("other", AppSettings.Load(settingsFile).VamPath);
        }

        [Fact]
        public void Job_state_changes_on_the_ui_context()
        {
            var context = new QueueContext();
            var runner = new JobRunner(context);

            Task job = runner.Start("Scan", "Scan finished.", work => work.Report(1, 2))!;
            job.Wait(Timeout);

            Assert.True(runner.IsRunning);
            Assert.Equal(0, runner.Percent);
            Assert.True(context.Count > 0);

            context.Run();

            Assert.False(runner.IsRunning);
            Assert.Equal("Scan finished.", runner.Note);
        }

        [Fact]
        public void Launch_starts_vam_from_its_folder()
        {
            SaveSettings();
            string? started = null;
            ShellViewModel shell = Shell(start: path => started = path);

            shell.LaunchCommand.Execute(null);

            Assert.Equal(vam, started);
        }

        [Fact]
        public void Launch_does_nothing_when_vam_is_running()
        {
            SaveSettings();
            bool started = false;
            ShellViewModel shell = Shell(() => true, _ => started = true);

            shell.LaunchCommand.Execute(null);

            Assert.False(started);
            Assert.Contains("already running", jobs.Note);
        }

        [Fact]
        public void Launch_failure_is_reported_in_the_job_bar()
        {
            SaveSettings();
            ShellViewModel shell = Shell(start: _ => throw new Win32Exception(2));

            shell.LaunchCommand.Execute(null);

            Assert.Contains("Could not start VaM", jobs.Note);
        }

        [Fact]
        public void A_job_reports_progress_and_finishes_with_its_note()
        {
            int seen = 0;
            Task job = jobs.Start("Scan", "Scan finished.", context =>
            {
                context.Report(1, 4);
                seen = jobs.Percent;
            })!;

            job.Wait();

            Assert.Equal(25, seen);
            Assert.False(jobs.IsRunning);
            Assert.Equal("", jobs.Name);
            Assert.Equal(0, jobs.Percent);
            Assert.Equal("Scan finished.", jobs.Note);
        }

        [Fact]
        public void Only_one_job_runs_at_a_time()
        {
            using var release = new ManualResetEventSlim();
            Task first = jobs.Start("Scan", "Done.", _ => release.Wait(Timeout))!;

            Assert.True(jobs.IsRunning);
            Assert.Null(jobs.Start("Other", "Done.", _ => { }));

            release.Set();
            first.Wait(Timeout);
            Assert.NotNull(jobs.Start("Other", "Done.", _ => { }));
        }

        [Fact]
        public void A_failed_job_reports_the_reason_and_frees_the_runner()
        {
            jobs.Start("Scan", "Done.", _ => throw new IOException("disk full"))!.Wait();

            Assert.Equal("Scan failed: disk full", jobs.Note);
            Assert.False(jobs.IsRunning);
        }

        [Fact]
        public void A_job_can_be_cancelled()
        {
            using var started = new ManualResetEventSlim();
            Task job = jobs.Start("Scan", "Done.", context =>
            {
                started.Set();
                context.Token.WaitHandle.WaitOne(5000);
                context.Token.ThrowIfCancellationRequested();
            })!;
            Assert.True(started.Wait(Timeout));

            Shell().CancelCommand.Execute(null);
            job.Wait(Timeout);

            Assert.Equal("Scan stopped.", jobs.Note);
            Assert.False(jobs.IsRunning);
        }

        [Fact]
        public void Progress_with_no_total_stays_at_zero()
        {
            int seen = -1;
            jobs.Start("Scan", "Done.", context =>
            {
                context.Report(3, 0);
                seen = jobs.Percent;
            })!.Wait();

            Assert.Equal(0, seen);
        }
    }
}
