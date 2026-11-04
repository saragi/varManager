using System.Threading;
using System.Windows;

namespace varManager.Ui
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var jobs = new JobRunner(SynchronizationContext.Current);
            new MainWindow { DataContext = new ShellViewModel(AppSettings.DefaultFile, jobs) }.Show();
        }
    }
}
