using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace varManager
{
    /// <summary>Previews a lean profile and asks for its name.</summary>
    public class FormLeanProfile : Form
    {
        private readonly TextBox nameBox = new TextBox { Dock = DockStyle.Top, Text = "lean" };

        public FormLeanProfile(Func<string, string> preview)
        {
            Text = "Lean profile";
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(520, 360);
            StartPosition = FormStartPosition.CenterParent;

            Label label = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(4, 6, 4, 0),
                Text = "Creates links only. Your var files are not moved. Selected vars, their dependencies and your base set are included. Profile name:"
            };
            TextBox details = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false
            };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            Button create = new Button { Text = "Create profile", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.AddRange(new Control[] { cancel, create });

            EventHandler refresh = (s, e) =>
            {
                create.Enabled = IsValidName(ProfileName);
                details.Text = create.Enabled ? preview(ProfileName) : "Enter a name other than default, without characters Windows doesn't allow in file names.";
            };
            nameBox.TextChanged += refresh;
            Load += refresh;

            Controls.Add(details);
            Controls.Add(buttons);
            Controls.Add(nameBox);
            Controls.Add(label);
            CancelButton = cancel;
            ActiveControl = nameBox;
        }

        public string ProfileName
        {
            get { return nameBox.Text.Trim(); }
        }

        private static bool IsValidName(string name)
        {
            return name.Length > 0 && !name.EndsWith(".")
                && !string.Equals(name, "default", StringComparison.OrdinalIgnoreCase)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }
    }

    /// <summary>Shows a profile's health. Yes means repair, Retry means restore the prior version.</summary>
    public class FormProfileCheck : Form
    {
        public FormProfileCheck(string text, bool canRepair, bool canRestore)
        {
            Text = "Profile check";
            ClientSize = new Size(720, 500);
            MinimumSize = new Size(480, 320);
            StartPosition = FormStartPosition.CenterParent;

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            Button close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
            Button repair = new Button { Text = "Repair", DialogResult = DialogResult.Yes, AutoSize = true, Enabled = canRepair };
            Button restore = new Button { Text = "Restore previous", DialogResult = DialogResult.Retry, AutoSize = true, Enabled = canRestore };
            buttons.Controls.AddRange(new Control[] { close, repair, restore });

            Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = text });
            Controls.Add(buttons);
            CancelButton = close;
        }
    }
}
