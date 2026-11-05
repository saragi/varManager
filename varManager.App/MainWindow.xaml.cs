using System.Windows;
using System.Windows.Controls;
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

        // A PasswordBox can't be bound, so it's filled each time the panel opens.
        private void PixeldrainKey_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (DataContext is ShellViewModel shell && (bool)e.NewValue)
                ((PasswordBox)sender).Password = shell.PixeldrainKey;
        }

        private void PixeldrainKey_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is ShellViewModel shell)
                shell.PixeldrainKey = ((PasswordBox)sender).Password;
        }

        private string? PickFolder(string title)
        {
            var dialog = new OpenFolderDialog { Title = title };
            bool chosen = dialog.ShowDialog(this) == true;
            // The dialog can move the working directory, which the scene cache hangs off.
            System.IO.Directory.SetCurrentDirectory(System.AppContext.BaseDirectory);
            return chosen ? dialog.FolderName : null;
        }
    }
}
