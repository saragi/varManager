using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Input;

namespace varManager.Ui
{
    /// <summary>One package in the downloads panel.</summary>
    public sealed class DownloadRow : Observable
    {
        private int percent;
        private bool isEndless;
        private string status = "Waiting";

        public DownloadRow(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public string Url { get; private set; } = "";
        public DownloadStage Stage { get; private set; }
        public bool IsStopped { get; private set; }
        public bool IsRunning => !IsStopped && Stage == DownloadStage.Downloading;
        public bool IsUnfinished => !IsStopped && (Stage == DownloadStage.Waiting || Stage == DownloadStage.Downloading);

        public int Percent
        {
            get => percent;
            private set
            {
                percent = value;
                Changed();
            }
        }

        /// <summary>The server didn't say how big the file is, so the bar can't show a share.</summary>
        public bool IsEndless
        {
            get => isEndless;
            private set
            {
                isEndless = value;
                Changed();
            }
        }

        public string Status
        {
            get => status;
            private set
            {
                status = value;
                Changed();
            }
        }

        public void Apply(DownloadUpdate update)
        {
            Url = update.Url;
            Changed(nameof(Url));
            Stage = update.Stage;
            IsStopped = false;
            bool sized = update.Total > 0;
            IsEndless = update.Stage == DownloadStage.Downloading && !sized;
            Percent = update.Stage == DownloadStage.Stored ? 100 : sized ? (int)(update.Received * 100 / update.Total!.Value) : 0;
            Status = update.Stage switch
            {
                DownloadStage.Waiting => "Waiting",
                DownloadStage.Stored => "Stored",
                DownloadStage.Failed => "Failed: " + update.Problem,
                _ when sized => $"{MissingCheck.Size(update.Received)} of {MissingCheck.Size(update.Total!.Value)}",
                _ => MissingCheck.Size(update.Received),
            };
        }

        public void Stop()
        {
            IsStopped = true;
            IsEndless = false;
            Status = "Stopped";
        }
    }

    /// <summary>What's being downloaded, from any page. Stays until cleared, so a failure can still be read.</summary>
    public sealed class DownloadsViewModel : Observable
    {
        private readonly SynchronizationContext? ui;
        private readonly Dictionary<string, DownloadRow> byName = new Dictionary<string, DownloadRow>(StringComparer.OrdinalIgnoreCase);
        private bool isOpen = true;

        public DownloadsViewModel(IHubSource hub, SynchronizationContext? ui)
        {
            this.ui = ui;
            hub.Downloading += update => Post(() => Apply(update));
            ClearCommand = new Command(ClearFinished);
            ToggleCommand = new Command(() => IsOpen = !IsOpen);
        }

        public ObservableCollection<DownloadRow> Rows { get; } = new ObservableCollection<DownloadRow>();
        public ICommand ClearCommand { get; }
        public ICommand ToggleCommand { get; }
        public bool HasRows => Rows.Count > 0;
        public string ToggleLabel => isOpen ? "Hide" : "Show";

        public bool IsOpen
        {
            get => isOpen;
            private set
            {
                isOpen = value;
                Changed();
                Changed(nameof(ToggleLabel));
            }
        }

        public string Summary
        {
            get
            {
                int stored = Rows.Count(row => row.Stage == DownloadStage.Stored);
                int running = Rows.Count(row => row.IsRunning);
                int failed = Rows.Count(row => row.Stage == DownloadStage.Failed);
                return $"{stored} of {Rows.Count} stored, {running} downloading, {failed} failed";
            }
        }

        /// <summary>A job that ended can't move its rows any further, as after Cancel.</summary>
        public void JobEnded()
        {
            Post(() =>
            {
                foreach (DownloadRow row in Rows.Where(row => row.IsUnfinished))
                    row.Stop();
                Refresh();
            });
        }

        private void Apply(DownloadUpdate update)
        {
            if (!byName.TryGetValue(update.Name, out DownloadRow? row))
            {
                row = new DownloadRow(update.Name);
                byName.Add(update.Name, row);
                Rows.Add(row);
            }
            row.Apply(update);
            Refresh();
        }

        private void ClearFinished()
        {
            foreach (DownloadRow row in Rows.Where(row => !row.IsUnfinished).ToList())
            {
                Rows.Remove(row);
                byName.Remove(row.Name);
            }
            Refresh();
        }

        private void Refresh()
        {
            Changed(nameof(HasRows));
            Changed(nameof(Summary));
        }

        // Posted even from the UI thread, so a job's last updates land before JobEnded does.
        private void Post(Action action)
        {
            if (ui == null)
                action();
            else
                ui.Post(_ => action(), null);
        }
    }
}
