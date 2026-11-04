using System.Windows;
using Microsoft.Win32;

namespace varManager.Ui
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private ShellViewModel Shell => (ShellViewModel)DataContext;

        private void BrowseVam_Click(object sender, RoutedEventArgs e)
        {
            string? folder = PickFolder("Choose the folder that holds VaM.exe");
            if (folder != null)
                Shell.VamPath = folder;
        }

        private void BrowseVars_Click(object sender, RoutedEventArgs e)
        {
            string? folder = PickFolder("Choose the folder that holds your vars");
            if (folder != null)
                Shell.VarsPath = folder;
        }

        private string? PickFolder(string title)
        {
            var dialog = new OpenFolderDialog { Title = title };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }
    }
}
