using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace varManager.Ui
{
    public partial class ProfilesView : UserControl
    {
        private const string TextFiles = "Text files (*.txt)|*.txt";

        public ProfilesView()
        {
            InitializeComponent();
        }

        private void ExportList_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Filter = TextFiles, FileName = "linked vars.txt" };
            if (dialog.ShowDialog() == true && DataContext is ProfilesViewModel profiles)
                profiles.ExportLinked(dialog.FileName);
        }

        private void ImportList_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = TextFiles };
            if (dialog.ShowDialog() == true && DataContext is ProfilesViewModel profiles)
                profiles.TurnOnFromList(dialog.FileName);
        }
    }
}
