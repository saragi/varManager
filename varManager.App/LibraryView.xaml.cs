using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace varManager.Ui
{
    public partial class LibraryView : UserControl
    {
        public const double TileWidth = 160;
        private const double TileGap = 12;
        private const double ScrollBarAllowance = 20;

        public LibraryView()
        {
            InitializeComponent();
        }

        private LibraryViewModel Library => (LibraryViewModel)DataContext;

        private void LibraryView_Loaded(object sender, RoutedEventArgs e)
        {
            Window window = Window.GetWindow(this);
            window.Deactivated -= Window_Deactivated;
            window.Deactivated += Window_Deactivated;
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            (DataContext as LibraryViewModel)?.HideSuggestions();
        }

        // The popup is its own window, so a click that lands here is outside it.
        private void LibraryView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!SearchBox.IsMouseOver)
                (DataContext as LibraryViewModel)?.HideSuggestions();
        }

        private void Gallery_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DataContext != null)
                Library.Columns = Math.Max(1, (int)((e.NewSize.Width - ScrollBarAllowance) / (TileWidth + TileGap)));
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Library.AcceptFirstSuggestion();
                e.Handled = true;
            }
            else if (e.Key == Key.Back && SearchBox.Text.Length == 0)
            {
                Library.RemoveLastAuthor();
                e.Handled = true;
            }
        }
    }
}
