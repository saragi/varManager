using DgvFilterPopup;
using ICSharpCode.SharpZipLib.Zip;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.Drawing;
using System.IO;
//using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
//using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using varManager.Properties;
using SimpleJSON;
using static SimpleLogger;

namespace varManager
{
    public partial class Form1 : Form
    {
        private static readonly byte[] s_creatorNameUtf8 = Encoding.UTF8.GetBytes("creatorName");
        //private static ReadOnlySpan<byte> Utf8Bom => new byte[] { 0xEF, 0xBB, 0xBF };
        private static SimpleLogger simpLog = new SimpleLogger();
        internal static string tidiedDirName = LibraryFolders.Tidied;
        private static string redundantDirName = LibraryFolders.Redundant;
        private static string notComplyRuleDirName = LibraryFolders.NotComplyRule;
        private static string previewpicsDirName = LibraryFolders.PreviewPics;
        private static string staleVarsDirName = LibraryFolders.StaleVars;
        private static string oldVersionVarsDirName = LibraryFolders.OldVersionVars;
        private static string deleVarsDirName = LibraryFolders.DeletedVars;
        private static string profilesDirName = LibraryFolders.Profiles;
        private const string migratedVarsFile = "varsMigrated.txt";

        internal static string addonPacksSwitch = LibraryFolders.AddonPacksSwitch;

        internal static string installLinkDirName = LibraryFolders.VarsLink;
        internal static string missingVarLinkDirName = LibraryFolders.MissingVarLink;
        internal static string tempVarLinkDirName = LibraryFolders.TempVarLink;
        //private varManagerDataSet.dependenciesDataTable installedDependencies = new varManagerDataSet.dependenciesDataTable();
        private InvokeAddLoglist addlog;
        public Form1()
        {
            InitializeComponent();
            addlog = new InvokeAddLoglist(UpdateAddLoglist);
            HookIndexInvalidation();
            AddProfileButtons();
            AddMissingButton();
        }

        private Button buttonMissing;
        private FormMissing formMissing;
        private FileSystemWatcher repositoryWatcher;
        private System.Windows.Forms.Timer repositoryTimer;
        private bool folderWatchPaused;
        private volatile bool libraryLoaded;

        /// <summary>Raised on the window's thread after any background job finishes.</summary>
        internal event Action LibraryChanged;

        private void AddMissingButton()
        {
            buttonMissing = new Button { Text = "Missing", Size = buttonHub.Size, Margin = buttonHub.Margin };
            buttonMissing.Click += buttonMissing_Click;
            buttonHub.Parent.Controls.Add(buttonMissing);
            buttonHub.Parent.Controls.SetChildIndex(buttonMissing, buttonHub.Parent.Controls.GetChildIndex(buttonHub) + 1);
        }

        private void buttonMissing_Click(object sender, EventArgs e)
        {
            if (formMissing == null || formMissing.IsDisposed)
            {
                List<string> selected = varsViewDataGridView.SelectedRows.Cast<DataGridViewRow>()
                    .Select(row => row.Cells["varNameDataGridViewTextBoxColumn"].Value.ToString()).ToList();
                formMissing = new FormMissing(this, selected.Count > 1 ? selected : null);
                formMissing.Show(this);
            }
            else
            {
                formMissing.Activate();
            }
        }

        /// <summary>Updates the count on the button. It reads the database only, so it needs no network.</summary>
        private void RefreshMissingCount()
        {
            try
            {
                int count = PlanMissing("Whole library", null).Count;
                buttonMissing.Text = count > 0 ? "Missing " + count : "Missing";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                UpdateAddLoglist("Could not check missing packages: " + ex.Message, LogLevel.WARNING);
            }
        }

        internal IEnumerable<string> ScopeVars(string scope, IEnumerable<string> selected)
        {
            if (scope == "This profile")
                return GetInstalledVars().Keys.ToList();
            if (scope == "Selected vars")
                return DependencyScanner.Closure(selected, VarLookup, varName => DependencyLookup.DependenciesOf(varName)).Vars;
            return varManagerDataSet.vars.Where(IsLive).Select(row => row.varName).ToList();
        }

        internal MissingReport PlanMissing(string scope, IEnumerable<string> selected)
        {
            return MissingPlanner.Plan(ScopeVars(scope, selected), varName => DependencyLookup.DependenciesOf(varName), VarLookup, GetInstalledVars().Keys);
        }

        internal bool Owns(string varName)
        {
            return VarLookup.Contains(varName);
        }

        /// <summary>Links an update when an older version of its package is linked.</summary>
        internal List<string> UpdatesToLink(IEnumerable<HubPackage> updates)
        {
            HashSet<string> linked = new HashSet<string>(GetInstalledVars().Keys.Select(MissingPlanner.PackageOf), StringComparer.OrdinalIgnoreCase);
            return updates.Select(update => update.Name).Where(name => linked.Contains(MissingPlanner.PackageOf(name))).ToList();
        }

        internal void LogProblem(string message)
        {
            UpdateAddLoglist(message, LogLevel.WARNING);
        }

        /// <summary>Runs Update DB, then links exactly these vars without following dependencies.</summary>
        internal void LinkAndUpdate(IEnumerable<string> varNames)
        {
            pendingLinks.AddRange(varNames);
            if (backgroundWorkerInstall.IsBusy)
                updateAfterJob = true;
            else
                StartPendingUpdate();
        }

        // Held here while a job runs, because the job reads and rewrites the file on disk.
        private readonly List<string> pendingLinks = new List<string>();
        private bool updateAfterJob;

        private void StartPendingUpdate()
        {
            File.AppendAllLines(migratedVarsFile, pendingLinks);
            pendingLinks.Clear();
            StartAutomaticUpdate();
        }

        /// <summary>Nobody asked for this job, so nobody knows not to switch profile while it links.</summary>
        private void StartAutomaticUpdate()
        {
            groupBoxSwitch.Enabled = false;
            backgroundWorkerInstall.RunWorkerAsync("UpdDB");
        }

        internal void PauseFolderWatch(bool paused)
        {
            folderWatchPaused = paused;
        }

        /// <summary>Vars placed in the repository are scanned without asking, once they finish arriving.</summary>
        private void WatchRepository()
        {
            string folder = Settings.Default.varspath;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return;
            repositoryTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            repositoryTimer.Tick += RepositoryTimer_Tick;
            repositoryWatcher = new FileSystemWatcher(folder, "*.var") { SynchronizingObject = this, IncludeSubdirectories = false };
            repositoryWatcher.Created += RepositoryChanged;
            repositoryWatcher.Renamed += RepositoryChanged;
            repositoryWatcher.EnableRaisingEvents = true;
            // Also picks up vars added while the app was closed.
            repositoryTimer.Start();
        }

        private void RepositoryChanged(object sender, FileSystemEventArgs e)
        {
            repositoryTimer.Stop();
            repositoryTimer.Start();
        }

        private void RepositoryTimer_Tick(object sender, EventArgs e)
        {
            // CanFocus is false while a dialog is open, and a dialog's action may assume no job runs.
            if (!CanFocus || folderWatchPaused || backgroundWorkerInstall.IsBusy || !RepositoryVarsReady())
                return;
            repositoryTimer.Stop();
            if (VarTidier.RepositoryVars(Settings.Default.varspath).Count > 0)
                StartAutomaticUpdate();
        }

        /// <summary>A var still being copied can't be opened exclusively.</summary>
        private static bool RepositoryVarsReady()
        {
            foreach (string file in Directory.GetFiles(Settings.Default.varspath, "*.var"))
            {
                try
                {
                    using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    // Update DB logs a var it can't read, so this one needn't hold the others back.
                }
            }
            return true;
        }

        private void AddProfileButtons()
        {
            Control parent = buttonSetting.Parent;
            AddSideButton(parent, "Lean Profile", 9, 670, 66, 36, buttonLeanProfile_Click);
            AddSideButton(parent, "Profile Check", 78, 670, 66, 36, buttonProfileCheck_Click);
            AddSideButton(parent, "Exclude Profile", 9, 709, 66, 36, buttonExcludeProfile_Click);
            AddSideButton(parent, "Launch VaM", 78, 709, 66, 36, buttonLaunchVaM_Click);
        }

        private static void AddSideButton(Control parent, string text, int x, int y, int width, int height, EventHandler click)
        {
            Button button = new Button { Text = text, Location = new Point(x, y), Size = new Size(width, height) };
            button.Click += click;
            parent.Controls.Add(button);
        }

        private static string ProfilePath(string name)
        {
            return Path.Combine(Settings.Default.varspath, profilesDirName, name + ".profile");
        }

        private static string SwitchLinkPath(string name)
        {
            return VarSwitches.LinkFolder(Settings.Default.vampath, name);
        }

        /// <summary>Packages VaM needs in every profile: session plugins, the default scene and base.txt.</summary>
        private static List<string> BaseReferences()
        {
            HashSet<string> references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] sources =
            {
                Path.Combine(Settings.Default.vampath, "Custom", "PluginPresets", "Plugins_UserDefaults.vap"),
                Path.Combine(Settings.Default.vampath, "Saves", "scene", "MeshedVR", "default.json"),
            };
            foreach (string source in sources.Where(File.Exists))
                references.UnionWith(DependencyScanner.Extract(source, File.ReadAllText(source)));
            string baseList = Path.Combine(Settings.Default.varspath, profilesDirName, "base.txt");
            if (File.Exists(baseList))
                references.UnionWith(File.ReadAllLines(baseList).Select(line => line.Trim()).Where(line => line.Length > 0));
            return references.OrderBy(reference => reference, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private ProfilePlan PlanProfile(LeanProfile profile)
        {
            Func<string, IEnumerable<string>> referencesOf = varName => DependencyLookup.DependenciesOf(varName);
            Func<string, long> sizeOf = varName =>
            {
                varManagerDataSet.varsRow row = varManagerDataSet.vars.FindByvarName(varName);
                return row == null || row.IsfilesizeNull() ? 0 : (long)row.filesize;
            };
            if (profile.Exclude.Count == 0)
                return LeanProfiles.Plan(profile, VarLookup, referencesOf, sizeOf);
            return LeanProfiles.PlanExcluding(profile, DefaultSwitchVars(), VarLookup, referencesOf, sizeOf);
        }

        /// <summary>What VaM loads on the default switch, from the same places the installed scan reads.</summary>
        private List<string> DefaultSwitchVars()
        {
            string switchPath = Path.Combine(Settings.Default.vampath, addonPacksSwitch, "default");
            return LeanProfiles.ActiveLinks(Path.Combine(switchPath, installLinkDirName), SearchOption.AllDirectories)
                .Concat(LeanProfiles.ActiveLinks(switchPath, SearchOption.TopDirectoryOnly))
                .Where(VarLookup.Contains)
                .ToList();
        }

        private string UnusedSwitchName(string name)
        {
            return VarSwitches.UnusedName(comboBoxPacksSwitch.Items.Cast<string>(), name);
        }

        /// <summary>Applies links first, so a failure leaves the saved profile describing the last good state.</summary>
        private void ApplyProfile(string name, ProfilePlan plan, LeanProfile toSave)
        {
            string switchName = comboBoxPacksSwitch.Items.Cast<string>()
                .FirstOrDefault(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            bool isNew = switchName == null;
            if (isNew)
            {
                switchName = name;
                comboBoxPacksSwitch.Items.Add(switchName);
            }
            try
            {
                LeanProfiles.ApplyLinks(plan.Closure.Vars, SwitchLinkPath(switchName), getVarFilePath, VarLinks.Create);
            }
            catch (Exception)
            {
                if (isNew && !LeanProfiles.Links(SwitchLinkPath(switchName)).Any())
                    comboBoxPacksSwitch.Items.Remove(switchName);
                throw;
            }
            if (toSave != null)
                LeanProfiles.Save(toSave, ProfilePath(switchName));
            if (string.Equals((string)comboBoxPacksSwitch.SelectedItem, switchName, StringComparison.OrdinalIgnoreCase))
            {
                UpdateVarsInstalled();
                RescanPackages();
            }
            else
            {
                comboBoxPacksSwitch.SelectedItem = switchName;
            }
        }

        private void RunProfileAction(Action action)
        {
            if (backgroundWorkerInstall.IsBusy)
            {
                MessageBox.Show("Another operation is still running. Please wait for it to finish.");
                return;
            }
            try
            {
                action();
            }
            catch (Exception ex)
            {
                UpdateAddLoglist("Profile action failed: " + ex.Message, LogLevel.ERROR);
                MessageBox.Show("The profile action failed: " + ex.Message);
            }
        }

        private void buttonLeanProfile_Click(object sender, EventArgs e)
        {
            RunProfileAction(() =>
            {
                LeanProfile profile = new LeanProfile();
                foreach (DataGridViewRow row in varsViewDataGridView.SelectedRows)
                    profile.Direct.Add(row.Cells["varNameDataGridViewTextBoxColumn"].Value.ToString());
                if (profile.Direct.Count == 0)
                {
                    MessageBox.Show("Select the vars you want in the list first.");
                    return;
                }
                profile.Base.AddRange(BaseReferences());
                ProfilePlan plan = PlanProfile(profile);
                using (FormLeanProfile form = new FormLeanProfile(name => LeanProfiles.Describe(plan,
                    LeanProfiles.Diff(plan.Closure.Vars, LeanProfiles.Links(SwitchLinkPath(name)).Select(Path.GetFileNameWithoutExtension)))))
                {
                    if (form.ShowDialog(this) == DialogResult.OK)
                        ApplyProfile(form.ProfileName, plan, profile);
                }
            });
        }

        private void buttonExcludeProfile_Click(object sender, EventArgs e)
        {
            RunProfileAction(() =>
            {
                string current = (string)comboBoxPacksSwitch.SelectedItem;
                string path = string.IsNullOrEmpty(current) ? null : ProfilePath(current);
                LeanProfile existing = path != null && File.Exists(path) ? LeanProfiles.Load(path) : null;
                bool editing = existing != null && existing.Exclude.Count > 0;

                SortedSet<string> exclusions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                if (editing)
                    exclusions.UnionWith(existing.Exclude);
                foreach (DataGridViewRow row in varsViewDataGridView.SelectedRows)
                    exclusions.Add(row.Cells["varNameDataGridViewTextBoxColumn"].Value.ToString());
                if (exclusions.Count == 0)
                {
                    MessageBox.Show("Select the vars you want to leave out in the list first.");
                    return;
                }

                List<string> baseReferences = BaseReferences();
                Func<List<string>, LeanProfile> profileOf = picks =>
                {
                    LeanProfile profile = new LeanProfile();
                    profile.Base.AddRange(baseReferences);
                    profile.Exclude.AddRange(picks);
                    return profile;
                };
                using (FormExcludeProfile form = new FormExcludeProfile(editing ? current : UnusedSwitchName("trimmed"), exclusions, (name, picks) =>
                {
                    ProfilePlan plan = PlanProfile(profileOf(picks));
                    return LeanProfiles.Describe(plan, LeanProfiles.Diff(plan.Closure.Vars,
                        LeanProfiles.Links(SwitchLinkPath(name)).Select(Path.GetFileNameWithoutExtension)));
                }))
                {
                    if (form.ShowDialog(this) == DialogResult.OK)
                    {
                        LeanProfile profile = profileOf(form.Exclusions);
                        ApplyProfile(form.ProfileName, PlanProfile(profile), profile);
                    }
                }
            });
        }

        private void buttonProfileCheck_Click(object sender, EventArgs e)
        {
            RunProfileAction(() =>
            {
                string name = (string)comboBoxPacksSwitch.SelectedItem;
                string path = string.IsNullOrEmpty(name) ? null : ProfilePath(name);
                LeanProfile profile = path != null && File.Exists(path) ? LeanProfiles.Load(path) : null;
                if (profile == null || profile.Direct.Count + profile.Exclude.Count == 0)
                {
                    MessageBox.Show("The current switch isn't a lean profile.");
                    return;
                }
                ProfilePlan plan = PlanProfile(profile);
                ProfileHealth health = LeanProfiles.Check(plan, SwitchLinkPath(name));
                string previous = LeanProfiles.PreviousPath(path);
                DialogResult result;
                using (FormProfileCheck form = new FormProfileCheck(LeanProfiles.Describe(health), health.CanRepair, File.Exists(previous)))
                {
                    result = form.ShowDialog(this);
                }
                if (result == DialogResult.Yes)
                {
                    ApplyProfile(name, plan, null);
                }
                else if (result == DialogResult.Retry)
                {
                    LeanProfile restored = LeanProfiles.Load(previous);
                    ApplyProfile(name, PlanProfile(restored), restored);
                }
            });
        }

        private void buttonLaunchVaM_Click(object sender, EventArgs e)
        {
            if (Process.GetProcessesByName("vam").Length > 0)
            {
                MessageBox.Show("VaM is already running.");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Settings.Default.vampath, "VaM.exe"), "-vrmode None")
                {
                    WorkingDirectory = Settings.Default.vampath,
                    UseShellExecute = false
                });
            }
            catch (Win32Exception ex)
            {
                MessageBox.Show("Could not start VaM: " + ex.Message);
            }
        }

        private volatile VarIndex varIndexCache;
        private volatile DependencyIndex dependencyIndexCache;
        private volatile ILookup<string, varManagerDataSet.scenesRow> scenesLookupCache;
        private int varIndexGeneration;
        private int dependencyIndexGeneration;
        private int scenesLookupGeneration;

        private void HookIndexInvalidation()
        {
            DataRowChangeEventHandler varsChanged = (s, e) => InvalidateVarIndex();
            varManagerDataSet.vars.RowChanged += varsChanged;
            varManagerDataSet.vars.RowDeleted += varsChanged;
            varManagerDataSet.vars.TableCleared += (s, e) => InvalidateVarIndex();

            DataRowChangeEventHandler dependenciesChanged = (s, e) => InvalidateDependencyIndex();
            varManagerDataSet.dependencies.RowChanged += dependenciesChanged;
            varManagerDataSet.dependencies.RowDeleted += dependenciesChanged;
            varManagerDataSet.dependencies.TableCleared += (s, e) => InvalidateDependencyIndex();

            DataRowChangeEventHandler scenesChanged = (s, e) => InvalidateScenesLookup();
            varManagerDataSet.scenes.RowChanged += scenesChanged;
            varManagerDataSet.scenes.RowDeleted += scenesChanged;
            varManagerDataSet.scenes.TableCleared += (s, e) => InvalidateScenesLookup();
        }

        private void InvalidateVarIndex()
        {
            Interlocked.Increment(ref varIndexGeneration);
            varIndexCache = null;
        }

        private void InvalidateDependencyIndex()
        {
            Interlocked.Increment(ref dependencyIndexGeneration);
            dependencyIndexCache = null;
        }

        private void InvalidateScenesLookup()
        {
            Interlocked.Increment(ref scenesLookupGeneration);
            scenesLookupCache = null;
        }

        private static bool IsLive(DataRow row)
        {
            return row.RowState != DataRowState.Deleted && row.RowState != DataRowState.Detached;
        }

        private VarIndex VarLookup
        {
            get
            {
                VarIndex index = varIndexCache;
                if (index == null)
                {
                    int generation = Volatile.Read(ref varIndexGeneration);
                    List<VarEntry> entries = new List<VarEntry>();
                    foreach (varManagerDataSet.varsRow row in varManagerDataSet.vars.Rows)
                    {
                        if (IsLive(row))
                            entries.Add(new VarEntry(row.varName, row.creatorName, row.packageName, row.version));
                    }
                    index = new VarIndex(entries);
                    if (generation == Volatile.Read(ref varIndexGeneration))
                        varIndexCache = index;
                }
                return index;
            }
        }

        private DependencyIndex DependencyLookup
        {
            get
            {
                DependencyIndex index = dependencyIndexCache;
                if (index == null)
                {
                    int generation = Volatile.Read(ref dependencyIndexGeneration);
                    List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
                    foreach (varManagerDataSet.dependenciesRow row in varManagerDataSet.dependencies.Rows)
                    {
                        if (IsLive(row))
                            pairs.Add(new KeyValuePair<string, string>(row.varName, row.dependency));
                    }
                    index = new DependencyIndex(pairs);
                    if (generation == Volatile.Read(ref dependencyIndexGeneration))
                        dependencyIndexCache = index;
                }
                return index;
            }
        }

        private ILookup<string, varManagerDataSet.scenesRow> ScenesLookup
        {
            get
            {
                ILookup<string, varManagerDataSet.scenesRow> lookup = scenesLookupCache;
                if (lookup == null)
                {
                    int generation = Volatile.Read(ref scenesLookupGeneration);
                    lookup = varManagerDataSet.scenes.Rows.Cast<varManagerDataSet.scenesRow>()
                        .Where(IsLive)
                        .ToLookup(row => row.varName, StringComparer.Ordinal);
                    if (generation == Volatile.Read(ref scenesLookupGeneration))
                        scenesLookupCache = lookup;
                }
                return lookup;
            }
        }

        /// <summary>Runs a background job unless one is already active.</summary>
        private void StartBackgroundJob(string job)
        {
            if (backgroundWorkerInstall.IsBusy)
            {
                MessageBox.Show("Another operation is still running. Please wait for it to finish.");
                return;
            }
            backgroundWorkerInstall.RunWorkerAsync(job);
        }

        /// <summary>Loads an image without keeping its file locked.</summary>
        private static Image LoadImageUnlocked(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (Image image = Image.FromStream(stream))
            {
                return new Bitmap(image);
            }
        }

        /// <summary>Escapes a value for a DataView LIKE filter.</summary>
        private static string EscapeLikeValue(string value)
        {
            StringBuilder escaped = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == '[' || c == ']' || c == '*' || c == '%')
                    escaped.Append('[').Append(c).Append(']');
                else if (c == '\'')
                    escaped.Append("''");
                else
                    escaped.Append(c);
            }
            return escaped.ToString();
        }

        private void buttonSetting_Click(object sender, EventArgs e)
        {
            OpenSetting();
        }

        private static void OpenSetting()
        {
            FormSettings formSettings = new FormSettings();
            if (formSettings.ShowDialog() == DialogResult.OK)
            {
                Application.Restart();
                Environment.Exit(0);
            }
        }

        private List<string> varsForInstall = new List<string>();
        private void TidyVars()
        {
            List<string> vars = VarTidier.RepositoryVars(Settings.Default.varspath);
            List<string> varsUsed = VarTidier.LooseAddonVars(AddonPackagesPath).ToList();
            IEnumerable<string> saved = File.Exists("varsForInstall.txt") ? File.ReadAllLines("varsForInstall.txt") : new string[0];
            varsForInstall = VarTidier.MergeInstallList(saved, varsUsed);
            File.Delete("varsForInstall.txt");
            File.WriteAllLines("varsForInstall.txt", varsForInstall);

            vars.AddRange(varsUsed);

            InvokeProgress mi = new InvokeProgress(UpdateProgress);
            this.BeginInvoke(addlog, new Object[] { "Tidy Vars...", LogLevel.INFO });
            VarTidier.Tidy(vars, Settings.Default.varspath,
                problem => this.BeginInvoke(addlog, new Object[] { problem, LogLevel.ERROR }),
                (current, total) => this.BeginInvoke(mi, new Object[] { current, total }));
        }

        private static string AddonPackagesPath
        {
            get { return VarSwitches.AddonPackages(Settings.Default.vampath); }
        }

        private static bool ExistAddonpackagesVar()
        {
            return VarTidier.LooseAddonVars(AddonPackagesPath).Any();
        }

        private List<string> Getdependencies(string jsonstring)
        {
            return DependencyScanner.Extract("meta.json", jsonstring).ToList();
        }

        /// <summary>Lists which files inside a var reference which packages.</summary>
        public string DescribeVarDependencies(string varName)
        {
            string varfile = getVarFilePath(varName);
            if (!File.Exists(varfile))
                return "Var file not found: " + varfile;
            try
            {
                return DependencyScanner.Describe(VarScanner.FileReferences(varfile, LogScanWarning), VarLookup, varName);
            }
            catch (Exception ex)
            {
                return "Could not read " + varfile + ": " + ex.Message;
            }
        }

        private bool Varislatest(string varname)
        {
            return VarLookup.IsLatest(varname);
        }
        private List<string> DependentVars(string varname)
        {
            return DependencyAnalysis.DependentVars(varname, VarLookup, DependencyLookup);
        }
        private List<string> DependentSaved(string varname)
        {
            List<string> saveds = new List<string>();
            varManagerDataSet.savedepensDataTable savedepens = new varManagerDataSet.savedepensDataTable();
            
            if (Varislatest(varname))
            {
                string latest = varname.Substring(0, varname.LastIndexOf('.')) + ".latest";
                savedepensTableAdapter.FillByDepens(savedepens, latest);
                savedepens.AcceptChanges();
                foreach (var row in savedepens)
                {
                    saveds.Add(row.savepath);
                }
            }
            savedepens.Clear();
            savedepens.AcceptChanges();

            savedepensTableAdapter.FillByDepens(savedepens, varname);
            savedepens.AcceptChanges();
            foreach (var row in savedepens)
            {
                saveds.Add(row.savepath);
            }
            saveds= saveds.Distinct().ToList();
            return saveds;
        }
        private List<string> ImplicatedVars(List<string> varnames)
        {
            return DependencyAnalysis.ImplicatedVars(varnames, VarLookup, DependencyLookup);
        }

        private void DelePreviewPics(string varname)
        {
            ScenePreviews.DeleteAll(Settings.Default.varspath, varname);
        }

        private void UnintallVars(List<string> varnames)
        {
            //FillInstalledDependencies();
            List<string> varimplics = ImplicatedVars(varnames);

            FormUninstallVars formUninstallVars = new FormUninstallVars();
            formUninstallVars.previewpicsDirName = previewpicsDirName;
            foreach (string varname in varimplics)
            {
                foreach (var row in varManagerDataSet.varsView.Where(q => q.varName == varname && q.Installed))
                {
                    formUninstallVars.varManagerDataSet.varsView.Rows.Add(row.ItemArray);
                }
            }
            if (formUninstallVars.ShowDialog() == DialogResult.OK)
            {
                var installedvars = GetInstalledVars();
                foreach (string varname in varimplics)
                {
                    if (VarLinks.Remove(installedvars, varname))
                        this.BeginInvoke(addlog, new Object[] { $"{varname} is uninstalled.", LogLevel.INFO });
                }
            }
        }
        public string getVarFilePath(string varname)
        {
            string varfilepath = "";
            var varRow = varManagerDataSet.vars.FindByvarName(varname);
            if (varRow != null)
            {
                varfilepath = Path.Combine(Settings.Default.varspath, varRow.varPath, varRow.varName + ".var");
            }
            return varfilepath;
        }

        private void DeleteVars(List<string> varnames)
        {
            // FillInstalledDependencies();
            List<string> varimplics = ImplicatedVars(varnames);

            FormUninstallVars formUninstallVars = new FormUninstallVars();
            formUninstallVars.operationType = "delete";
            formUninstallVars.deleVarsDirName = deleVarsDirName;
            formUninstallVars.previewpicsDirName = previewpicsDirName;
            foreach (string varname in varimplics)
            {
                foreach (var row in varManagerDataSet.varsView.Where(q => q.varName == varname))
                {
                    formUninstallVars.varManagerDataSet.varsView.Rows.Add(row.ItemArray);
                }
            }
            if (formUninstallVars.ShowDialog() == DialogResult.OK)
            {
                string delevarspath = Path.Combine(Settings.Default.varspath, deleVarsDirName);
                if (!Directory.Exists(delevarspath))
                    Directory.CreateDirectory(delevarspath);

                var installedvars = GetInstalledVars();
                foreach (string varname in varimplics)
                {
                    VarLinks.Remove(installedvars, varname);

                    string operav = Path.Combine(Settings.Default.varspath, varManagerDataSet.vars.FindByvarName(varname).varPath, varname + ".var");
                    string deletedv = Path.Combine(delevarspath, varname + ".var");
                    try
                    {
                        File.Move(operav, deletedv);
                        CleanVar(varname);
                    }
                    catch (Exception ex)
                    {
                        this.BeginInvoke(addlog, new Object[] { $"{operav} move failed,{ex.Message}", LogLevel.ERROR });
                    }
                }
            }

        }
        private DgvFilterManager dgvFilterManager;
        private Dictionary<string, string> GetInstalledVars()
        {
            return VarLinks.Installed(AddonPackagesPath);
        }
        private void UpdateVarsInstalled()
        {
            foreach (var row in this.varManagerDataSet.installStatus)
                row.Delete();
            installStatusTableAdapter.Update(varManagerDataSet.installStatus);
            this.varManagerDataSet.installStatus.AcceptChanges();
            installStatusTableAdapter.DeleteAll();
            this.varManagerDataSet.installStatus.Clear();
            this.varManagerDataSet.installStatus.AcceptChanges();
            mutex.WaitOne();
            try
            {
                foreach (string varfile in GetInstalledVars().Values)
                {
                    string varName = Path.GetFileNameWithoutExtension(varfile);
                    if (varManagerDataSet.vars.FindByvarName(varName) != null)
                    {
                        bool isdisable = File.Exists(varfile + ".disabled");
                        varManagerDataSet.installStatus.AddinstallStatusRow(varName, true, isdisable);
                    }
                }

                installStatusTableAdapter.Update(varManagerDataSet.installStatus);
                this.varManagerDataSet.installStatus.AcceptChanges();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
            // TODO: 这行代码将数据加载到表“varManagerDataSet1.varsView”中。您可以根据需要移动或删除它。

            //varsViewBindingSource.ResetBindings(true);
            InvokeUpdateVarsViewDataGridView invokeUpdateVarsViewDataGridView = new InvokeUpdateVarsViewDataGridView(UpdateVarsViewDataGridView);
            this.BeginInvoke(invokeUpdateVarsViewDataGridView);
            
            //varsViewDataGridView.Update();
        }
        private Mutex mutex;
        private System.Threading.Mutex mut = new Mutex();
      
        private void Form1_Load(object sender, EventArgs e)
        {
            this.Text = "VarManager  v" + Assembly.GetEntryAssembly().GetName().Version.ToString();
            if (!File.Exists(Path.Combine(Settings.Default.vampath, "VaM.exe")))
            {
                OpenSetting();
            }
            if (!File.Exists(Path.Combine(Settings.Default.vampath, "VaM.exe")))
            {
                this.Close();
                return;
            }
            // TODO: 这行代码将数据加载到表“varManagerDataSet.installStatus”中。您可以根据需要移动或删除它。
            //this.installStatusTableAdapter.DeleteAll();
            string varspath = new DirectoryInfo(Settings.Default.varspath).FullName.TrimEnd(Path.DirectorySeparatorChar);
            string packpath = new DirectoryInfo(Path.Combine(Settings.Default.vampath, "AddonPackages")).FullName;
            string packroot = packpath.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(varspath, packroot, StringComparison.OrdinalIgnoreCase)
                || varspath.StartsWith(packroot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Vars Path can't be {VamInstallDir}\\AddonPackages or a folder inside it");
                OpenSetting();
                this.Close();
                return;
            }
            if (!MigrateRealAddonPackages(packpath, varspath))
            {
                this.Close();
                return;
            }

            mutex = new System.Threading.Mutex();

            StartBackgroundJob("FillDataTables");
            //

            comboBoxPreviewType.SelectedIndex = 0;

            foreach (string packname in VarSwitches.Names(Settings.Default.vampath))
                comboBoxPacksSwitch.Items.Add(packname);
            string currentSwitch = VarSwitches.EnsureLink(Settings.Default.vampath);
            if (comboBoxPacksSwitch.Items.IndexOf(currentSwitch) >= 0)
            {
                comboBoxPacksSwitch.SelectedItem = currentSwitch;
            }
            //TimeSpan ts5 = DateTime.Now - dtstart;
            //dtstart = DateTime.Now;
            //string[] packswitchdirs = Directory.GetDirectories(packsSwitchpath, "*", SearchOption.TopDirectoryOnly);

            UpdateVarsInstalled();
            comboBoxCreater.Items.Add("____ALL");
            foreach (var row in this.varManagerDataSet.vars.GroupBy(g => g.creatorName))
            {
                comboBoxCreater.Items.Add(row.Key);
            }
            comboBoxCreater.SelectedIndex = 0;
            //  FillDataTables();
            //TimeSpan ts6 = DateTime.Now - dtstart;
            //dtstart = DateTime.Now;
            //MessageBox.Show($"{ts1.TotalSeconds},{ts2.TotalSeconds},{ts3.TotalSeconds},{ts4.TotalSeconds},{ts5.TotalSeconds},{ts6.TotalSeconds}");
            dgvFilterManager = new DgvFilterManager(varsViewDataGridView);
            if (ExistAddonpackagesVar())
            {
                MessageBox.Show("There are unorganized var files in the current switch, please run UPD_DB first");
                buttonUpdDB.Focus();
            }
        }
        
        /// <summary>
        /// Moves a real AddonPackages folder into the repository, after showing the plan.
        /// Returns false if startup must stop, so nothing below runs on an unmigrated folder.
        /// </summary>
        private bool MigrateRealAddonPackages(string packpath, string varspath)
        {
            DirectoryInfo packInfo = new DirectoryInfo(packpath);
            if (!packInfo.Exists || packInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
            if (Process.GetProcessesByName("vam").Length > 0)
            {
                MessageBox.Show("Please close VaM first. varManager needs to move files out of AddonPackages.");
                return false;
            }
            string[] linkDirs = { installLinkDirName, missingVarLinkDirName, tempVarLinkDirName };
            MigrationPlan plan = LibraryMigration.Plan(packpath, varspath, tidiedDirName, redundantDirName, linkDirs);
            if (plan.Actions.Count == 0 && plan.LeftInPlace.Count == 0)
                return true;
            using (FormMigrationPreview preview = new FormMigrationPreview(plan, packpath, varspath))
            {
                if (preview.ShowDialog(this) != DialogResult.OK)
                    return false;
            }
            string logPath = Path.Combine(varspath, "___MigrationLogs___", $"migration-{DateTime.Now:yyyyMMdd-HHmmss}.tsv");
            try
            {
                LibraryMigration.Apply(plan, logPath, Path.Combine(Directory.GetCurrentDirectory(), migratedVarsFile));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"The move stopped: {ex.Message}\r\n\r\nTo put files back, run:\r\nvarManager.exe --undo-migration \"{logPath}\"");
                return false;
            }
            MessageBox.Show($"Files moved. Run UPD_DB next to link your enabled vars back.\r\n\r\nLog: {logPath}");
            return true;
        }

        private void FillDataTables()
        {
            this.BeginInvoke(addlog, new Object[] { $"load vars...", LogLevel.INFO });
            // TODO: 这行代码将数据加载到表“varManagerDataSet.vars”中。您可以根据需要移动或删除它。
            this.varsTableAdapter.Fill(this.varManagerDataSet.vars); 
            this.BeginInvoke(addlog, new Object[] { $"load scenes...", LogLevel.INFO });
            // TODO: 这行代码将数据加载到表“varManagerDataSet.scenes”中。您可以根据需要移动或删除它。
            this.scenesTableAdapter.Fill(this.varManagerDataSet.scenes);
            this.BeginInvoke(addlog, new Object[] { $"load dependencies...", LogLevel.INFO });
            // TODO: 这行代码将数据加载到表“varManagerDataSet.dependencies”中。您可以根据需要移动或删除它。
            this.dependenciesTableAdapter.Fill(this.varManagerDataSet.dependencies);
            
        }

        public delegate void InvokeUpdateVarsViewDataGridView();
        
        public void UpdateVarsViewDataGridView()
        {
            List<string> selectedRowList = new List<string>();
            foreach (DataGridViewRow item in varsViewDataGridView.SelectedRows)
            {
                selectedRowList.Add(item.Cells[0].Value.ToString());
            }
            varsViewDataGridView.SelectionChanged -= new System.EventHandler(this.varsDataGridView_SelectionChanged);
            this.varsViewTableAdapter.Fill(this.varManagerDataSet.varsView);
            varsViewDataGridView.Update();

            int firstindex = int.MaxValue;
            varsViewDataGridView.ClearSelection();
            foreach (DataGridViewRow row in varsViewDataGridView.Rows)
            {
                string varname = row.Cells["varNamedataGridViewTextBoxColumn"].Value.ToString();
                if (selectedRowList.Contains(varname))
                {
                    row.Selected = true;
                    if (row.Index < firstindex) firstindex = row.Index;
                }
            }
            if (firstindex == int.MaxValue) firstindex = 0;
            if (varsViewDataGridView.SelectedRows.Count > 0)
            {
                varsViewDataGridView.FirstDisplayedScrollingRowIndex = firstindex;
            }
            varsViewDataGridView.SelectionChanged += new System.EventHandler(this.varsDataGridView_SelectionChanged);

            mutex.WaitOne();
            try
            {
                UpdatePreviewPics();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
            tableLayoutPanelPreview.Visible = false;
        }

        public delegate void InvokeAddLoglist(string message, LogLevel logLevel);

        public void UpdateAddLoglist(string message, LogLevel logLevel)
        {
            string msg = simpLog.WriteFormattedLog(logLevel, message);
            listBoxLog.Items.Add(msg);
            listBoxLog.TopIndex = listBoxLog.Items.Count - 1;
        }

        public delegate void InvokeProgress(int cur, int total);

        public void UpdateProgress(int cur, int total)
        {
            labelProgress.Text = string.Format("{0}/{1}", cur, total);
            if (total != 0)
            {
                int progressvalue = (int)((float)cur * 100 / (float)total);
                if (progressvalue < 0) progressvalue = 0;
                if (progressvalue >100) progressvalue = 100;

                progressBar1.Value = progressvalue;
            }
               
        }

        public delegate void InvokeShowformMissingVars(List<string> missingvars);

        public void ShowformMissingVars(List<string> missingvars)
        {
            if (missingvars.Count > 0)
            {
                FormMissingVars formMissingVars = new FormMissingVars();
                formMissingVars.form1 = this;
                formMissingVars.MissingVars = missingvars;
                formMissingVars.Show();
            }
        }
        private void buttonUpdDB_Click(object sender, EventArgs e)
        {
            string message = "Will organize vars, extract preview images,update DB. It will take some time, please be patient.";

            const string caption = "UpdateDB";
            var result = MessageBox.Show(message, caption,
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1);
            if (result == DialogResult.Yes)
                StartBackgroundJob("UpdDB");
        }

        private void UpdDB(string destvarfilename)
        {
            try
            {
                string basename = Path.GetFileNameWithoutExtension(destvarfilename);
                string curpath = Path.GetDirectoryName(destvarfilename);
                curpath = Comm.MakeRelativePath(Settings.Default.varspath, curpath);

                var varsrow = varManagerDataSet.vars.FindByvarName(basename);
                if (varsrow == null)
                {
                    VarScan scan = VarScanner.Read(destvarfilename, Path.Combine(Settings.Default.varspath, previewpicsDirName),
                        LogScanWarning, message => this.BeginInvoke(addlog, new Object[] { message, LogLevel.ERROR }));
                    if (scan.Outcome == VarScanOutcome.Invalid)
                    {
                        string notComplRulefilename = Path.Combine(Settings.Default.varspath, notComplyRuleDirName, Path.GetFileName(destvarfilename));
                        string errlog = $"{basename}, Invalid var package structure, move into {notComplyRuleDirName} directory";
                        this.BeginInvoke(addlog, new Object[] { errlog, LogLevel.WARNING });
                        File.Move(destvarfilename, notComplRulefilename);
                        return;
                    }
                    if (scan.Outcome == VarScanOutcome.Scanned)
                        SaveVarScan(basename, curpath, scan);
                }
                else
                {
                    // File times shift with daylight saving, so only size marks a changed var.
                    if (!varsrow.IsfilesizeNull() && varsrow.filesize != new FileInfo(destvarfilename).Length)
                    {
                        this.BeginInvoke(addlog, new Object[] { $"{basename} has changed, updating its record", LogLevel.INFO });
                        if (CleanVar(basename))
                            UpdDB(destvarfilename);
                        return;
                    }
                    if (varsrow.varPath != curpath)
                    {
                        varsrow.varPath = curpath;
                        varsTableAdapter.Update(varManagerDataSet.vars);
                        varManagerDataSet.vars.AcceptChanges();
                    }
                }

            }
            catch (Exception ex)
            {
                this.BeginInvoke(addlog, new Object[] { destvarfilename + " " + ex.Message, LogLevel.ERROR });
            }
        }
        private void LogScanWarning(string message)
        {
            this.BeginInvoke(addlog, new Object[] { message, LogLevel.WARNING });
        }

        private void SaveVarScan(string basename, string curpath, VarScan scan)
        {
            foreach (VarItem item in scan.Items)
            {
                try
                {
                    varManagerDataSet.scenes.AddscenesRow(basename, item.Type, item.IsPreset, item.EntryName, item.Preview);
                }
                catch (ArgumentException ex)
                {
                    // Columns hold 255 characters, and one long path must not cost the var its record.
                    this.BeginInvoke(addlog, new Object[] { item.EntryName + " " + ex.Message, LogLevel.ERROR });
                }
            }
            scenesTableAdapter.Update(varManagerDataSet.scenes);
            varManagerDataSet.scenes.AcceptChanges();

            var varsrow = varManagerDataSet.vars.NewvarsRow();
            varsrow.varName = basename;
            varsrow.filesize = scan.FileSize;
            varsrow.creatorName = scan.CreatorName;
            varsrow.packageName = scan.PackageName;
            varsrow.varDate = scan.VarDate;
            varsrow.version = scan.Version;
            varsrow.varPath = curpath;
            varsrow.metaDate = scan.MetaDate;
            varsrow.scenes = scan.Scenes;
            varsrow.looks = scan.Looks;
            varsrow.clothing = scan.Clothing;
            varsrow.hairstyle = scan.Hairstyle;
            varsrow.morphs = scan.Morphs;
            varsrow.pose = scan.Pose;
            varsrow.skin = scan.Skin;
            varsrow.plugins = scan.Plugins;
            varsrow.assets = scan.Assets;
            varManagerDataSet.vars.AddvarsRow(varsrow);
            varsTableAdapter.Update(varManagerDataSet.vars);
            varManagerDataSet.vars.AcceptChanges();

            var dependencierows = varManagerDataSet.dependencies.Where(q => q.varName == basename).ToList();
            for (int i = dependencierows.Count() - 1; i >= 0; i--)
            {
                dependencierows[i].Delete();
            }
            foreach (string dependencie in scan.Dependencies)
                varManagerDataSet.dependencies.AdddependenciesRow(basename, dependencie);
            dependenciesTableAdapter.Update(varManagerDataSet.dependencies);
            varManagerDataSet.dependencies.AcceptChanges();
        }

        private bool UpdDB()
        {
            InvokeProgress mi = new InvokeProgress(UpdateProgress);
            this.BeginInvoke(addlog, new Object[] { "Analyze Var files, extract preview images, save info to DB", LogLevel.INFO });
            string[] vars = Directory.GetFiles(Path.Combine(Settings.Default.varspath, tidiedDirName), "*.var", SearchOption.AllDirectories);
            if (vars.Length <= 0)
            {
                MessageBox.Show("No VAR file found, please check if the path setting is wrong!");
                return false;
            }
            HashSet<string> existVars = new HashSet<string>();
            int curVarfile = 0;
            foreach (string varfile in vars)
            {
                existVars.Add(Path.GetFileNameWithoutExtension(varfile));
                UpdDB(varfile);
                curVarfile++;
                this.BeginInvoke(mi, new Object[] { curVarfile, vars.Length });
            }
            varManagerDataSet.AcceptChanges();

            List<string> deletevars = new List<string>();


            foreach (var row in varManagerDataSet.vars)
            {
                if (!existVars.Contains(row.varName))
                {
                    this.BeginInvoke(addlog, new Object[] { $"{row.varName} The target VAR file is not found and the record will be deleted", LogLevel.WARNING });
                    deletevars.Add(row.varName);
                }
            }
            deletevars = deletevars.Distinct().ToList();
            if (deletevars.Count > 0)
                CleanVars(deletevars);

            //UpdateVarsInstalled();
            //this.varManagerDataSet.varsView.Clear();
            //this.varsViewTableAdapter.Fill(this.varManagerDataSet.varsView);
            //MessageBox.Show("Update DB finish!Please reopen this tool!");
            return true;
        }
        /// <summary>
        /// Deletes records by var name. Rows added this session hold temporary IDs, so the
        /// table adapters can't delete them by key.
        /// </summary>
        private void DeleteVarRecords(ICollection<string> varNames)
        {
            string[] tables = { "dependencies", "scenes", "vars" };
            using (OleDbConnection connection = new OleDbConnection(Settings.Default.varManagerConnectionString))
            {
                connection.Open();
                using (OleDbTransaction transaction = connection.BeginTransaction())
                {
                    foreach (string table in tables)
                    {
                        using (OleDbCommand command = new OleDbCommand($"DELETE FROM [{table}] WHERE varName = ?", connection, transaction))
                        {
                            OleDbParameter name = command.Parameters.Add("varName", OleDbType.VarWChar, 255);
                            foreach (string varName in varNames)
                            {
                                name.Value = varName;
                                command.ExecuteNonQuery();
                            }
                        }
                    }
                    transaction.Commit();
                }
            }

            HashSet<string> names = new HashSet<string>(varNames, StringComparer.OrdinalIgnoreCase);
            foreach (string table in tables)
            {
                DataTable dataTable = varManagerDataSet.Tables[table];
                foreach (DataRow row in dataTable.Rows.Cast<DataRow>().Where(r => IsLive(r) && names.Contains((string)r["varName"])).ToList())
                {
                    row.Delete();
                    row.AcceptChanges();
                }
            }
        }

        private bool CleanVars(List<string> deletevars)
        {
            try
            {
                this.BeginInvoke(addlog, new Object[] { $"Cleanup records...", LogLevel.INFO });
                DeleteVarRecords(deletevars);
                this.BeginInvoke(addlog, new Object[] { $"Cleanup records completed.", LogLevel.INFO });

                this.BeginInvoke(addlog, new Object[] { $"Cleanup PreviewPics...", LogLevel.INFO });
                foreach (string deletevar in deletevars)
                    DelePreviewPics(deletevar);
                FixPreview();
                this.BeginInvoke(addlog, new Object[] { $"Cleanup PreviewPics completed.", LogLevel.INFO });

            }
            catch (Exception ex)
            {
                this.BeginInvoke(addlog, new Object[] { "delete record or preview error, " + ex.Message, LogLevel.ERROR });
                return false;
            }
            return true;
        }
        private bool CleanVar(string deletevar)
        {
            try
            {
                DeleteVarRecords(new[] { deletevar });
                DelePreviewPics(deletevar);
            }
            catch (Exception ex)
            {
                this.BeginInvoke(addlog, new Object[] { deletevar + ",delete record or preview error, " + ex.Message, LogLevel.ERROR });
                return false;
            }
            return true;
        }

        /// <summary>
        /// varInstall
        /// </summary>
        /// <param name="varName"></param>
        /// <param name="bTemp"></param>
        /// <param name="operate"></param>
        /// <returns>0:faile,1:success，2：installed</returns>
        private VarLinkResult VarInstall(string varName, bool bTemp = false)
        {
            varManagerDataSet.varsRow varsrow = varManagerDataSet.vars.FindByvarName(varName);
            if (varsrow == null)
                return VarLinkResult.Failed;

            string destvarfile = Path.Combine(Settings.Default.varspath, varsrow.varPath, varsrow.varName + ".var");
            int errorCode;
            VarLinkResult result = VarLinks.Install(AddonPackagesPath, varName, destvarfile, bTemp, out errorCode);
            if (result == VarLinkResult.Failed)
                MessageBox.Show("Error: Unable to create symbolic link. (Error Code: " + errorCode + ")");
            else if (result == VarLinkResult.Linked)
                this.BeginInvoke(addlog, new Object[] { $"{varName}  Installed", LogLevel.INFO });
            return result;
        }

        private void backgroundWorkerInstall_DoWork(object sender, DoWorkEventArgs e)
        {
            mutex.WaitOne();
            try
            {
                RunBackgroundJob((string)e.Argument);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }

        /// <summary>
        /// Links exactly what was enabled before migration. Dependencies aren't followed,
        /// because that would turn disabled vars back on.
        /// </summary>
        private void InstallMigratedVars()
        {
            if (!File.Exists(migratedVarsFile))
                return;
            List<string> notLinked = File.ReadAllLines(migratedVarsFile)
                .Where(varname => varname.Length > 0 && VarInstall(varname) == VarLinkResult.Failed).ToList();
            if (notLinked.Count == 0)
            {
                File.Delete(migratedVarsFile);
                return;
            }
            // Kept so the next Update DB can retry them.
            File.WriteAllLines(migratedVarsFile, notLinked);
            this.BeginInvoke(addlog, new Object[] { $"{notLinked.Count} migrated vars were not linked, starting with {string.Join(", ", notLinked.Take(10))}. Update DB will retry them.", LogLevel.WARNING });
        }

        private void RunBackgroundJob(string job)
        {
            if (job == "FillDataTables")
            {
                /*
                Thread thread1 = new Thread(new ThreadStart(fillscenes));
                thread1.Start();
                Thread thread2 = new Thread(new ThreadStart(fillvars));
                thread2.Start();
                Thread thread3 = new Thread(new ThreadStart(filldependencies));
                thread3.Start();
                thread1.Join();
                thread2.Join();
                thread3.Join();
                */
                FillDataTables();
                libraryLoaded = true;
            } 
            if (job == "UpdDB")
            {
                TidyVars();
                UpdDB();
                InstallMigratedVars();

                if (varsForInstall.Count() > 0)
                {
                    var varNames = VarsDependencies(varsForInstall);
                    varsForInstall.Clear();
                    File.Delete("varsForInstall.txt");
                    foreach (string varname in varNames)
                    {
                        VarInstall(varname);
                    }
                }
                UpdateVarsInstalled();
                RescanPackages();
                //Application.Restart();
                //Environment.Exit(0);

            }

            if (job == "rebuildLink")
            {
                FixRebuildLink();
            } 
            if (job == "fixPreview")
            {
                FixPreview();
                MessageBox.Show("Fix preview finish");
            }
            if (job == "savesDepend")
            {
                FixSavseDependencies();
                UpdateVarsInstalled();
                RescanPackages();
            }
            if (job == "LogAnalysis")
            {
                LogAnalysis();
                UpdateVarsInstalled();
                RescanPackages();
            }
            if (job == "MissingDepends")
            {
                MissingDepends();
                UpdateVarsInstalled();
                RescanPackages();
            }
            if (job == "AllMissingDepends")
            {
                AllMissingDepends();
            }
            if (job == "FilteredMissingDepends")
            {
                FilteredMissingDepends();
            }
            if (job == "StaleVars")
            {
                StaleVars();
            }
            if (job == "OldVersionVars")
            {
                StaleVars();
                OldVersionVars();
            }
        }

        private void backgroundWorkerInstall_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                UpdateAddLoglist("Operation failed: " + e.Error.Message, LogLevel.ERROR);
            }
            groupBoxSwitch.Enabled = true;
            // After a failed load the tables are empty, and a scan would redo every var.
            if (repositoryWatcher == null && libraryLoaded)
                WatchRepository();
            if (updateAfterJob)
            {
                updateAfterJob = false;
                try
                {
                    StartPendingUpdate();
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    UpdateAddLoglist("Could not start Update DB: " + ex.Message, LogLevel.ERROR);
                }
            }
            RefreshMissingCount();
            LibraryChanged?.Invoke();
        }

        private void buttonMissingDepends_Click(object sender, EventArgs e)
        {
            string message = "Analyzing dependencies from Installed Vars, if it is found in the repository it will be installed, otherwise a processing window will be opened.";

            const string caption = "MissingDepends";
            var result = MessageBox.Show(message, caption,
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1);
            if (result == DialogResult.Yes)
            {
                StartBackgroundJob("MissingDepends");
            }
        }
        
        private void MissingDepends()
        {
            this.BeginInvoke(addlog, new Object[] { "Search for dependencies...", LogLevel.INFO });
            List<string> dependencies = new List<string>();
            foreach (var varrow in varManagerDataSet.varsView.Where(q => q.Installed == true))
            {
                dependencies.AddRange(DependencyLookup.DependenciesOf(varrow.varName));
            }
            List<ResolvedReference> resolved = ResolveAndLog(dependencies);
            foreach (ResolvedReference reference in resolved.Where(q => q.VarName != null))
                VarInstall(reference.VarName);
            ShowMissing(resolved);
        }

        private List<ResolvedReference> ResolveAndLog(IEnumerable<string> dependencies)
        {
            List<ResolvedReference> resolved = DependencyAnalysis.Resolve(dependencies.Distinct(), VarLookup);
            foreach (ResolvedReference reference in resolved)
            {
                if (reference.IsSubstitute)
                    this.BeginInvoke(addlog, new Object[] { reference.Reference + " missing version", LogLevel.INFO });
                if (reference.VarName == null)
                    this.BeginInvoke(addlog, new Object[] { reference.Reference + " missing", LogLevel.INFO });
            }
            return resolved;
        }

        private void ShowMissing(IEnumerable<ResolvedReference> resolved)
        {
            List<string> missingvars = DependencyAnalysis.MissingList(resolved);
            if (missingvars.Count > 0)
            {
                InvokeShowformMissingVars showformMissingVars = new InvokeShowformMissingVars(ShowformMissingVars);
                this.BeginInvoke(showformMissingVars, missingvars);
            }
        }

        private void FilteredMissingDepends()
        {
            this.BeginInvoke(addlog, new Object[] { "Search for dependencies...", LogLevel.INFO });
            List<string> dependencies = new List<string>();
            System.Collections.IList listDatarow = varsViewBindingSource.List;
            
            foreach (DataRowView varrowview in listDatarow)
            {
                dependencies.AddRange(DependencyLookup.DependenciesOf(varrowview.Row.Field<string>("varName")));
                //dependencies.Add(varrowview.Row.Field<string>("varName"));
                //dependencies.AddRange(varManagerDataSet.dependencies.Where(q => q.varName == varrow.varName).Select(q => q.dependency));
            }
            
            ShowMissing(ResolveAndLog(dependencies));
        }

        private void buttonAllMissingDepends_Click(object sender, EventArgs e)
        {
            string message = "Analyzing dependencies from All organized vars, a processing window will be opened.";

            const string caption = "AllMissingDepends";
            var result = MessageBox.Show(message, caption,
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1);
            if (result == DialogResult.Yes)
            {
                StartBackgroundJob("AllMissingDepends");
            }
        }
        public  void AllMissingDepends()
        {
            this.BeginInvoke(addlog, new Object[] { "Search for dependencies...", LogLevel.INFO });

            List<string> missingvars = MissingDependencies();
            if (missingvars.Count > 0)
            {
                this.BeginInvoke(addlog, new Object[] { $"Total { missingvars.Count } dependencies missing", LogLevel.INFO });
                InvokeShowformMissingVars showformMissingVars = new InvokeShowformMissingVars(ShowformMissingVars);
                this.BeginInvoke(showformMissingVars, missingvars);
            }
        }

        public List<string> MissingDependencies()
        {
            List<string> dependencies = varManagerDataSet.dependencies.Select(q => q.dependency).Distinct().ToList();
            return DependencyAnalysis.MissingList(DependencyAnalysis.Resolve(dependencies, VarLookup));
        }

        private void FixRebuildLink()
        {
            this.BeginInvoke(addlog, new Object[] { "Check Installed symlink", LogLevel.INFO });
            //List<string> varfiles = Directory.GetFiles(Path.Combine(Settings.Default.vampath, "AddonPackages", installLinkDirName), "*.var", SearchOption.AllDirectories).ToList();
            List<string> varfiles = GetInstalledVars().Values.ToList();
            if (Directory.Exists(Path.Combine(Settings.Default.vampath, "AddonPackages", missingVarLinkDirName)))
                varfiles.AddRange(Directory.GetFiles(Path.Combine(Settings.Default.vampath, "AddonPackages", missingVarLinkDirName), "*.var", SearchOption.AllDirectories));

            foreach (string linkvar in varfiles)
            {
                try
                {
                    if (VarLinks.IsLink(linkvar))
                    {
                        this.BeginInvoke(addlog, new Object[] { $"symlink {linkvar} rebuilding ...", LogLevel.INFO });
                        VarLinks.Rebuild(linkvar, varName =>
                        {
                            varManagerDataSet.varsRow varsrow = varManagerDataSet.vars.FindByvarName(varName);
                            return varsrow == null ? null : Path.Combine(Settings.Default.varspath, varsrow.varPath, varsrow.varName + ".var");
                        });
                    }
                }
                catch (Exception ex)
                {
                    this.BeginInvoke(addlog, new Object[] { linkvar + " rebuild symlink failed. " + ex.Message, LogLevel.ERROR });
                }
            }

            MessageBox.Show("fix finish");
        }

        private bool ReExtractedPreview(varManagerDataSet.scenesRow scenerow, string picpath)
        {
            var varsrow = this.varManagerDataSet.vars.FindByvarName(scenerow.varName);
            if (varsrow == null)
                return false;
            string destvarfile = Path.Combine(Settings.Default.varspath, varsrow.varPath, varsrow.varName + ".var");
            return ScenePreviews.Restore(destvarfile, scenerow.scenePath, picpath);
        }
        private void FixPreview()
        {
            foreach (varManagerDataSet.scenesRow scenerow in this.varManagerDataSet.scenes.Where(q=> !string.IsNullOrEmpty(q.previewPic)))
            {
                string picpath = ScenePreviews.PicturePath(Settings.Default.varspath, scenerow.atomType, scenerow.varName, scenerow.previewPic);
                if (!File.Exists(picpath))
                {
                    if (ReExtractedPreview(scenerow, picpath))
                    {
                        this.BeginInvoke(addlog, new Object[] { $"missing {picpath} is fixed.", LogLevel.INFO });
                    }
                    else
                    {
                        this.BeginInvoke(addlog, new Object[] { $"{picpath} is missing and the repair failed", LogLevel.WARNING });
                    }
                }
            }
           
        }
        private void FixSavseDependencies()
        {
            List<string> dependencies = new List<string>();
            this.BeginInvoke(addlog, new Object[] { "Analyze the *.json files in the 'Save' directory and  the *.vap files in the 'Custom' directory ", LogLevel.INFO });
            this.varManagerDataSet.savedepens.Clear();
            this.varManagerDataSet.savedepens.AcceptChanges();
            this.savedepensTableAdapter.Fill(this.varManagerDataSet.savedepens);
            ILookup<string, varManagerDataSet.savedepensRow> savedRows =
                this.varManagerDataSet.savedepens.ToLookup(q => q.savepath, StringComparer.Ordinal);
            foreach (string jsonfile in DependencyAnalysis.SaveFiles(Settings.Default.vampath))
            {
                FileInfo fi = new FileInfo(jsonfile);
                string savepath = DependencyAnalysis.SavePath(Settings.Default.vampath, jsonfile);

                this.BeginInvoke(addlog, new Object[] { $"Analyze { Path.GetFileName(jsonfile)} ...", LogLevel.INFO });

                var rows = savedRows[savepath].Where(q => Math.Abs((q.modidate - fi.LastWriteTime).TotalSeconds) <= 2).ToList();

                if (rows.Count > 0)
                {
                    foreach (var row in rows)
                    {
                        row.SetModified();
                        //dependencies.Add(row.dependency);
                    }
                }
                else
                {
                    try
                    {
                        string jsonstring = File.ReadAllText(jsonfile);
                        foreach (string dependency in Getdependencies(jsonstring))
                        {
                            this.varManagerDataSet.savedepens.AddsavedepensRow(savepath, fi.LastWriteTime, dependency);
                            //dependencies.Add(dependency);
                        }

                    }
                    catch (Exception ex)
                    {
                        this.BeginInvoke(addlog, new Object[] { jsonfile + " Get dependencies failed " + ex.Message, LogLevel.ERROR });
                    }
                }
            }
            foreach (var row in this.varManagerDataSet.savedepens)
            {
                if (row.RowState == DataRowState.Unchanged)
                    row.Delete();
            }
            this.savedepensTableAdapter.Update(this.varManagerDataSet.savedepens);
            this.varManagerDataSet.savedepens.AcceptChanges();
            dependencies = this.varManagerDataSet.savedepens.Select(q => q.dependency).Distinct().ToList();
            //dependencies = dependencies.Distinct().ToList();
            var dependencies2 = VarsDependencies(dependencies);
            dependencies = dependencies.Concat(dependencies2).Distinct().OrderBy(q => q).ToList();

            //List<string> varinstalled = new List<string>();
            //foreach (string varfile in Directory.GetFiles(Path.Combine(Settings.Default.vampath, "AddonPackages", installLinkDirName), "*.var", SearchOption.AllDirectories))
            //{
            //    varinstalled.Add(Path.GetFileNameWithoutExtension(varfile));
            //}
            //foreach (string varfile in Directory.GetFiles(Path.Combine(Settings.Default.vampath, "AddonPackages", missingVarLinkDirName), "*.var", SearchOption.AllDirectories))
            //{
            //    varinstalled.Add(Path.GetFileNameWithoutExtension(varfile));
            //}
            List<string> varinstalled = GetInstalledVars().Keys.ToList();
            dependencies = dependencies.Except(varinstalled).ToList();
            this.BeginInvoke(addlog, new Object[] { $"{dependencies.Count()} var files will be installed", LogLevel.INFO });
            List<ResolvedReference> resolved = DependencyAnalysis.Resolve(dependencies, VarLookup);
            foreach (ResolvedReference reference in resolved.Where(q => q.VarName != null))
            {
                VarInstall(reference.VarName);
                this.BeginInvoke(addlog, new Object[] { reference.VarName + " installed", LogLevel.INFO });
            }
            List<string> missingvars = DependencyAnalysis.MissingList(resolved).Distinct().ToList();
            if (missingvars.Count > 0)
            {
                InvokeShowformMissingVars showformMissingVars = new InvokeShowformMissingVars(ShowformMissingVars);
                this.BeginInvoke(showformMissingVars, missingvars);
            }
            else
                MessageBox.Show("fix finish");
        }

        private void buttonFixRebuildLink_Click(object sender, EventArgs e)
        {
            string message = "will analyze and repair symlinks in the AddonPackages folder, if your var file repository changes location";

            const string caption = "RebuildLink";
            var result = MessageBox.Show(message, caption,
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1);
            if (result == DialogResult.Yes)
                StartBackgroundJob("rebuildLink");
        }

        private void buttonFixSavesDepend_Click(object sender, EventArgs e)
        {
            string message = "Analyzing dependencies from json files in \"Saves\" folder";

            const string caption = "SavesDependencies";
            var result = MessageBox.Show(message, caption,
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1);
            if (result == DialogResult.Yes)
                StartBackgroundJob("savesDepend");
        }

        private void comboBoxCreater_SelectedIndexChanged(object sender, EventArgs e)
        {
            FilterVars();
        }

        private void FilterVars()
        {
            string strFilter = "1=1";
            if (comboBoxCreater.SelectedItem != null)
                if (comboBoxCreater.SelectedItem.ToString() != "____ALL")
                    strFilter += " AND creatorName = '" + comboBoxCreater.SelectedItem.ToString().Replace("'", "''") + "'";
            if (textBoxFilter.Text.Trim() != "")
            {
                strFilter += " AND varName Like '%" + EscapeLikeValue(textBoxFilter.Text.Trim()) + "%'";
            }
            if (checkBoxInstalled.CheckState == CheckState.Checked)
            {
                strFilter += " AND installed";
            }
            if (checkBoxInstalled.CheckState == CheckState.Unchecked)
            {
                strFilter += " AND (NOT installed OR installed Is Null )";
            }

            this.comboBoxCreater.SelectedIndexChanged -= new System.EventHandler(this.comboBoxCreater_SelectedIndexChanged);
            var creators = this.varManagerDataSet.vars.GroupBy(g => g.creatorName);
            if (textBoxFilter.Text.Trim() != "")
                creators = this.varManagerDataSet.vars.Where(q => q.varName.ToLower().IndexOf(textBoxFilter.Text.Trim().ToLower()) >= 0).GroupBy(g => g.creatorName);
            string curcreator = comboBoxCreater.Text;
            comboBoxCreater.Items.Clear();
            comboBoxCreater.Items.Add("____ALL");
            foreach (var creator in creators)
            {
                comboBoxCreater.Items.Add(creator.Key);
            }
            if (comboBoxCreater.Items.Contains(curcreator))
                comboBoxCreater.SelectedItem = curcreator;
            else
                comboBoxCreater.SelectedIndex = 0;
            this.comboBoxCreater.SelectedIndexChanged += new System.EventHandler(this.comboBoxCreater_SelectedIndexChanged);

            varsViewBindingSource.Filter = strFilter;
            varsViewDataGridView.Update();
        }
        public string VarExistName(string varname)
        {
            return VarLookup.Resolve(varname);
        }
        public string GetClosestMatchingPackageVersion(string creatorName,string packageName,int requestVersion)
        {
            return VarLookup.Closest(creatorName, packageName, requestVersion);
        }
        private List<string> VarsDependencies(List<string> varnames)
        {
            return DependencyAnalysis.WithDependencies(varnames, VarLookup, DependencyLookup);
        }

        private void varsDataGridView_SelectionChanged(object sender, EventArgs e)
        {
            UpdatePreviewPics();
            tableLayoutPanelPreview.Visible = false;
        }

        public struct Previewpic
        {
            public Previewpic(string varname, string atomtype, string picpath, bool installed, string scenePath, bool ispreset)
            {
                Varname = varname;
                Atomtype = atomtype;
                Picpath = picpath;
                Installed = installed;
                ScenePath = scenePath;
                IsPreset = ispreset;

            }
            public string Varname { get; }
            public string Atomtype { get; }
            public string Picpath { get; }
            public bool Installed { get; }
            public string ScenePath { get; }
            public bool IsPreset { get; }
        }

        private List<Previewpic> previewpics = new List<Previewpic>();

        private List<Previewpic> previewpicsfilter = new List<Previewpic>();

        private void UpdatePreviewPics()
        {
            previewpics.Clear();
            foreach (DataGridViewRow row in varsViewDataGridView.SelectedRows)
            {
                string varName = row.Cells["varNameDataGridViewTextBoxColumn"].Value.ToString();
                var installedCell = row.Cells["installedDataGridViewCheckBoxColumn"];
                bool installed = false;

                if (true.Equals(installedCell.Value))
                {
                    installed = true;
                }
                foreach (varManagerDataSet.scenesRow scenerow in ScenesLookup[varName])
                {
                    previewpics.Add(new Previewpic(varName, scenerow.atomType, scenerow.previewPic, installed, scenerow.scenePath, scenerow.isPreset));
                }
                //string[] typenames = new string[5] { "scenes", "looks", "clothing", "hairstyle", "assets" };
                //foreach (string typename in typenames)
                //{
                //    string typepath = Path.Combine(Settings.Default.varspath, previewpicsDirName, typename, varName);
                //    if (Directory.Exists(typepath))
                //    {
                //        foreach (string jpgfile in Directory.GetFiles(typepath, "*.jpg", SearchOption.AllDirectories))
                //        {
                //            string scenepath = "";
                //            EnumerableRowCollection<varManagerDataSet.scenesRow> scenesels = this.varManagerDataSet.scenes.Where(q => q.varName == varName && q.previewPic == Path.GetFileName(jpgfile));
                //            if (scenesels.Count() > 0) scenepath = scenesels.First().scenePath;
                //            previewpics.Add(new Previewpic(varName, typename, jpgfile,installed, scenepath));
                //        }
                //    }
                //}
            }
            PreviewInitType();
        }

        private void PreviewInitType()
        {
            mut.WaitOne();
            previewpicsfilter = previewpics;
            if (checkBoxPreviewTypeLoadable.CheckState == CheckState.Checked)
                previewpicsfilter = previewpicsfilter.Where(q => q.IsPreset || q.Atomtype == "scenes").ToList();
            string previewtype = "all";
            if (new string[8] { "scenes", "looks", "clothing", "hairstyle", "assets", "morphs", "pose", "skin" }.Contains(comboBoxPreviewType.Text))
                previewtype = comboBoxPreviewType.Text;
            if (previewtype != "all")
                previewpicsfilter = previewpicsfilter.Where(q => q.Atomtype == previewtype).ToList();
            mut.ReleaseMutex();
            listViewPreviewPics.VirtualListSize = previewpicsfilter.Count;
            listViewPreviewPics.Invalidate();
            toolStripLabelPreviewCountItem.Text = "/" + previewpicsfilter.Count.ToString();
            /*
            toolStripComboBoxPreviewPage.SelectedIndexChanged -= new System.EventHandler(this.toolStripComboBoxPreviewPage_SelectedIndexChanged); toolStripComboBoxPreviewPage.Items.Clear();
            previewPages = (previewpicsfilter.Count + maxpicxPerpage - 1) / maxpicxPerpage;
            toolStripLabelPreviewCountItem.Text = "/" + previewpicsfilter.Count.ToString();
            toolStripComboBoxPreviewPage.Items.Clear();
            if (previewPages >= 1)
            {
                for (int page = 0; page < previewPages; page++)
                {
                    int min = page * maxpicxPerpage + 1;
                    int max = (page + 1) * maxpicxPerpage;
                    if (max > previewpicsfilter.Count) max = previewpicsfilter.Count;

                    string strpage = min.ToString("000") + " - " + max.ToString("000");
                    toolStripComboBoxPreviewPage.Items.Add(strpage);
                }
                toolStripComboBoxPreviewPage.SelectedIndex = 0;
                //PreviewPage();
            }
            else
            {
                imageListPreviewPics.Images.Clear();
                listViewPreviewPics.Items.Clear();
            }
            toolStripComboBoxPreviewPage.SelectedIndexChanged += new System.EventHandler(this.toolStripComboBoxPreviewPage_SelectedIndexChanged);
            if (previewPages >= 1)
                PreviewPage();
            */
        }


        public delegate void InvokePreviewPics(string varname,
                                                string picpath,
                                                bool installed,
                                                string typename,
                                                string scenepath,
                                                bool ispreset);
        public void PreviewPics(string varname,
                                string picpath,
                                bool installed,
                                string typename,
                                string scenepath,
                                bool ispreset)
        {
            if (varname == "clear")
            {
                imageListPreviewPics.Images.Clear();
                listViewPreviewPics.Items.Clear();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(picpath))
                {
                    imageListPreviewPics.Images.Add(Image.FromFile("vam.png"));
                }
                else
                {
                    picpath = Path.Combine(Settings.Default.varspath, previewpicsDirName, typename, varname, picpath);
                    if (File.Exists(picpath))
                    {
                        imageListPreviewPics.Images.Add(Image.FromFile(picpath));
                    }
                    else
                    {
                        imageListPreviewPics.Images.Add(Image.FromFile("vam.png"));
                    }
                }
                var item = listViewPreviewPics.Items.Add(Path.GetFileNameWithoutExtension(picpath), imageListPreviewPics.Images.Count - 1);
                item.SubItems.Add(varname);
                item.SubItems.Add(picpath);
                item.SubItems.Add(installed.ToString());
                item.SubItems.Add(typename);
                item.SubItems.Add(scenepath);
                item.SubItems.Add(ispreset.ToString());
            }
        }

        private void listViewPreviewPics_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var curpriviewpic = previewpicsfilter[e.ItemIndex];
            string key = "vam.png";
            if (!string.IsNullOrWhiteSpace(curpriviewpic.Picpath))
            {
                string picpath= Path.Combine(Settings.Default.varspath, previewpicsDirName, curpriviewpic.Atomtype, curpriviewpic.Varname, curpriviewpic.Picpath);
                if (File.Exists(picpath))
                    key = picpath;
                else
                {
                    this.BeginInvoke(addlog, new Object[] { $"{picpath} is missing,Please run 'fix preview'", LogLevel.WARNING });
                    buttonFixPreview.Focus();
                }

            }
            if (!imageListPreviewPics.Images.ContainsKey(key))
            {
                imageListPreviewPics.Images.Add(key, LoadImageUnlocked(key));
                if (imageListPreviewPics.Images.Count > 100) imageListPreviewPics.Images.RemoveAt(0);
            }
            string itemname = Path.GetFileNameWithoutExtension(curpriviewpic.Picpath);
            if(string.IsNullOrEmpty(itemname))
            {
                itemname = curpriviewpic.Atomtype + "_" + Path.GetFileNameWithoutExtension(curpriviewpic.ScenePath);
            }
            e.Item = new ListViewItem(itemname, imageListPreviewPics.Images.IndexOfKey(key));
            e.Item.SubItems.Add(curpriviewpic.Varname);
            e.Item.SubItems.Add(key);
            e.Item.SubItems.Add(curpriviewpic.Installed.ToString());
            e.Item.SubItems.Add(curpriviewpic.Atomtype);
            e.Item.SubItems.Add(curpriviewpic.ScenePath);
            e.Item.SubItems.Add(curpriviewpic.IsPreset.ToString());

        }
        /*
        private void backgroundWorkerPreview_DoWork(object sender, DoWorkEventArgs e)
        {
            InvokePreviewPics previewpics = new InvokePreviewPics(PreviewPics);
            int startpic = maxpicxPerpage * previewCurPage;
            listViewPreviewPics.BeginInvoke(previewpics, "clear", "", true, "", "", true);
            for (int i = 0; i < maxpicxPerpage; i++)
            {
                Thread.Sleep(5);
                if (backgroundWorkerPreview.CancellationPending)
                {
                    //Tell the Backgroundworker you are canceling and exit the for-loop
                    e.Cancel = true;
                    return;
                }
                mut.WaitOne();
                if (previewpicsfilter.Count > startpic + i)
                {
                    this.BeginInvoke(previewpics,
                                        previewpicsfilter[startpic + i].Varname,
                                        previewpicsfilter[startpic + i].Picpath,
                                        previewpicsfilter[startpic + i].Installed,
                                        previewpicsfilter[startpic + i].Atomtype,
                                        previewpicsfilter[startpic + i].ScenePath,
                                        previewpicsfilter[startpic + i].IsPreset
                                                );

                }
                mut.ReleaseMutex();
            }
        }
        */

        private void toolStripComboBoxPreviewType_SelectedIndexChanged(object sender, EventArgs e)
        {
            PreviewInitType();
        }
        /*
        private void toolStripComboBoxPreviewPage_SelectedIndexChanged(object sender, EventArgs e)
        {
            PreviewPage();
        }

        private void PreviewPage()
        {
            previewCurPage = toolStripComboBoxPreviewPage.SelectedIndex;
            while (backgroundWorkerPreview.IsBusy)
            {
                backgroundWorkerPreview.CancelAsync();
                // Keep UI messages moving, so the form remains 
                // responsive during the asynchronous operation.
                Application.DoEvents();
            }
            backgroundWorkerPreview.RunWorkerAsync();
        }
        */
        private void toolStripButtonPreviewFirst_Click(object sender, EventArgs e)
        {
            int selectindex = 0;
            listViewPreviewPics.Items[selectindex].Selected = true;
            listViewPreviewPics.EnsureVisible(selectindex);
           // if (toolStripComboBoxPreviewPage.SelectedIndex > 0) toolStripComboBoxPreviewPage.SelectedIndex = 0;
        }

        private void toolStripButtonPreviewPrev_Click(object sender, EventArgs e)
        {
            int selectindex = 0;
            if (listViewPreviewPics.SelectedIndices.Count >= 1)
            {
                int index = listViewPreviewPics.SelectedIndices[0];
                if (index > 0) selectindex = index - 1;
            }

            listViewPreviewPics.Items[selectindex].Selected = true;
            listViewPreviewPics.EnsureVisible(selectindex);
            // if (toolStripComboBoxPreviewPage.SelectedIndex > 0) toolStripComboBoxPreviewPage.SelectedIndex--;
        }

        private void toolStripButtonPreviewNext_Click(object sender, EventArgs e)
        {
            int selectindex = listViewPreviewPics.Items.Count-1;
            if (listViewPreviewPics.SelectedIndices.Count >= 1)
            {
                int index = listViewPreviewPics.SelectedIndices[0];
                if (index < listViewPreviewPics.Items.Count - 1) selectindex = index + 1;
            }

            listViewPreviewPics.Items[selectindex].Selected = true;
            listViewPreviewPics.EnsureVisible(selectindex);
            // if (toolStripComboBoxPreviewPage.SelectedIndex < toolStripComboBoxPreviewPage.Items.Count - 1) toolStripComboBoxPreviewPage.SelectedIndex++;
        }

        private void toolStripButtonPreviewLast_Click(object sender, EventArgs e)
        {
            int selectindex = listViewPreviewPics.Items.Count - 1;
            listViewPreviewPics.Items[selectindex].Selected = true;
            listViewPreviewPics.EnsureVisible(selectindex);
            // if (toolStripComboBoxPreviewPage.SelectedIndex < toolStripComboBoxPreviewPage.Items.Count - 1) toolStripComboBoxPreviewPage.SelectedIndex = toolStripComboBoxPreviewPage.Items.Count - 1;

        }

        private void buttonStaleVars_Click(object sender, EventArgs e)
        {
            /*
            string message = $"Stale var means:\r\n1,The version is not the latest.\r\n2,Not depended by other var.\r\nThey will be moved to the {staleVarsDirName} directory";

            const string caption = "StaleVars";
            var result = MessageBox.Show(message, caption,
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1);
            */
            FormStaleVars formStaleVars = new FormStaleVars();
            if (formStaleVars.ShowDialog() == DialogResult.OK)
            {
                if (formStaleVars.removeOldVersion)
                    StartBackgroundJob("OldVersionVars");
                else
                    StartBackgroundJob("StaleVars");
            }
        }

        private static List<VarEntry> VarEntries(IEnumerable<varManagerDataSet.varsRow> rows)
        {
            return rows.Select(q => new VarEntry(q.varName, q.creatorName, q.packageName, q.version)).ToList();
        }

        void StaleVars()
        {
            List<VarEntry> entries = VarEntries(varManagerDataSet.vars);
            List<string> listOldvar = DependencyAnalysis.OldVersions(entries, DependencyAnalysis.LatestVersions(entries));
            string stalepath = Path.Combine(Settings.Default.varspath, staleVarsDirName);
            if (!Directory.Exists(stalepath))
                Directory.CreateDirectory(stalepath);
            var installedvars = GetInstalledVars();
            foreach (var oldvar in listOldvar)
            {
                if (DependencyLookup.DependentsOf(oldvar).Count == 0 && DependentSaved(oldvar).Count == 0)
                {
                    if (installedvars.ContainsKey(oldvar))
                    {
                        File.Delete(installedvars[oldvar]);
                    }
                    //string linkvar = Path.Combine(Settings.Default.vampath, "AddonPackages", installLinkDirName, oldvar + ".var");
                    //if (File.Exists(linkvar))
                    //    File.Delete(linkvar);
                    string oldv = Path.Combine(Settings.Default.varspath, varManagerDataSet.vars.FindByvarName(oldvar).varPath, oldvar + ".var");
                    string stalev = Path.Combine(stalepath, oldvar + ".var");
                    try
                    {
                        this.BeginInvoke(addlog, new Object[] { $"move {oldv} to {stalepath}.", LogLevel.INFO });
                        File.Move(oldv, stalev);
                        CleanVar(oldvar);
                    }
                    catch (Exception ex)
                    {
                        this.BeginInvoke(addlog, new Object[] { $"{oldv} move failed,{ex.Message}", LogLevel.ERROR });
                    }
                }
            }
            System.Diagnostics.Process.Start(stalepath);
            //MessageBox.Show("Please run upd-db once");
        }

        void OldVersionVars()
        {
            var versionLastest = DependencyAnalysis.LatestVersions(
                VarEntries(varManagerDataSet.vars.Where(q => q.plugins <= 0 || q.scenes > 0 || q.looks > 0)));
            List<string> listOldvar = DependencyAnalysis.OldVersions(VarEntries(varManagerDataSet.vars), versionLastest);
            string oldversionpath = Path.Combine(Settings.Default.varspath, oldVersionVarsDirName);
            if (!Directory.Exists(oldversionpath))
                Directory.CreateDirectory(oldversionpath);
            var installedvars = GetInstalledVars();
            foreach (var oldvar in listOldvar)
            {
                if (installedvars.ContainsKey(oldvar))
                {
                    string basename = oldvar.Substring(0, oldvar.LastIndexOf(".") );
                    string varlastest = basename + "." + versionLastest[basename];
                    File.Delete(installedvars[oldvar]);
                    VarInstall(varlastest);
                }
                //string linkvar = Path.Combine(Settings.Default.vampath, "AddonPackages", installLinkDirName, oldvar + ".var");
                //if (File.Exists(linkvar))
                //    File.Delete(linkvar);
                string oldv = Path.Combine(Settings.Default.varspath, varManagerDataSet.vars.FindByvarName(oldvar).varPath, oldvar + ".var");
                string oldversionv = Path.Combine(oldversionpath, oldvar + ".var");
                try
                {
                    this.BeginInvoke(addlog, new Object[] { $"move {oldv} to {oldversionpath}.", LogLevel.INFO });
                    File.Move(oldv, oldversionv);
                    CleanVar(oldvar);
                }
                catch (Exception ex)
                {
                    this.BeginInvoke(addlog, new Object[] { $"{oldv} move failed,{ex.Message}", LogLevel.ERROR });
                }

            }
            System.Diagnostics.Process.Start(oldversionpath);
            //MessageBox.Show("Please run upd-db once");
        }

        private void textBoxFilter_TextChanged(object sender, EventArgs e)
        {
            FilterVars();
        }

        private void checkBoxInstalled_CheckStateChanged(object sender, EventArgs e)
        {
            FilterVars();
        }

        private void varsViewDataGridView_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (varsViewDataGridView.Columns[e.ColumnIndex].Name == "installedDataGridViewCheckBoxColumn" && e.RowIndex >= 0)
            {
                string varName = varsViewDataGridView.Rows[e.RowIndex].Cells["varNameDataGridViewTextBoxColumn"].Value.ToString();
                bool installed = false;
                var row = varManagerDataSet.installStatus.FindByvarName(varName);
                if (row != null)
                {
                    installed = row.Installed;
                }
                if (installed)
                {
                    string message = varName + " will be removed, are you sure?";
                    string caption = "Remove Var";
                    var result = MessageBox.Show(message, caption,
                                          MessageBoxButtons.YesNo,
                                          MessageBoxIcon.Question,
                                          MessageBoxDefaultButton.Button2);
                    if (result == DialogResult.Yes)
                    {
                        List<string> varnames = new List<string>();
                        varnames.Add(varName);
                        UnintallVars(varnames);
                        UpdateVarsInstalled();
                        RescanPackages();
                    }

                }
                else
                {
                    string message = varName + "  will be installed, are you sure?";
                    string caption = "Install Var";
                    var result = MessageBox.Show(message, caption,
                                          MessageBoxButtons.YesNo,
                                          MessageBoxIcon.Question,
                                          MessageBoxDefaultButton.Button2);
                    if (result == DialogResult.Yes)
                    {
                        List<string> varnames = new List<string>();
                        varnames.Add(varName);
                        varnames = VarsDependencies(varnames);
                        foreach (string varname in varnames)
                        {
                            VarInstall(varname);
                        }
                        UpdateVarsInstalled();
                        RescanPackages();
                    }
                }
            }
            if (varsViewDataGridView.Columns[e.ColumnIndex].Name == "ColumnDetail" && e.RowIndex >= 0)
            {
                string varName = varsViewDataGridView.Rows[e.RowIndex].Cells["varNameDataGridViewTextBoxColumn"].Value.ToString();
                VarDetail(varName);
            }
        }

        public bool IsVarInstalled(string varName)
        {
            var installstatusrow = varManagerDataSet.installStatus.FindByvarName(varName);
            if (installstatusrow!=null)
                return installstatusrow.Installed;
            else
                return false;
        }
        private void VarDetail(string varName)
        {
            FormVarDetail formVarDetail = new FormVarDetail();
            formVarDetail.form1 = this;
            formVarDetail.strVarName = varName;

            formVarDetail.dependencies = new Dictionary<string, string>();
            formVarDetail.dependencyStatus = new Dictionary<string, DependencyStatus>();
            foreach (string dependency in DependencyLookup.DependenciesOf(varName))
            {
                DependencyResolution resolution = VarLookup.ResolveReference(dependency);
                formVarDetail.dependencies[dependency] = resolution.VarName ?? VarIndex.Missing;
                formVarDetail.dependencyStatus[dependency] = resolution.Status;
            }
            formVarDetail.DependentVarList = DependentVars(varName);
            formVarDetail.DependentJsonList = DependentSaved(varName);
            if (formVarDetail.ShowDialog() == DialogResult.OK)
            {
                if (formVarDetail.strAction == "filter")
                {
                    string creator = varName.Substring(0, varName.IndexOf("."));
                    comboBoxCreater.Text = creator;
                }
            }
        }

        private void buttonInstall_Click(object sender, EventArgs e)
        {
            List<string> varNames = new List<string>();
            foreach (DataGridViewRow row in varsViewDataGridView.SelectedRows)
            {
                string varName = row.Cells["varNameDataGridViewTextBoxColumn"].Value.ToString();
                bool install = false;
                var varsrow = varManagerDataSet.installStatus.FindByvarName(varName);
                if (varsrow != null)
                {
                    if (varsrow.Installed)
                    {
                        install = true; ;
                    }
                }
                if (!install)
                {
                    varNames.Add(varName);
                }
            }
            if (varNames.Count <= 0) return;
            int max = 500;
            if (varNames.Count > max)
            {
                MessageBox.Show($"Please do not install more than {max} files at once");
                return;
            }
            string message = $"There are {varNames.Count} vars and their dependencies will be installed, are you sure?";
            string caption = "Install Var";
            var result = MessageBox.Show(message, caption,
                                  MessageBoxButtons.YesNo,
                                  MessageBoxIcon.Question,
                                  MessageBoxDefaultButton.Button2);
            if (result == DialogResult.Yes)
            {
                varNames = VarsDependencies(varNames);
                foreach (string varname in varNames)
                {
                    VarInstall(varname);
                }
                UpdateVarsInstalled();
            }
        }
        public List<string> GetDependents(string dependName)
        {
            List<string> result = new List<string>();
            result.AddRange(DependencyLookup.DependentsOf(dependName));
            foreach (var dependrow in varManagerDataSet.savedepens.Where(q => q.dependency == dependName))
            {
                result.Add(dependrow.savepath);
            }
            return result;

        }

        private void buttonpreviewback_Click(object sender, EventArgs e)
        {
            tableLayoutPanelPreview.Visible = false;
        }

        private void buttonScenesManager_Click(object sender, EventArgs e)
        {
            FormScenes formScenes = new FormScenes();
            formScenes.form1 = this;
            formScenes.Show();
        }

        private void pictureBoxPreview_Click(object sender, EventArgs e)
        {
            tableLayoutPanelPreview.Visible = false;
        }
        private string curVarName = "",curEntryName="";
        private JSONClass jsonLoadScene;
        private void listViewPreviewPics_Click(object sender, EventArgs e)
        {
            if (listViewPreviewPics.SelectedIndices.Count >= 1)
            {
                tableLayoutPanelPreview.Dock = DockStyle.Fill;
                tableLayoutPanelPreview.Visible = true;
                int index = listViewPreviewPics.SelectedIndices[0];
                toolStripLabelPreviewItemIndex.Text = index.ToString();
                var item = listViewPreviewPics.Items[index];
                if (item != null)
                {
                    curVarName = item.SubItems[1].Text;
                    curEntryName = item.SubItems[5].Text;
                    labelPreviewVarName.Text = curVarName;
                    if (string.IsNullOrEmpty(item.SubItems[2].Text))
                        pictureBoxPreview.Image = pictureBoxPreview.Image = Image.FromFile("vam.png");
                    else
                        pictureBoxPreview.Image = Image.FromFile(item.SubItems[2].Text);

                    if (item.SubItems[3].Text.ToLower() == "true")
                    {
                        buttonpreviewinstall.Text = "Uninstall";
                        buttonpreviewinstall.ForeColor = Color.Red;
                    }

                    else
                    {
                        buttonpreviewinstall.Text = "Install";
                        buttonpreviewinstall.ForeColor = Color.DarkBlue;
                    }

                    if (item.SubItems[4].Text.ToLower() == "scenes" || item.SubItems[6].Text.ToLower() == "true")
                    {
                        buttonLoad.Visible = true;
                        checkBoxMerge.Visible = true;
                        checkBoxMerge.Checked = false;
                        buttonLoad.Text = "Load " + item.SubItems[4].Text;
                        string rescan = "false";
                        if (item.SubItems[3].Text.ToLower() == "false")
                            rescan = "true";

                        jsonLoadScene = new JSONClass();
                        jsonLoadScene.Add("rescan", rescan);

                        jsonLoadScene.Add("resources", new JSONArray());
                        JSONArray resources = jsonLoadScene["resources"].AsArray;
                        resources.Add(new JSONClass());
                        JSONClass resource = (JSONClass)resources[resources.Count - 1];
                        resource.Add("type", item.SubItems[4].Text.ToLower());
                        resource.Add("saveName", curVarName + ":/" + curEntryName.Replace('\\', '/'));
                        UpdateButtonClearCache();

                        if (item.SubItems[4].Text.ToLower() == "scenes" || item.SubItems[4].Text.ToLower() == "looks")
                        {
                            buttonAnalysis.Visible = true;
                        }
                        else
                        {
                            buttonAnalysis.Visible = false;
                        }
                        if (item.SubItems[4].Text.ToLower() == "looks" || item.SubItems[4].Text.ToLower() == "clothing" ||
                           item.SubItems[4].Text.ToLower() == "morphs" || item.SubItems[4].Text.ToLower() == "hairstyle" ||
                           item.SubItems[4].Text.ToLower() == "skin" || item.SubItems[4].Text.ToLower() == "pose")
                        {
                            groupBoxPersonOrder.Visible = true;
                            checkBoxIgnoreGender.Visible = true;
                        }
                        else
                        {
                            groupBoxPersonOrder.Visible = false;
                            checkBoxIgnoreGender.Visible = false;
                        }
                        if (item.SubItems[4].Text.ToLower() == "morphs" ||
                           item.SubItems[4].Text.ToLower() == "skin" ||
                           item.SubItems[4].Text.ToLower() == "pose")
                        {
                            checkBoxForMale.Visible = true;
                        }
                        else
                        {
                            checkBoxForMale.Visible = false;
                        }
                    }
                    else
                    {
                        buttonLoad.Visible = false;
                        checkBoxMerge.Visible = false;
                        buttonAnalysis.Visible = false;
                    }
                }
            }
        }

        private void UpdateButtonClearCache()
        {
            string sceneCacheFolderName = SceneAnalysis.CacheFolder(curVarName, curEntryName);
            if (Directory.Exists(sceneCacheFolderName))
            {
                buttonClearCache.Visible = true;
            }
            else
            {
                buttonClearCache.Visible = false;
            }
        }

        private static string GetKnownFolderPath(Guid knownFolderId)
        {
            IntPtr pszPath = IntPtr.Zero;
            try
            {
                int hr = SHGetKnownFolderPath(knownFolderId, 0, IntPtr.Zero, out pszPath);
                if (hr >= 0)
                    return Marshal.PtrToStringAuto(pszPath);
                throw Marshal.GetExceptionForHR(hr);
            }
            finally
            {
                if (pszPath != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(pszPath);
            }
        }

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr pszPath);

        private void buttonLogAnalysis_Click(object sender, EventArgs e)
        {
            string message = "Analyzing dependencies from log file, otherwise a processing window will be opened.\r\nThis feature requires the game to be closed";

            const string caption = "LogAnalysis";
            var result = MessageBox.Show(message, caption,
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1);
            if (result == DialogResult.Yes)
            {
                StartBackgroundJob("LogAnalysis");
            }
        }

        private void LogAnalysis()
        {
            Guid localLowId = new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16");
            string appdataPath = GetKnownFolderPath(localLowId);
            string logfile = Path.Combine(appdataPath, "MeshedVR\\VaM\\output_log.txt");
            if (File.Exists(logfile))
            {
                var metajsonsteam = new StreamReader(logfile);
                string logstring = metajsonsteam.ReadToEnd();
                metajsonsteam.Close();
                System.Diagnostics.Process.Start(logfile);
                List<string> dependencies = new List<string>();
                try
                {
                    Regex regexObj = new Regex(@"Missing\s+addon\s+package\s+(?<depens>[^\x3A\x2E]{1,60}\x2E[^\x3A\x2E]{1,80}\x2E(?:\d+|latest))\s+that\s+package(?<package>[^\x3A\x2E]{1,60}\x2E[^\x3A\x2E]{1,80}\x2E\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    Match matchResult = regexObj.Match(logstring);
                    while (matchResult.Success)
                    {
                        dependencies.Add(matchResult.Groups["depens"].Value);
                        matchResult = matchResult.NextMatch();
                    }
                }
                catch (ArgumentException ex)
                {
                    this.BeginInvoke(addlog, new Object[] { "LogAnalysis failed" + ex.Message, LogLevel.ERROR });
                }
                dependencies = dependencies.Distinct().ToList();
                List<string> missingvars = new List<string>();
                foreach (string varname in dependencies)
                {
                    string varexistname = VarExistName(varname);
                    if (varexistname.EndsWith("$"))
                    {
                        varexistname = varexistname.Substring(0, varexistname.Length - 1);
                        missingvars.Add(varname + "$");
                    }
                    if (varexistname != "missing")
                    {
                        VarInstall(varexistname);
                        this.BeginInvoke(addlog, new Object[] { varexistname + " installed", LogLevel.INFO });
                    }
                    else
                    {
                        missingvars.Add(varname);
                    }
                }
                if (missingvars.Count > 0)
                {
                    InvokeShowformMissingVars showformMissingVars = new InvokeShowformMissingVars(ShowformMissingVars);
                    this.BeginInvoke(showformMissingVars, missingvars);
                }
            }
        }

        private void varsViewDataGridView_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            this.BeginInvoke(addlog, new Object[] { $"varsViewDataGridView_DataError, {e.Exception.Message}", LogLevel.ERROR });
        }

        private void buttonUninstallSels_Click(object sender, EventArgs e)
        {
            List<string> varNames = new List<string>();
            foreach (DataGridViewRow row in varsViewDataGridView.SelectedRows)
            {
                string varName = row.Cells["varNameDataGridViewTextBoxColumn"].Value.ToString();
                var varsrow = varManagerDataSet.installStatus.FindByvarName(varName);
                if (varsrow != null)
                {
                    if (varsrow.Installed)
                    {
                        varNames.Add(varName);
                    }
                }
            }
            if (varNames.Count <= 0) return;
            int max = 500;
            if (varNames.Count > max)
            {
                MessageBox.Show($"Please do not uninstall more than {max} files at once");
                return;
            }
            string message = $"There are {varNames.Count} vars and their dependencies will be Uninstall, are you sure?";
            string caption = "Uninstall Vars";
            var result = MessageBox.Show(message, caption,
                                  MessageBoxButtons.YesNo,
                                  MessageBoxIcon.Question,
                                  MessageBoxDefaultButton.Button2);
            if (result == DialogResult.Yes)
            {
                UnintallVars(varNames);
                UpdateVarsInstalled();
            }
        }

        private void buttonDelete_Click(object sender, EventArgs e)
        {
            List<string> varNames = new List<string>();
            foreach (DataGridViewRow row in varsViewDataGridView.SelectedRows)
            {
                string varName = row.Cells["varNameDataGridViewTextBoxColumn"].Value.ToString();

                varNames.Add(varName);
            }
            if (varNames.Count <= 0) return;
            int max = 50;
            if (varNames.Count > max)
            {
                MessageBox.Show($"Please do not delete more than {max} files at once");
                return;
            }
            string message = $"There are {varNames.Count} vars and their dependencies will be delete, are you sure?";
            string caption = "Delete Vars";
            var result = MessageBox.Show(message, caption,
                                  MessageBoxButtons.YesNo,
                                  MessageBoxIcon.Question,
                                  MessageBoxDefaultButton.Button2);
            if (result == DialogResult.Yes)
            {
                DeleteVars(varNames);
                UpdateVarsInstalled();
                RescanPackages();
            }
        }

        private void buttonMove_Click(object sender, EventArgs e)
        {
            List<string> varNames = new List<string>();
            foreach (DataGridViewRow row in varsViewDataGridView.SelectedRows)
            {
                string varName = row.Cells["varNameDataGridViewTextBoxColumn"].Value.ToString();
                var varsrow = varManagerDataSet.installStatus.FindByvarName(varName);
                if (varsrow != null)
                {
                    if (varsrow.Installed)
                    {
                        varNames.Add(varName);
                    }
                }
            }
            if (varNames.Count <= 0) return;
            FormVarsMove fvm = new FormVarsMove();
            fvm.VarlinkDirName = installLinkDirName;
            fvm.VarsToMove = varNames;
            if (fvm.ShowDialog() == DialogResult.OK)
            {
                string movetovarspath = Path.Combine(Settings.Default.vampath, "AddonPackages", installLinkDirName, fvm.MovetoDirName);
                if (!Directory.Exists(movetovarspath))
                    Directory.CreateDirectory(movetovarspath);

                foreach (string varname in varNames)
                {
                    string[] operalinks = Directory.GetFiles(Path.Combine(Settings.Default.vampath, "AddonPackages", installLinkDirName), varname + ".var", SearchOption.AllDirectories);
                    if (operalinks.Length > 0)
                    {
                        string operav = operalinks[0];
                        string movetodv = Path.Combine(movetovarspath, varname + ".var");
                        if (!File.Exists(movetodv))
                        {
                            try
                            {
                                File.Move(operav, movetodv);
                                //CleanVar(varname);
                            }
                            catch (Exception ex)
                            {
                                this.BeginInvoke(addlog, new Object[] { $"{operav} move failed,{ex.Message}", LogLevel.ERROR });
                            }
                        }
                    }
                }
            }

        }

        private void buttonExpInsted_Click(object sender, EventArgs e)
        {
            if (saveFileDialogExportInstalled.ShowDialog() == DialogResult.OK)
            {
                List<string> varNames = new List<string>();
                foreach (var varstatus in this.varManagerDataSet.installStatus)
                {
                    if (varstatus.Installed) varNames.Add(varstatus.varName);
                }
                File.WriteAllLines(saveFileDialogExportInstalled.FileName, varNames.ToArray());
            }
        }

        private void buttonInstFormTxt_Click(object sender, EventArgs e)
        {
            if (openFileDialogInstByTXT.ShowDialog() == DialogResult.OK)
            {
                string[] varNames = File.ReadAllLines(openFileDialogInstByTXT.FileName);
                foreach (string varname in varNames)
                {
                    VarInstall(varname);
                }
                UpdateVarsInstalled();
                RescanPackages();
            }
        }

        private void comboBoxPacksSwitch_SelectedIndexChanged(object sender, EventArgs e)
        {
            
            string sw = (string)comboBoxPacksSwitch.SelectedItem;
            if (!string.IsNullOrEmpty(sw))
            {
                buttonPacksDelete.Enabled = (sw != "default");
                varpacksSwitch(sw);

            }
            else
            {
                buttonPacksDelete.Enabled = false;
            }
        }

        private void varpacksSwitch(string sw)
        {
            this.BeginInvoke(addlog, new Object[] { $"Point the Addonpackages symbo-link to '{sw}'", LogLevel.INFO });
            int errorCode;
            SwitchChange change = VarSwitches.Point(Settings.Default.vampath, sw, out errorCode);
            if (change == SwitchChange.Failed)
            {
                MessageBox.Show("Error: create AddonPackages symbolic link. " +
                        "(Error Code: " + errorCode + ")");
            }
            else if (change == SwitchChange.Switched)
            {
                UpdateVarsInstalled();
                RescanPackages();
            }
        }

        private static void RescanPackages()
        {
            VamBridge.RequestRescan(Settings.Default.vampath);
        }

        private void buttonPacksAdd_Click(object sender, EventArgs e)
        {
            FormSwitchAdd formSwitchAdd = new FormSwitchAdd();
            if (formSwitchAdd.ShowDialog() == DialogResult.OK)
            {
                if (comboBoxPacksSwitch.Items.IndexOf(formSwitchAdd.SwitchName) < 0)
                {
                    comboBoxPacksSwitch.Items.Add(formSwitchAdd.SwitchName);
                    comboBoxPacksSwitch.SelectedItem = formSwitchAdd.SwitchName;
                }
                else
                {
                    MessageBox.Show("Switch space already exists!");
                }

            }
        }

        private void buttonPacksDelete_Click(object sender, EventArgs e)
        {
            string curswitch = (string)comboBoxPacksSwitch.SelectedItem;
            if (!string.IsNullOrEmpty(curswitch) && curswitch != "default")
            {
                if (MessageBox.Show($"Will delete {curswitch} AddonPackagesSwitch, sure?", "delete switch", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    VarSwitches.Delete(Settings.Default.vampath, curswitch);
                    LeanProfiles.DeleteFiles(ProfilePath(curswitch));
                    comboBoxPacksSwitch.Items.Remove(curswitch);
                    comboBoxPacksSwitch.SelectedItem = "default";
                }
            }

        }

        private void buttonPacksRename_Click(object sender, EventArgs e)
        {
            string curswitch = (string)comboBoxPacksSwitch.SelectedItem;
            if (!string.IsNullOrEmpty(curswitch) && curswitch != "default")
            {
                FormSwitchRename formSwitchRename = new FormSwitchRename();
                formSwitchRename.OldName = curswitch;
                if (formSwitchRename.ShowDialog() == DialogResult.OK)
                {
                    string newName = formSwitchRename.NewName;
                    VarSwitches.Rename(Settings.Default.vampath, curswitch, newName);
                    comboBoxPacksSwitch.Items[comboBoxPacksSwitch.SelectedIndex] = newName;
                    try
                    {
                        this.varpacksSwitch(newName);
                    }
                    finally
                    {
                        LeanProfiles.RenameFiles(ProfilePath(curswitch), ProfilePath(newName));
                    }
                }
            }
        }
        private void buttonLoad_Click(object sender, EventArgs e)
        {
            bool merge = false;
            if (checkBoxMerge.Checked) merge = true;
            bool ignoreGender = false;
            if (checkBoxIgnoreGender.Checked) ignoreGender = true;
            string characterGender = "unknown";
            if (checkBoxForMale.Visible)
            {
                if (checkBoxForMale.Checked) characterGender = "male";
                else characterGender = "female";
            }
            int personOrder = 1;
            foreach (RadioButton rbperson in groupBoxPersonOrder.Controls)
            {
                if (rbperson.Checked)
                {
                    personOrder = int.Parse(rbperson.Text);
                    break;
                }
            }
            tableLayoutPanelPreview.Visible = false;
            Cursor = Cursors.WaitCursor;
            LoadScene(jsonLoadScene, merge, ignoreGender, characterGender, personOrder);
            Cursor = Cursors.Arrow;
            UpdateButtonClearCache();
        }

        public void LoadScene(JSONClass jc,bool merge, bool ignoreGender, string characterGender, int personOrder)
        {
            JSONArray resources = jc["resources"].AsArray;
            string saveName = "";

            if (resources.Count > 0)
            {
                JSONClass resource = (JSONClass)resources[0];
                saveName = resource["saveName"].Value;
                (string varName, string entryName) = SceneAnalysis.SplitSaveName(saveName);
                string sceneFolder = SceneAnalysis.CacheFolder(varName, entryName);
                string dependfFilename = Path.Combine(sceneFolder, "depend.txt");
                if (!File.Exists(dependfFilename))
                {
                    ReadSaveName(saveName, characterGender);
                }
                GenLoadscenetxt(jc, merge, SceneAnalysis.ReadDepends(sceneFolder), SceneAnalysis.ReadGender(sceneFolder), ignoreGender, personOrder);
            }
        }

        public bool FindByvarName(string varName)
        {
            bool inRepository = false;
            if (varName.EndsWith(".var"))
                varName = varName.Substring(0, varName.Length - 4);
            if (varManagerDataSet.vars.FindByvarName(varName) != null)
            {
                inRepository = true;
            }
            return inRepository;
        }
        public void GenLoadscenetxt(JSONClass jsonLS,bool merge, List<string> dependVars,string characterGender="female", bool ignoreGender = false,int personOrder=1 )
        {
            bool hasScene;
            JSONClass jsonls = VamBridge.PrepareLoad(jsonLS, merge, characterGender, ignoreGender, personOrder, out hasScene);
            List<string> deletetempfiles = hasScene ? VarLinks.TempLinks(AddonPackagesPath) : new List<string>();
            if (dependVars == null)
                dependVars = VamBridge.ResourceVars(jsonls);
            bool rescan = false;
            var installtemplist = InstallTemp(dependVars.ToArray(), ref rescan);
            jsonls["rescan"] = rescan.ToString();
            foreach (var installtemp in installtemplist)
                deletetempfiles.Remove(installtemp.ToLower() + ".var");
            VamBridge.WriteRequest(Settings.Default.vampath, jsonls);
            if (deletetempfiles.Count > 0)
            {
                Thread thread = new Thread(DeleteTempThread);
                thread.Start(deletetempfiles);
            }
        }

        public static void DeleteTempThread(object data)
        {
            List<string> tempfiles = data as List<string>;
            string loadscenefile = VamBridge.RequestFile(Settings.Default.vampath);

            while (true)
            {
                Thread.Sleep(2000);
                if (File.Exists(loadscenefile))
                {
                    continue;
                }
                else
                {
                    Thread.Sleep(20000);
                    VarLinks.RemoveTempLinks(AddonPackagesPath, tempfiles);
                    break;
                }
            }
        }

        public List<string> InstallTemp(string[] varNames,ref bool rescan)
        {
            rescan = false;
            List<string> varnames = new List<string>();
            varnames.AddRange(varNames);
            varnames = VarsDependencies(varnames);
            varnames = varnames.Except(GetInstalledVars().Keys).ToList();
            DirectoryInfo templinkdirinfo = Directory.CreateDirectory(Path.Combine(Settings.Default.vampath, "AddonPackages", tempVarLinkDirName));

            foreach (string varname in varnames)
            {
                var rows = varManagerDataSet.varsView.Where(q => q.varName == varname);
                if (rows.Count() > 0)
                {
                    if (!rows.First().Installed)
                        if (VarInstall(varname, true) == VarLinkResult.Linked)
                            rescan = true;
                }
                else
                {
                    this.BeginInvoke(addlog, new Object[] { string.Format("missing var:{0},install failed", varname), LogLevel.INFO });
                }
            }
            return varnames;
            //UpdateVarsInstalled();
        }

        private void checkBoxPreviewTypeLoadable_CheckedChanged(object sender, EventArgs e)
        {
            PreviewInitType();
        }

        private void buttonLocate_Click(object sender, EventArgs e)
        {
            string varName = labelPreviewVarName.Text;
            LocateVar(varName);
        }

        public void LocateVar(string varName)
        {
            varManagerDataSet.varsRow varsrow = varManagerDataSet.vars.FindByvarName(varName);
            if (varsrow != null)
            {
                string destvarfile = Path.Combine(Settings.Default.varspath, varsrow.varPath, varsrow.varName + ".var");
                Comm.LocateFile(destvarfile);
            }
        }

        private void buttonpreviewinstall_Click(object sender, EventArgs e)
        {
            string varName = labelPreviewVarName.Text;
            if (varManagerDataSet.installStatus.Where(q => q.varName == varName && q.Installed).Count() > 0)
            {
                string message = varName + "  will be remove, are you sure?";
                string caption = "Remove Var";
                var result = MessageBox.Show(message, caption,
                                      MessageBoxButtons.YesNo,
                                      MessageBoxIcon.Question,
                                      MessageBoxDefaultButton.Button2);
                if (result == DialogResult.Yes)
                {
                    List<string> varnames = new List<string>();
                    varnames.Add(varName);
                    UnintallVars(varnames);
                }
            }
            else
            {
                string message = varName + "  will install, are you sure?";
                string caption = "Install Var";
                var result = MessageBox.Show(message, caption,
                                      MessageBoxButtons.YesNo,
                                      MessageBoxIcon.Question,
                                      MessageBoxDefaultButton.Button2);
                if (result == DialogResult.Yes)
                {
                    List<string> varnames = new List<string>();
                    varnames.Add(varName);
                    varnames = VarsDependencies(varnames);
                    foreach (string varname in varnames)
                    {
                        VarInstall(varname);
                    }

                }
            }
            UpdateVarsInstalled();
            RescanPackages();
        }

        private void buttonAnalysis_Click(object sender, EventArgs e)
        {
            Analysisscene(jsonLoadScene);
            UpdateButtonClearCache();
        }

        public void Analysisscene(JSONClass jsonLS)
        {
            JSONClass jsonls = (JSONClass)JSONNode.Parse(jsonLS.ToString());
            JSONArray resources = jsonls["resources"].AsArray;
            string saveName = "";
            if (resources.Count > 0)
            {
                JSONClass resource = (JSONClass)resources[0];
                saveName = resource["saveName"].Value;
                (string varName, string entryName) = SceneAnalysis.SplitSaveName(saveName);
                string sceneFolder = SceneAnalysis.CacheFolder(varName, entryName);
                string atomsFoldername = Path.Combine(sceneFolder, "atoms");

                if (!Directory.Exists(atomsFoldername))
                {
                    ReadSaveName(saveName, "female",true);
                }
                
                FormAnalysis formAnalysis = new FormAnalysis();
                formAnalysis.form1 = this;
                formAnalysis.VarName = varName;
                formAnalysis.EntryName = entryName;
                if (formAnalysis.ShowDialog() == DialogResult.OK)
                {
                    
                }
            }
            

        }
        public bool ReadSaveName(string saveName,string characterGender,bool analysis=false)
        {
            UseWaitCursor = true;
            string destvarfile = null;
            (string varName, string entryName) = SceneAnalysis.SplitSaveName(saveName);
            if (varName!="save")
            {
                var varsrow = varManagerDataSet.vars.FindByvarName(varName);
                destvarfile = Path.Combine(Settings.Default.varspath, varsrow.varPath, varsrow.varName + ".var");
            }
            SceneAnalysis.Prepare(saveName, destvarfile, Settings.Default.vampath, characterGender, analysis);
            UseWaitCursor = false;
            return true;
        }

        private void buttonResetFilter_Click(object sender, EventArgs e)
        {
            ResetFilter();
        }

        private void ResetFilter()
        {
            comboBoxCreater.SelectedItem = "____ALL";
            textBoxFilter.Text = "";
            dgvFilterManager.ActivateAllFilters(false);
        }

        private void SwitchControl_Enter(object sender, EventArgs e)
        {
            if (ExistAddonpackagesVar())
            {
                if (MessageBox.Show("There are unorganized var files in the current switch, please run UPD_DB first", "warning", MessageBoxButtons.OKCancel) == DialogResult.OK)
                    buttonUpdDB.Focus();
            }
        }

        private void buttonFixPreview_Click(object sender, EventArgs e)
        {
            string message = "Missing preview images will be detected and re-extracted.";

            const string caption = "RebuildLink";
            var result = MessageBox.Show(message, caption,
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1);
            if (result == DialogResult.Yes)
                StartBackgroundJob("fixPreview");
        }

        private void buttonHub_Click(object sender, EventArgs e)
        {
            FormHub formhub = new FormHub();
            formhub.form1 = this;
            //formhub.VarManagerDataSet = this.varManagerDataSet;
            formhub.Show();
        }
        public void SelectVarInList(string varname)
        {
            ResetFilter();

            int firstindex = int.MaxValue;
            varsViewDataGridView.ClearSelection();
            foreach (DataGridViewRow row in varsViewDataGridView.Rows)
            {
                string varnameinrow = row.Cells["varNamedataGridViewTextBoxColumn"].Value.ToString();
                if (varname== varnameinrow)
                {
                    row.Selected = true;
                    if (row.Index < firstindex) firstindex = row.Index;
                }
            }
            if (firstindex == int.MaxValue) firstindex = 0;
            if (varsViewDataGridView.SelectedRows.Count > 0)
            {
                varsViewDataGridView.FirstDisplayedScrollingRowIndex = firstindex;
            }
            this.WindowState = FormWindowState.Normal;
            //this.Activate();
        }

        private void prepareFormSavesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            PrepareSaves prepareSaves = new PrepareSaves();
            prepareSaves.form1 = this;
            prepareSaves.ShowDialog();
        }

        private void buttonFilteredMissingDepends_Click(object sender, EventArgs e)
        {
            int varscount= varsViewDataGridView.Rows.Count;
            string message =String.Format( "Analyzing dependencies from {0} vars on the leftside of form, a processing window will be opened.",varscount);

            const string caption = "FilteredMissingDepends";
            var result = MessageBox.Show(message, caption,
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1);
            if (result == DialogResult.Yes)
            {
                StartBackgroundJob("FilteredMissingDepends");
            }
        }

        private void buttonClearCache_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("The cache can improve the speed of secondary analysis, normally you don't need to clear it, unless you modify the scene file. This operation only clears the cache of the current scene, if you need to clear all the cache, please delete the cache directory manually.", "Clear Cache", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                string sceneFoldername = SceneAnalysis.CacheFolder(curVarName, curEntryName);
                try { Directory.Delete(sceneFoldername, true); }
                catch { }
                UpdateButtonClearCache();
            }
        }
    }
}
