using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace varManager
{
    /// <summary>Shows a migration plan and asks before anything moves.</summary>
    public class FormMigrationPreview : Form
    {
        public FormMigrationPreview(MigrationPlan plan, string addonPackagesPath, string varsPath)
        {
            Text = "Move library into varManager";
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(560, 380);
            StartPosition = FormStartPosition.CenterScreen;

            Label intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 58,
                Padding = new Padding(10, 10, 10, 0),
                Text = "AddonPackages is a real folder, so varManager needs to move its files before it can manage them."
                    + Environment.NewLine + "From: " + addonPackagesPath
                    + Environment.NewLine + "To: " + varsPath
            };
            TextBox details = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Text = plan.Describe()
            };
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(6)
            };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            Button move = new Button { Name = "buttonMove", Text = "Move files", DialogResult = DialogResult.OK, AutoSize = true, Enabled = plan.SameDrive };
            Button save = new Button { Text = "Save plan...", AutoSize = true };
            save.Click += (s, e) =>
            {
                using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Text files|*.txt", FileName = "varManager-migration-plan.txt" })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        File.WriteAllText(dialog.FileName, details.Text);
                }
            };
            buttons.Controls.AddRange(new Control[] { cancel, move, save });

            Controls.Add(details);
            Controls.Add(buttons);
            Controls.Add(intro);
            AcceptButton = null;
            CancelButton = cancel;
        }
    }
}
