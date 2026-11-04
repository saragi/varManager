using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace varManager.Ui
{
    public sealed class HealthCard
    {
        public HealthCard(string number, string text, string? actionLabel = null, ICommand? action = null)
        {
            Number = number;
            Text = text;
            ActionLabel = actionLabel ?? "";
            Action = action;
        }

        public string Number { get; }
        public string Text { get; }
        public string ActionLabel { get; }
        public ICommand? Action { get; }
        public bool HasAction => Action != null;
    }

    /// <summary>A change waiting for the user's yes, with what it will touch.</summary>
    public sealed class HealthChange
    {
        public HealthChange(string title, string note, IReadOnlyList<string> lines, string confirmLabel, string jobName, Func<JobContext, string> work)
        {
            Title = title;
            Note = note;
            Lines = lines;
            ConfirmLabel = confirmLabel;
            JobName = jobName;
            Work = work;
        }

        public string Title { get; }
        public string Note { get; }
        public IReadOnlyList<string> Lines { get; }
        public string ConfirmLabel { get; }
        public string JobName { get; }
        public Func<JobContext, string> Work { get; }
    }

    /// <summary>Shows what's wrong first. Nothing here changes anything until the user confirms it.</summary>
    public sealed class HealthViewModel : Observable
    {
        private const int MostRows = 200;

        private readonly string storeFile;
        private readonly LibraryPaths paths;
        private readonly JobRunner jobs;
        private IReadOnlyList<HealthCard> cards = new HealthCard[0];
        private IReadOnlyList<MissingNeed> missing = new MissingNeed[0];
        private string missingMore = "";
        private string statusText = "Checking the library.";
        private HealthChange? pending;
        private int reloads;

        public HealthViewModel(string storeFile, LibraryPaths paths, JobRunner jobs, Action goToMissing)
        {
            this.storeFile = storeFile;
            this.paths = paths;
            this.jobs = jobs;
            ConfirmCommand = new Command(() => Work = Confirm());
            CancelCommand = new Command(() => Pending = null);
            FindOnHubCommand = new Command(goToMissing);
        }

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand FindOnHubCommand { get; }

        /// <summary>The last confirmed change, including its job.</summary>
        public Task Work { get; private set; } = Task.CompletedTask;

        public IReadOnlyList<HealthCard> Cards
        {
            get => cards;
            private set
            {
                cards = value;
                Changed();
            }
        }

        public IReadOnlyList<MissingNeed> Missing
        {
            get => missing;
            private set
            {
                missing = value;
                Changed();
                Changed(nameof(HasMissing));
            }
        }

        public bool HasMissing => missing.Count > 0;

        public string MissingMore
        {
            get => missingMore;
            private set
            {
                missingMore = value;
                Changed();
            }
        }

        public string StatusText
        {
            get => statusText;
            private set
            {
                statusText = value;
                Changed();
            }
        }

        public HealthChange? Pending
        {
            get => pending;
            private set
            {
                pending = value;
                Changed();
                Changed(nameof(HasPending));
            }
        }

        public bool HasPending => pending != null;

        public async Task Reload()
        {
            int reload = ++reloads;
            StatusText = "Checking the library.";
            var problems = new List<string>();
            HealthReport report = await Task.Run(() =>
            {
                using LibraryStore store = LibraryStore.Open(storeFile);
                return LibraryHealth.Load(store, paths, problems.Add);
            }).ConfigureAwait(true);
            if (reload != reloads)
                return;

            // A review on screen describes the last check, so it goes with it.
            Pending = null;
            Cards = MakeCards(report);
            Missing = report.Missing.Take(MostRows).ToList();
            MissingMore = report.Missing.Count > MostRows ? $"And {report.Missing.Count - MostRows:N0} more." : "";
            StatusText = problems.Count == 0 ? "Problems first, nothing fixed without asking" : $"{problems.Count} saves could not be read, so they weren't counted.";
        }

        private List<HealthCard> MakeCards(HealthReport report)
        {
            var made = new List<HealthCard>();
            if (report.GoneRecords.Count > 0)
                made.Add(new HealthCard(report.GoneRecords.Count.ToString("N0"), "Records whose var file is gone", "Review", Review(GoneChange(report))));
            if (report.BrokenLinks.Count > 0)
                made.Add(new HealthCard(report.BrokenLinks.Count.ToString("N0"), "Broken links in this profile", "Review", Review(BrokenChange(report))));
            if (report.Stale.Count > 0)
                made.Add(new HealthCard(report.Stale.Count.ToString("N0"), $"Old versions nothing uses, {Size(report.StaleBytes)}", "Review", Review(StaleChange(report))));
            foreach (HeldGroup group in report.Held)
                made.Add(new HealthCard(group.Files.Count.ToString("N0"), $"{group.Title}, {Size(group.Bytes)}", "Review before delete", Review(HeldChange(group))));

            made.Add(new HealthCard(report.NotLinked.ToString("N0"), "Stored, not linked in default"));
            made.Add(new HealthCard(Size(report.LibraryBytes), $"In the library, {report.VarCount:N0} vars"));
            if (report.FreeBytes != null)
                made.Add(new HealthCard(Size(report.FreeBytes.Value), "Free on " + report.Drive));
            return made;
        }

        private ICommand Review(HealthChange change)
        {
            return new Command(() => Pending = change);
        }

        private static IReadOnlyList<string> Rows(IEnumerable<string> lines, int count)
        {
            List<string> rows = lines.Take(MostRows).ToList();
            if (count > MostRows)
                rows.Add($"And {count - MostRows:N0} more.");
            return rows;
        }

        private HealthChange HeldChange(HeldGroup group)
        {
            string warning = group.NotInLibrary == 0 ? "The library has its own copy of every var here." : $"{Count(group.NotInLibrary, "var")} here {(group.NotInLibrary == 1 ? "has" : "have")} no copy in the library.";
            return new HealthChange(group.Title, $"This deletes {Count(group.Files.Count, "file")} for good, {Size(group.Bytes)}. {warning}",
                Rows(group.Files.Select(file => Path.GetFileName(file.Path) + (file.InLibrary ? "" : "  (no copy in the library)")), group.Files.Count),
                "Delete for good", "Deleting files", context =>
                {
                    int deleted = LibraryHealth.DeleteHeld(group);
                    return deleted == 1 ? "Deleted 1 file." : $"Deleted {deleted:N0} files.";
                });
        }

        private HealthChange StaleChange(HealthReport report)
        {
            List<string> stale = report.Stale;
            string linked = report.StaleLinked.Count == 0 ? "" : $" {Count(report.StaleLinked.Count, "var")} of them {(report.StaleLinked.Count == 1 ? "is" : "are")} linked now, and moving turns that off.";
            return new HealthChange("Old versions nothing uses", $"This moves {Count(stale.Count, "var")} to a holding folder, and Undo move puts them back. No var, profile, base.txt or save needs them.{linked}",
                Rows(stale.Select(name => report.StaleLinked.Contains(name) ? name + "  (linked now)" : name), stale.Count), "Move out", "Moving stale vars", context =>
                {
                    using LibraryStore store = LibraryStore.Open(storeFile);
                    if (LibraryHealth.MoveStale(store, paths, stale) == null)
                        return "The library changed, so nothing moved. Check again.";
                    VamBridge.RequestRescan(paths.VamPath);
                    return stale.Count == 1 ? "Moved 1 var to the holding folder." : $"Moved {stale.Count:N0} vars to the holding folder.";
                });
        }

        private HealthChange BrokenChange(HealthReport report)
        {
            List<string> links = report.BrokenLinks;
            return new HealthChange("Broken links in this profile", "This points each link at where its var is now. A link to a var the library no longer has goes.",
                Rows(links.Select(Path.GetFileName)!, links.Count), "Repair links", "Repairing links", context =>
                {
                    using LibraryStore store = LibraryStore.Open(storeFile);
                    int repaired = LibraryHealth.RepairLinks(store, paths, links);
                    VamBridge.RequestRescan(paths.VamPath);
                    return repaired == 1 ? "Repaired 1 link." : $"Repaired {repaired:N0} links.";
                });
        }

        private HealthChange GoneChange(HealthReport report)
        {
            List<string> gone = report.GoneRecords;
            return new HealthChange("Records whose var file is gone", "Check the vars folder first. This forgets these vars, and Update DB adds any back that return, which can take a long time on a big library.",
                Rows(gone, gone.Count), "Forget records", "Forgetting records", context =>
                {
                    using LibraryStore store = LibraryStore.Open(storeFile);
                    int removed = LibraryUpdate.RemoveMissing(store, paths, gone);
                    return removed == 1 ? "Forgot 1 record." : $"Forgot {removed:N0} records.";
                });
        }

        private async Task Confirm()
        {
            HealthChange? change = pending;
            if (change == null)
                return;
            Task? job = jobs.Start(change.JobName, change.Work);
            if (job == null)
                return;
            Pending = null;
            await job.ConfigureAwait(true);
        }

        private static string Count(int count, string noun)
        {
            return count == 1 ? "1 " + noun : $"{count:N0} {noun}s";
        }

        private static string Size(long bytes)
        {
            return bytes >= 1073741824 ? $"{bytes / 1073741824.0:F2} GB" : $"{bytes / 1048576.0:F1} MB";
        }
    }
}
