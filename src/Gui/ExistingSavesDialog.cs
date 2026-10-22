using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AzimuthSaveImporter.Core;

namespace AzimuthSaveImporter.Gui
{
    /// <summary>Asks what to do with the saves Azimuth already has. A backup is made either way.</summary>
    sealed class ExistingSavesDialog : Form
    {
        ExistingSaves? _choice;

        public static ExistingSaves? Ask(IWin32Window owner, ImportPlan plan, string backupFolder)
        {
            using (var dialog = new ExistingSavesDialog(plan, backupFolder))
            {
                dialog.ShowDialog(owner);
                return dialog._choice;
            }
        }

        ExistingSavesDialog(ImportPlan plan, string backupFolder)
        {
            Text = "Azimuth already has saves";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16), Dock = DockStyle.Fill };
            Controls.Add(root);

            var existing = plan.ExistingContainers.Count;
            var profile = plan.Items.FirstOrDefault(i => i.Source.IsProfile);
            var heading = new Label
            {
                Text = string.Format("Azimuth already has {0} saved item{1} in this folder.", existing, existing == 1 ? "" : "s"),
                Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8)
            };
            root.Controls.Add(heading);

            var details = string.Format(
                "{0}{1} of the {2} Xbox item{3} {5} the same name as an item already there.\n\n" +
                "Whatever you choose, the whole save folder is backed up first to:\n{4}\n" +
                "You can put it back any time with \"Restore a backup...\".",
                profile != null && profile.Status == ItemStatus.ReplacesExisting
                    ? "Your Azimuth profile (" + profile.TargetName + ") will be replaced by the Xbox profile.\n" : "",
                plan.Conflicts, plan.Items.Count, plan.Items.Count == 1 ? "" : "s", backupFolder, plan.Conflicts == 1 ? "has" : "have");
            root.Controls.Add(new Label { Text = details, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 14) });

            root.Controls.Add(Choice(
                "Back up and replace everything (recommended)",
                "Removes all current Azimuth saves in this folder and imports the Xbox saves. Use this for a clean move from Xbox.",
                ExistingSaves.ReplaceAll, true));
            root.Controls.Add(Choice(
                "Back up and merge",
                "Replaces only items with the same name and keeps everything else (for example liveries you made on PC).",
                ExistingSaves.Merge, false));

            var cancel = new Button { Text = "Cancel", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0), DialogResult = DialogResult.Cancel };
            root.Controls.Add(cancel);
            CancelButton = cancel;
        }

        Control Choice(string title, string description, ExistingSaves value, bool primary)
        {
            var button = new Button
            {
                Text = title + "\n" + description,
                TextAlign = ContentAlignment.MiddleLeft,
                Width = 520,
                Height = 58,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(8, 0, 8, 0),
                FlatStyle = FlatStyle.Flat
            };
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(22, 112, 84) : SystemColors.ControlDark;
            button.FlatAppearance.BorderSize = primary ? 2 : 1;
            button.Click += (s, e) => { _choice = value; DialogResult = DialogResult.OK; Close(); };
            if (primary) AcceptButton = button;
            return button;
        }
    }
}
