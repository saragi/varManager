using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace varManager
{
    /// <summary>Shows what the library needs, and gets it in one click. Checks by itself.</summary>
    public class FormMissing : Form
    {
        // Left free on the drive, so a download can't fill it.
        private const long ReservedBytes = 1L << 30;
        private static HashSet<string> listedPackages;

        private readonly Form1 owner;
        private readonly Label title = new Label { Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI", 13F), Padding = new Padding(6, 6, 0, 0) };
        private readonly ComboBox scope = new ComboBox { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button getAll = new Button { Text = "Get all", AutoSize = true, Enabled = false };
        private readonly Button turnOn = new Button { Text = "Turn on all", AutoSize = true, Enabled = false };
        private readonly Button getUpdates = new Button { Text = "Get updates", AutoSize = true, Enabled = false };
        private readonly Button copyList = new Button { Text = "Copy list", AutoSize = true, Enabled = false };
        private readonly Button cancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false };
        private readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(6, 4, 0, 0) };
        private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 8, Visible = false };

        private MissingReport report = new MissingReport();
        private MissingSources sources = new MissingSources();
        private List<HubPackage> offered = new List<HubPackage>();
        private List<HubPackage> updates = new List<HubPackage>();
        private CancellationTokenSource downloading;
        private int checkNumber;
        private string lastResult;

        // Downloads have dependencies of their own, so Get all keeps going until none are left.
        private bool followingChain;
        private readonly HashSet<string> attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> turnedOnInChain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> selected;

        // Update DB is running for this window. Checking now would read the old library.
        private bool waitingForScan;

        /// <param name="selected">The vars picked when the window opened, or null.</param>
        public FormMissing(Form1 owner, List<string> selected)
        {
            this.owner = owner;
            this.selected = selected;
            Text = "Missing";
            ClientSize = new Size(900, 600);
            MinimumSize = new Size(640, 400);
            StartPosition = FormStartPosition.CenterParent;

            scope.Items.AddRange(new object[] { "Whole library", "This profile" });
            if (selected != null)
                scope.Items.Add("Selected vars");
            scope.SelectedIndex = selected != null ? 2 : 0;

            list.Columns.Add("Package", 340);
            list.Columns.Add("Needed by", 400);
            list.Columns.Add("Size", 90);

            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4) };
            bar.Controls.AddRange(new Control[] { getAll, turnOn, getUpdates, copyList, cancel, scope });

            Controls.Add(list);
            Controls.Add(bar);
            Controls.Add(title);
            Controls.Add(progress);
            Controls.Add(status);

            Load += (s, e) => Recheck();
            scope.SelectedIndexChanged += (s, e) => Recheck();
            getAll.Click += (s, e) => Run(() =>
            {
                followingChain = true;
                scope.Enabled = false;
                attempted.Clear();
                turnedOnInChain.Clear();
                return DownloadNext();
            });
            getUpdates.Click += (s, e) => Run(() => Download(updates, owner.UpdatesToLink(updates)));
            turnOn.Click += (s, e) => Run(() =>
            {
                Scan(report.TurnedOff.Keys.ToList());
                return Task.CompletedTask;
            });
            copyList.Click += (s, e) =>
            {
                Clipboard.SetText(string.Join("\r\n", sources.Listed.Select(item => item.Reference)));
                status.Text = $"List of {sources.Listed.Count} copied.";
            };
            cancel.Click += (s, e) =>
            {
                StopChain();
                downloading?.Cancel();
            };
            owner.LibraryChanged += LibraryChanged;
            FormClosed += (s, e) =>
            {
                owner.LibraryChanged -= LibraryChanged;
                followingChain = false;
                downloading?.Cancel();
            };
        }

        private bool IsNeeded(HubPackage package)
        {
            string name = MissingPlanner.PackageOf(package.Name);
            return sources.OnHub.Any(item => string.Equals(item.Package, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Downloads that something linked needs. The rest stay stored and off.</summary>
        private List<string> LinkedNeeds()
        {
            HashSet<string> wanted = new HashSet<string>(sources.OnHub.Where(item => item.NeededByLinkedVar).Select(item => item.Package), StringComparer.OrdinalIgnoreCase);
            return offered.Where(package => wanted.Contains(MissingPlanner.PackageOf(package.Name))).Select(package => package.Name).ToList();
        }

        private void LibraryChanged()
        {
            waitingForScan = false;
            Recheck();
        }

        private void Recheck()
        {
            if (downloading == null && !waitingForScan && !IsDisposed)
                Check();
        }

        private void StopChain()
        {
            followingChain = false;
            scope.Enabled = true;
        }

        private async void Run(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                StopChain();
                if (!IsDisposed)
                    status.Text = "Stopped: " + ex.Message;
            }
        }

        /// <summary>Runs Update DB and links these vars. The next check starts when it ends.</summary>
        private void Scan(List<string> toLink)
        {
            waitingForScan = true;
            try
            {
                owner.LinkAndUpdate(toLink);
            }
            catch (Exception)
            {
                waitingForScan = false;
                throw;
            }
        }

        private async void Check()
        {
            int check = ++checkNumber;
            try
            {
                report = owner.PlanMissing((string)scope.SelectedItem, selected);
                sources = new MissingSources();
                sources.NotFound.AddRange(report.Needed);
                offered = new List<HubPackage>();
                updates = new List<HubPackage>();
                Render("Checking the Hub...");

                string hubProblem = null;
                HashSet<string> listed = await ListedPackages();
                try
                {
                    // Asked directly, because the Hub's package list doesn't hold every package.
                    offered = await HubClient.FindAll(report.Needed.Select(item => item.Package + ".latest"));
                    string packageList = await HubClient.GetPackageList();
                    Dictionary<string, int> versions = await Task.Run(() => HubParser.LatestVersions(packageList));
                    updates = (await HubClient.FindAll(MissingPlanner.UpdateRequests(owner.ScopeVars((string)scope.SelectedItem, selected), versions)))
                        .Where(package => !owner.Owns(package.Name)).ToList();
                }
                catch (Exception ex)
                {
                    hubProblem = "The Hub couldn't be reached: " + ex.Message;
                }
                if (check != checkNumber || IsDisposed)
                    return;
                sources = MissingPlanner.Sort(report.Needed, offered, listed);
                Render(hubProblem ?? lastResult ?? "Checked.");
                if (followingChain)
                {
                    if (hubProblem == null)
                        await DownloadNext();
                    else
                        StopChain();
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    StopChain();
                    status.Text = "Check failed: " + ex.Message;
                }
            }
        }

        private static async Task<HashSet<string>> ListedPackages()
        {
            if (listedPackages != null)
                return listedPackages;
            string install = SharpVaMToolsData.FindBeside(Properties.Settings.Default.vampath);
            HashSet<string> empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (install == null)
                return empty;
            try
            {
                listedPackages = await Task.Run(() => SharpVaMToolsData.Packages(install));
                return listedPackages;
            }
            catch (IOException)
            {
                return empty;
            }
        }

        private void Render(string message)
        {
            long hubBytes = offered.Where(IsNeeded).Sum(package => package.Size);
            int ready = sources.OnHub.Count;
            title.Text = ready > 0 ? $"{ready} packages ready on the Hub ({Size(hubBytes)})"
                : report.Count == 0 ? "Nothing is missing" : "Nothing to download from the Hub";
            getAll.Text = ready > 0 ? $"Get all {ready}" : "Get all";
            getAll.Enabled = ready > 0;
            turnOn.Enabled = report.TurnedOff.Count > 0;
            getUpdates.Text = updates.Count > 0 ? $"Get {updates.Count} updates" : "Get updates";
            getUpdates.Enabled = updates.Count > 0;
            copyList.Enabled = sources.Listed.Count > 0;
            status.Text = message;

            list.BeginUpdate();
            list.Items.Clear();
            list.Groups.Clear();
            AddGroup($"Ready on Hub ({sources.OnHub.Count})", sources.OnHub.Select(Row));
            AddGroup($"Turned off in your library ({report.TurnedOff.Count})",
                report.TurnedOff.Select(off => new[] { off.Key, Users(off.Value), "" }));
            AddGroup($"Updates on Hub ({updates.Count})", updates.Select(package => new[] { package.Name, "", Size(package.Size) }));
            AddGroup($"Listed in SharpVaMTools ({sources.Listed.Count})", sources.Listed.Select(Row));
            AddGroup($"Not found ({sources.NotFound.Count})", sources.NotFound.Select(Row));
            AddGroup($"Already covered by a newer version ({report.Covered.Count})", Enumerable.Empty<string[]>());
            list.EndUpdate();
        }

        private string[] Row(MissingPackage item)
        {
            HubPackage package = offered.FirstOrDefault(found => string.Equals(MissingPlanner.PackageOf(found.Name), item.Package, StringComparison.OrdinalIgnoreCase));
            return new[] { item.Reference, Users(item.NeededBy), package == null ? "" : Size(package.Size) };
        }

        private void AddGroup(string header, IEnumerable<string[]> rows)
        {
            ListViewGroup group = new ListViewGroup(header);
            list.Groups.Add(group);
            foreach (string[] row in rows)
                list.Items.Add(new ListViewItem(row, group));
            if (group.Items.Count == 0)
                list.Items.Add(new ListViewItem(new[] { "", "", "" }, group));
        }

        private static string Users(ICollection<string> users)
        {
            string first = users.FirstOrDefault();
            return users.Count <= 1 ? first : $"{first} and {users.Count - 1} more";
        }

        private static string Size(long bytes)
        {
            return bytes >= 1L << 30 ? $"{bytes / 1073741824.0:F2} GB" : $"{bytes / 1048576.0:F1} MB";
        }

        private async Task DownloadNext()
        {
            List<HubPackage> next = offered.Where(IsNeeded).Where(package => !attempted.Contains(package.Name)).ToList();
            if (next.Count > 0)
            {
                attempted.UnionWith(next.Select(package => package.Name));
                await Download(next, LinkedNeeds());
                return;
            }
            // Stored vars the downloads need, and what those need in turn. Other turned-off vars
            // stay off, because they may be off on purpose.
            List<string> needed = report.TurnedOff
                .Where(off => !turnedOnInChain.Contains(off.Key) && (off.Value.Overlaps(attempted) || off.Value.Overlaps(turnedOnInChain)))
                .Select(off => off.Key).ToList();
            if (needed.Count > 0)
            {
                turnedOnInChain.UnionWith(needed);
                Scan(needed);
            }
            else
            {
                StopChain();
            }
        }

        private async Task Download(List<HubPackage> packages, List<string> toLink)
        {
            string folder = Properties.Settings.Default.varspath;
            long needed = packages.Sum(package => package.Size);
            long free = FreeSpace(folder);
            if (needed + ReservedBytes > free)
            {
                StopChain();
                MessageBox.Show(this, $"This needs {Size(needed)}, but only {Size(free)} is free.", "Missing");
                return;
            }

            DownloadResult result;
            owner.PauseFolderWatch(true);
            downloading = new CancellationTokenSource();
            getAll.Enabled = getUpdates.Enabled = turnOn.Enabled = false;
            cancel.Enabled = true;
            progress.Maximum = Math.Max(packages.Count, 1);
            progress.Value = 0;
            progress.Visible = true;
            try
            {
                int done = 0;
                using (HubDownloader downloader = new HubDownloader())
                {
                    result = await downloader.DownloadAll(packages, folder, package => Post(() =>
                    {
                        progress.Value = Math.Min(++done, progress.Maximum);
                        status.Text = $"Downloaded {done} of {packages.Count}";
                    }), downloading.Token);
                }
            }
            finally
            {
                downloading.Dispose();
                downloading = null;
                owner.PauseFolderWatch(false);
                if (!IsDisposed)
                {
                    cancel.Enabled = false;
                    progress.Visible = false;
                }
            }

            foreach (KeyValuePair<string, string> failure in result.Failed)
                owner.LogProblem($"{failure.Key} download failed: {failure.Value}");
            lastResult = $"Stored {result.Stored.Count}, failed {result.Failed.Count}.";
            if (result.Failed.Count > 0)
                lastResult += " First problem: " + result.Failed.First().Value;
            if (!IsDisposed)
                status.Text = lastResult + " Scanning...";
            if (result.Stored.Count > 0)
            {
                Scan(toLink.Where(name => result.Stored.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList());
            }
            else
            {
                // A chain still has stored vars to turn on, even when this round stored nothing.
                Recheck();
            }
        }

        /// <summary>A network folder has no drive to ask, so it counts as having room.</summary>
        private static long FreeSpace(string folder)
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

        private void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The window closed while this was on its way.
            }
        }
    }
}
