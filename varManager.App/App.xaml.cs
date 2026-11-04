using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace varManager.Ui
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (!AppSettings.Load(AppSettings.DefaultFile).HardwareRendering)
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            // The scene cache and the plugin's temporary files sit in the working directory.
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            // Clear cache deletes this folder, so it can't be left to follow the working directory.
            SceneAnalysis.CacheBase = AppContext.BaseDirectory;
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
