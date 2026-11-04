using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace varManager.Ui
{
    /// <summary>What a running job can see and report.</summary>
    public sealed class JobContext
    {
        private readonly Action<int, int> report;

        internal JobContext(Action<int, int> report, CancellationToken token)
        {
            this.report = report;
            Token = token;
        }

        public CancellationToken Token { get; }

        public void Report(int done, int total)
        {
            report(done, total);
        }
    }

    /// <summary>Runs one background job at a time, and feeds the job bar. Nothing here opens a popup.</summary>
    public sealed class JobRunner : INotifyPropertyChanged
    {
        private readonly SynchronizationContext? ui;
        private CancellationTokenSource? cancel;
        private bool isRunning;
        private string name = "";
        private int percent;
        private string note = "Ready.";

        public JobRunner(SynchronizationContext? ui)
        {
            this.ui = ui;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsRunning
        {
            get => isRunning;
            private set => Set(ref isRunning, value);
        }

        public string Name
        {
            get => name;
            private set => Set(ref name, value);
        }

        public int Percent
        {
            get => percent;
            private set => Set(ref percent, value);
        }

        public string Note
        {
            get => note;
            private set => Set(ref note, value);
        }

        /// <summary>Shows a message in the job bar.</summary>
        public void Say(string message)
        {
            Post(() => Note = message);
        }

        /// <summary>Starts a job, or returns null if one is already running.</summary>
        public Task? Start(string jobName, string doneNote, Action<JobContext> work)
        {
            if (cancel != null)
                return null;

            var source = new CancellationTokenSource();
            cancel = source;
            Name = jobName;
            Percent = 0;
            Note = "";
            IsRunning = true;
            var context = new JobContext((done, total) => Post(() => Percent = total <= 0 ? 0 : done * 100 / total), source.Token);
            return Task.Run(() =>
            {
                string result;
                try
                {
                    work(context);
                    result = doneNote;
                }
                catch (OperationCanceledException)
                {
                    result = jobName + " stopped.";
                }
                catch (Exception ex)
                {
                    result = jobName + " failed: " + ex.Message;
                }
                Post(() =>
                {
                    source.Dispose();
                    cancel = null;
                    IsRunning = false;
                    Name = "";
                    Percent = 0;
                    Note = result;
                });
            });
        }

        public void Cancel()
        {
            cancel?.Cancel();
        }

        private void Post(Action action)
        {
            if (ui == null)
                action();
            else
                ui.Post(_ => action(), null);
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string property = "")
        {
            if (Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
