using System;
using System.IO;
using System.Threading;

namespace varManager.Ui
{
    /// <summary>Scans vars dropped in the repository by themselves, once they have finished arriving.</summary>
    public sealed class RepositoryWatch : IDisposable
    {
        private readonly string folder;
        private readonly Func<bool> busy;
        private readonly Action scan;
        private readonly SynchronizationContext? ui;
        private readonly TimeSpan wait;
        private readonly FileSystemWatcher watcher;
        private readonly Timer timer;
        private volatile bool disposed;

        public RepositoryWatch(string folder, Func<bool> busy, Action scan, SynchronizationContext? ui, TimeSpan wait)
        {
            this.folder = folder;
            this.busy = busy;
            this.scan = scan;
            this.ui = ui;
            this.wait = wait;
            timer = new Timer(_ => Post());
            watcher = new FileSystemWatcher(folder, "*.var") { IncludeSubdirectories = false };
            watcher.Created += (sender, e) => Restart();
            watcher.Renamed += (sender, e) => Restart();
            watcher.EnableRaisingEvents = true;
            // Also picks up vars added while the app was closed.
            Restart();
        }

        public void Dispose()
        {
            disposed = true;
            watcher.Dispose();
            timer.Dispose();
        }

        private void Restart()
        {
            try
            {
                if (!disposed)
                    timer.Change(wait, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // A file event was already on its way when this closed.
            }
        }

        private void Post()
        {
            if (ui == null)
                Tick();
            else
                ui.Post(_ => Tick(), null);
        }

        private void Tick()
        {
            if (disposed)
                return;
            try
            {
                // Asks again later, because a job or a var still copying only means not yet.
                if (busy() || !VarTidier.RepositoryVarsReady(folder))
                {
                    Restart();
                    return;
                }
                if (VarTidier.RepositoryVars(folder).Count > 0)
                    scan();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // The folder went away, or the app is closing. An exception on a timer would end the app.
            }
        }
    }
}
