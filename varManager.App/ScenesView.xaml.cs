using System;
using System.Windows;
using System.Windows.Controls;

namespace varManager.Ui
{
    public partial class ScenesView : UserControl
    {
        public const double CardWidth = 260;
        private const double CardGap = 14;
        private const double ScrollBarAllowance = 20;

        public ScenesView()
        {
            InitializeComponent();
        }

        private void List_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DataContext is ScenesViewModel scenes)
                scenes.Columns = Math.Max(1, (int)((e.NewSize.Width - ScrollBarAllowance) / (CardWidth + CardGap)));
        }
    }
}
