using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace varManager.Ui
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // The scene cache and the plugin's temporary files sit in the working directory.
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            var jobs = new JobRunner(SynchronizationContext.Current);
            DispatcherUnhandledException += (_, args) =>
            {
                jobs.Say("Something went wrong: " + args.Exception.Message);
                args.Handled = true;
            };
            new MainWindow { DataContext = new ShellViewModel(AppSettings.DefaultFile, jobs) }.Show();
        }
    }
}
