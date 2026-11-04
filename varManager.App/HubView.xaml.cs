using System.Windows;
using System.Windows.Controls;

namespace varManager.Ui
{
    public partial class HubView : UserControl
    {
        private const double TileWidth = 252;

        public HubView()
        {
            InitializeComponent();
        }

        private void Gallery_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DataContext is HubViewModel hub)
                hub.Columns = (int)System.Math.Max(1, (e.NewSize.Width - 20) / TileWidth);
        }
    }
}
