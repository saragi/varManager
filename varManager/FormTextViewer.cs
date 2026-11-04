using System.Drawing;
using System.Windows.Forms;

namespace varManager
{
    /// <summary>Shows read-only text.</summary>
    public class FormTextViewer : Form
    {
        public FormTextViewer(string title, string text)
        {
            Text = title;
            ClientSize = new Size(820, 600);
            MinimumSize = new Size(420, 300);
            StartPosition = FormStartPosition.CenterParent;
            Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Text = text
            });
        }
    }
}
