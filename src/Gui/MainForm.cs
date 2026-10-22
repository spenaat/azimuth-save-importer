using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AzimuthSaveImporter.Core;

namespace AzimuthSaveImporter.Gui
{
    sealed class MainForm : Form
    {
        static readonly Color Accent = Color.FromArgb(22, 112, 84);
        static readonly Color WarningBack = Color.FromArgb(255, 244, 206);
        static readonly Color ErrorFore = Color.FromArgb(176, 32, 32);
        static readonly Color OkFore = Color.FromArgb(22, 112, 64);

        readonly Settings _settings = Settings.Load();

        readonly TextBox _source = new TextBox();
        readonly TextBox _target = new TextBox();
        readonly ComboBox _player = new ComboBox();
        readonly Label _formatStatus = new Label();
        readonly Label _summary = new Label();
        readonly ListView _list = new ListView();
        readonly Label _warnings = new Label();
        readonly ProgressBar _progress = new ProgressBar();
        readonly Button _import = new Button();
        readonly Button _restore = new Button();
        readonly LinkLabel _backups = new LinkLabel();
        readonly TextBox _log = new TextBox();

        ImportPlan _plan;
        string _planError;
        int _planVersion;
        bool _busy;

        public MainForm()
        {
            Text = "Azimuth Save Importer " + Program.Version;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(900, 780);
            MinimumSize = new Size(760, 600);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 10, 14, 12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96f));
            Controls.Add(root);

            root.Controls.Add(BuildHeader());
            root.Controls.Add(BuildSourceGroup());
            root.Controls.Add(BuildTargetGroup());
            root.Controls.Add(BuildReviewGroup());
            root.Controls.Add(BuildActions());

            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.BackColor = SystemColors.Window;
            _log.Font = new Font("Consolas", 8.5f);
            root.Controls.Add(_log);

            _source.Text = _settings.Source;
            _target.Text = _settings.Target;
            UpdateBackupsLink();
            Shown += (s, e) =>
            {
                RefreshPlayers(_settings.Player);
                Log("Ready. 1) Pick the folder or ZIP you downloaded with XBsync. 2) Pick Azimuth's folder. 3) Review and import.");
            };
            FormClosed += (s, e) =>
            {
                SaveSettings();
                if (_plan != null) _plan.Source.Dispose();
            };
        }

        // ------------------------------------------------------------------ layout

        Control BuildHeader()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 6) };
            var title = new Label
            {
                Text = "Azimuth Save Importer",
                Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
                ForeColor = Accent,
                AutoSize = true,
                Margin = new Padding(0)
            };
            var subtitle = new Label
            {
                Text = "Brings your Forza Horizon 2 Xbox saves (downloaded with XBsync) into Azimuth on PC: " +
                       "profile, liveries, tuning setups, photos and garage thumbnails.",
                AutoSize = true,
                MaximumSize = new Size(860, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(1, 2, 0, 0)
            };
            panel.Controls.Add(title);
            panel.Controls.Add(subtitle);
            return panel;
        }

        Control BuildSourceGroup()
        {
            var group = Group("1.  Xbox saves (downloaded with XBsync)");
            var grid = Grid(3);
            _source.Dock = DockStyle.Fill;
            _source.Margin = new Padding(0, 4, 6, 4);
            _source.AllowDrop = true;
            _source.DragEnter += OnDragEnter;
            _source.DragDrop += (s, e) => { var p = DroppedPath(e); if (p != null) { _source.Text = p; RebuildPlan(); } };
            _source.Leave += (s, e) => RebuildPlan();
            _source.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { RebuildPlan(); e.SuppressKeyPress = true; } };
            var folder = SmallButton("Folder...", (s, e) =>
            {
                var path = PickFolder("Choose the folder XBsync downloaded the Forza Horizon 2 saves into.", _source.Text);
                if (path != null) { _source.Text = path; RebuildPlan(); }
            });
            var zip = SmallButton("ZIP...", (s, e) =>
            {
                using (var dialog = new OpenFileDialog { Filter = "XBsync ZIP (*.zip)|*.zip", Title = "Choose the ZIP downloaded with XBsync" })
                {
                    if (File.Exists(_source.Text)) dialog.InitialDirectory = Path.GetDirectoryName(_source.Text);
                    if (dialog.ShowDialog(this) == DialogResult.OK) { _source.Text = dialog.FileName; RebuildPlan(); }
                }
            });
            grid.Controls.Add(_source, 0, 0);
            grid.Controls.Add(folder, 1, 0);
            grid.Controls.Add(zip, 2, 0);
            var hint = Hint("In XBsync, sign in, pick Forza Horizon 2 and use \"Download all\" (folder) or \"Download ZIP\".");
            grid.Controls.Add(hint, 0, 1);
            grid.SetColumnSpan(hint, 3);
            group.Controls.Add(grid);
            return group;
        }

        Control BuildTargetGroup()
        {
            var group = Group("2.  Azimuth saves");
            var grid = Grid(3);
            _target.Dock = DockStyle.Fill;
            _target.Margin = new Padding(0, 4, 6, 4);
            _target.AllowDrop = true;
            _target.DragEnter += OnDragEnter;
            _target.DragDrop += (s, e) => { var p = DroppedPath(e); if (p != null) { _target.Text = p; RefreshPlayers(null); } };
            _target.Leave += (s, e) => RefreshPlayers(null);
            _target.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { RefreshPlayers(null); e.SuppressKeyPress = true; } };
            var browse = SmallButton("Browse...", (s, e) =>
            {
                var path = PickFolder("Choose the folder Azimuth is installed in (or its saves folder).", _target.Text);
                if (path != null) { _target.Text = path; RefreshPlayers(null); }
            });
            var open = SmallButton("Open", (s, e) =>
            {
                var player = SelectedPlayer;
                var folder = player != null && Directory.Exists(player) ? player : _target.Text;
                if (Directory.Exists(folder)) Process.Start("explorer.exe", "\"" + folder + "\"");
            });
            grid.Controls.Add(_target, 0, 0);
            grid.Controls.Add(browse, 1, 0);
            grid.Controls.Add(open, 2, 0);

            var playerRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
            playerRow.Controls.Add(new Label { Text = "Save folder:", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
            _player.DropDownStyle = ComboBoxStyle.DropDownList;
            _player.Width = 640;
            _player.SelectedIndexChanged += (s, e) => RebuildPlan();
            playerRow.Controls.Add(_player);
            grid.Controls.Add(playerRow, 0, 1);
            grid.SetColumnSpan(playerRow, 3);

            _formatStatus.AutoSize = true;
            _formatStatus.MaximumSize = new Size(840, 0);
            _formatStatus.Margin = new Padding(1, 4, 0, 2);
            grid.Controls.Add(_formatStatus, 0, 2);
            grid.SetColumnSpan(_formatStatus, 3);
            group.Controls.Add(grid);
            return group;
        }

        Control BuildReviewGroup()
        {
            var group = Group("3.  Review");
            group.AutoSize = false;
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _summary.AutoSize = true;
            _summary.MaximumSize = new Size(840, 0);
            _summary.Margin = new Padding(1, 2, 0, 6);
            _summary.Font = new Font(Font, FontStyle.Bold);
            grid.Controls.Add(_summary);

            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.HideSelection = false;
            _list.Dock = DockStyle.Fill;
            _list.Columns.Add("Xbox container", 295);
            _list.Columns.Add("Imported as", 230);
            _list.Columns.Add("Type", 110);
            _list.Columns.Add("Files", 45, HorizontalAlignment.Right);
            _list.Columns.Add("Size", 70, HorizontalAlignment.Right);
            _list.Columns.Add("Status", 80);
            grid.Controls.Add(_list);

            _warnings.AutoSize = true;
            _warnings.MaximumSize = new Size(840, 0);
            _warnings.BackColor = WarningBack;
            _warnings.Padding = new Padding(6, 4, 6, 4);
            _warnings.Margin = new Padding(0, 6, 0, 0);
            _warnings.Visible = false;
            grid.Controls.Add(_warnings);
            group.Controls.Add(grid);
            return group;
        }

        Control BuildActions()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 8, 0, 8) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _restore.Text = "Restore a backup...";
            _restore.AutoSize = true;
            _restore.Padding = new Padding(6, 2, 6, 2);
            _restore.Click += OnRestore;
            bar.Controls.Add(_restore, 0, 0);

            _backups.AutoSize = true;
            _backups.Margin = new Padding(10, 9, 10, 0);
            _backups.LinkClicked += OnBackupsLink;
            bar.Controls.Add(_backups, 1, 0);

            _progress.Width = 160;
            _progress.Height = 18;
            _progress.Margin = new Padding(0, 9, 10, 0);
            _progress.Visible = false;
            bar.Controls.Add(_progress, 2, 0);

            _import.Text = "Import into Azimuth";
            _import.AutoSize = true;
            _import.Padding = new Padding(14, 4, 14, 4);
            _import.Font = new Font(Font, FontStyle.Bold);
            _import.BackColor = Accent;
            _import.ForeColor = Color.White;
            _import.FlatStyle = FlatStyle.Flat;
            _import.FlatAppearance.BorderSize = 0;
            SetImportEnabled(false);
            _import.Click += OnImport;
            bar.Controls.Add(_import, 3, 0);
            return bar;
        }

        static GroupBox Group(string title)
        {
            return new GroupBox
            {
                Text = title,
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8),
                Margin = new Padding(0, 4, 0, 4)
            };
        }

        static TableLayoutPanel Grid(int columns)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = columns };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (var i = 1; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return grid;
        }

        static Button SmallButton(string text, EventHandler click)
        {
            var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 3, 6, 3), Padding = new Padding(4, 0, 4, 0) };
            button.Click += click;
            return button;
        }

        static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(1, 0, 0, 2) };
        }

        // ------------------------------------------------------------------ state

        string SelectedPlayer
        {
            get { var item = _player.SelectedItem as PlayerItem; return item == null ? null : item.Path; }
        }

        void RefreshPlayers(string preferred)
        {
            var target = _target.Text.Trim();
            var current = preferred ?? SelectedPlayer;
            _player.BeginUpdate();
            _player.Items.Clear();
            foreach (var path in Directory.Exists(target) ? AzimuthTarget.FindPlayerFolders(target) : new List<string>())
                _player.Items.Add(new PlayerItem(path));
            _player.EndUpdate();
            if (_player.Items.Count > 0)
            {
                var match = _player.Items.Cast<PlayerItem>().FirstOrDefault(p =>
                    string.Equals(p.Path, current, StringComparison.OrdinalIgnoreCase));
                _player.SelectedItem = match ?? _player.Items[0];
            }
            else RebuildPlan();
        }

        async void RebuildPlan()
        {
            if (_busy) return;
            var version = ++_planVersion;
            var sourcePath = _source.Text.Trim();
            var targetText = _target.Text.Trim();
            var player = SelectedPlayer;

            ImportPlan plan = null;
            string error = null;
            if (sourcePath.Length > 0 || player != null)
            {
                SetSummary("Reading...", SystemColors.ControlText);
                try
                {
                    plan = await Task.Run(() => BuildPlan(sourcePath, player));
                }
                catch (ImportException e) { error = e.Message; }
                catch (Exception e) { error = "Unexpected error: " + e.Message; }
            }
            if (version != _planVersion) { if (plan != null) plan.Source.Dispose(); return; }

            if (_plan != null) _plan.Source.Dispose();
            _plan = plan;
            _planError = error;
            ShowPlan(sourcePath, targetText, player);
        }

        static ImportPlan BuildPlan(string sourcePath, string player)
        {
            if (sourcePath.Length == 0) throw new ImportException("Pick the Xbox saves first (step 1).");
            var source = XboxSource.Open(sourcePath);
            if (player == null) { source.Dispose(); throw new ImportException("Pick Azimuth's folder (step 2)."); }
            try { return Importer.Plan(source, player); }
            catch { source.Dispose(); throw; }
        }

        void ShowPlan(string sourcePath, string targetText, string player)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            _warnings.Visible = false;

            // Format check is shown whenever a save folder is known, even without a source.
            if (player != null)
            {
                var check = _plan != null ? _plan.FormatCheck : SaveFormat.VerifyExistingCommits(player);
                _formatStatus.Text = (check.Verified ? "✔ " : check.Failed ? "✖ " : "⚠ ") + check.Describe();
                _formatStatus.ForeColor = check.Verified ? OkFore : check.Failed ? ErrorFore : Color.FromArgb(150, 100, 0);
            }
            else
            {
                _formatStatus.Text = targetText.Length == 0
                    ? "Choose the folder where Azimuth is installed; its saves folder is found automatically."
                    : "No Azimuth save folder (data\\titles\\...\\saves\\player-N) was found in this folder.";
                _formatStatus.ForeColor = targetText.Length == 0 ? SystemColors.GrayText : ErrorFore;
            }

            if (_plan == null)
            {
                var isError = _planError != null && !_planError.StartsWith("Pick");
                SetSummary(_planError ?? "Pick the Xbox saves (step 1) and Azimuth's folder (step 2).",
                    isError ? ErrorFore : SystemColors.GrayText);
                _list.EndUpdate();
                SetImportEnabled(false);
                return;
            }

            foreach (var item in _plan.Items.OrderByDescending(i => i.Source.IsProfile))
            {
                var row = new ListViewItem(item.Source.OriginalName);
                row.SubItems.Add(item.TargetName);
                row.SubItems.Add(item.Source.Kind);
                row.SubItems.Add(item.Source.Files.Count.ToString());
                row.SubItems.Add(Importer.FormatSize(item.Source.TotalSize));
                row.SubItems.Add(item.Status == ItemStatus.New ? "new" : item.Status == ItemStatus.ReplacesExisting ? "replaces" : "imported");
                if (item.Source.IsProfile) row.Font = new Font(_list.Font, FontStyle.Bold);
                if (item.Status == ItemStatus.ReplacesExisting) row.ForeColor = Color.FromArgb(150, 100, 0);
                if (item.Status == ItemStatus.AlreadyImported) row.ForeColor = OkFore;
                var skipped = item.Source.SkippedFiles;
                if (skipped.Count > 0) row.ToolTipText = "Not imported: " + string.Join(", ", skipped);
                _list.Items.Add(row);
            }
            _list.ShowItemToolTips = true;
            _list.EndUpdate();

            var summary = "Ready to import " + _plan.Summary() + ".";
            if (_plan.AllAlreadyImported)
                summary = "\u2714 All " + _plan.Items.Count + " Xbox items are already in Azimuth, identical to this download. Nothing to do.";
            else if (_plan.HasExistingSaves)
                summary += string.Format("\nAzimuth already has {0} saved item{1} here.", _plan.ExistingContainers.Count,
                    _plan.ExistingContainers.Count == 1 ? "" : "s");
            SetSummary(summary, _plan.AllAlreadyImported ? OkFore : SystemColors.ControlText);

            var warnings = new List<string>(_plan.Warnings);
            var skippedFiles = _plan.Items.Sum(i => i.Source.SkippedFiles.Count);
            if (skippedFiles > 0)
                warnings.Add(skippedFiles + " Xbox-only file" + (skippedFiles == 1 ? " is" : "s are") +
                             " left out (hover a row to see which); Azimuth does not use them.");
            if (warnings.Count > 0)
            {
                _warnings.Text = string.Join(Environment.NewLine, warnings.Select(w => "• " + w));
                _warnings.Visible = true;
            }
            SetImportEnabled(!_plan.FormatCheck.Failed && !_plan.AllAlreadyImported);
            SaveSettings();
        }

        void SetSummary(string text, Color color)
        {
            _summary.Text = text;
            _summary.ForeColor = color;
        }

        // ------------------------------------------------------------------ actions

        async void OnImport(object sender, EventArgs e)
        {
            var plan = _plan;
            if (plan == null || _busy) return;

            var running = AzimuthTarget.RunningGameProcesses();
            if (running.Count > 0 && MessageBox.Show(this,
                    "The game seems to be running (" + string.Join(", ", running) + ").\n\n" +
                    "Close it before importing, or it may overwrite the imported saves.\n\nImport anyway?",
                    "Close the game first", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            var options = new ImportOptions { BackupFolder = _settings.BackupFolder };
            if (plan.FormatCheck.NothingToCompare)
            {
                if (MessageBox.Show(this,
                        "There are no Azimuth saves in this folder yet, so the importer cannot confirm that it writes saves " +
                        "exactly the way Azimuth does.\n\nRecommended: cancel, start the game, let it create a save, close it, " +
                        "and import again.\n\nImport anyway?",
                        "Save format not verified", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
                options.AllowUnverifiedFormat = true;
            }

            if (plan.HasExistingSaves)
            {
                var choice = ExistingSavesDialog.Ask(this, plan, _settings.BackupFolder);
                if (choice == null) return;
                options.ExistingSaves = choice.Value;
            }
            else if (MessageBox.Show(this,
                         "Import " + plan.Summary() + " into\n" + plan.PlayerFolder + "?",
                         "Import", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            options.Log = message => BeginInvoke(new Action(() => Log(message)));
            options.Progress = percent => BeginInvoke(new Action(() => _progress.Value = Math.Max(0, Math.Min(100, percent))));
            SetBusy(true);
            Log("Importing into " + plan.PlayerFolder + " (" + (options.ExistingSaves == ExistingSaves.ReplaceAll
                ? "replace all" : "merge") + ")...");
            try
            {
                var result = await Task.Run(() => Importer.Execute(plan, options));
                Log("Done.");
                MessageBox.Show(this,
                    string.Format("Imported and verified {0} item{1}.{2}\n\nStart the game and check your profile, liveries and photos.",
                        result.Imported, result.Imported == 1 ? "" : "s",
                        result.BackupPath != null ? "\n\nYour previous saves were backed up to:\n" + result.BackupPath : ""),
                    "Import finished", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error)
            {
                Log("Error: " + error.Message);
                MessageBox.Show(this, error.Message, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                RebuildPlan();
            }
        }

        async void OnRestore(object sender, EventArgs e)
        {
            var player = SelectedPlayer;
            if (player == null)
            {
                MessageBox.Show(this, "Pick Azimuth's folder first (step 2).", "Restore a backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string zip;
            using (var dialog = new OpenFileDialog { Filter = "Save backups (*.zip)|*.zip", Title = "Choose a backup to restore" })
            {
                if (Directory.Exists(_settings.BackupFolder)) dialog.InitialDirectory = _settings.BackupFolder;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                zip = dialog.FileName;
            }
            if (MessageBox.Show(this,
                    "Replace the saves in\n" + player + "\nwith the backup\n" + Path.GetFileName(zip) + "?\n\n" +
                    "The current saves are backed up first.", "Restore a backup",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
                return;
            SetBusy(true);
            _progress.Style = ProgressBarStyle.Marquee;
            try
            {
                var backupFolder = _settings.BackupFolder;
                Action<string> log = message => BeginInvoke(new Action(() => Log(message)));
                var safety = await Task.Run(() => Importer.Restore(zip, player, backupFolder, log));
                MessageBox.Show(this, "The backup was restored." + (safety != null ? "\n\nThe saves it replaced were backed up to:\n" + safety : ""),
                    "Restore a backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error)
            {
                Log("Error: " + error.Message);
                MessageBox.Show(this, error.Message, "Restore failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _progress.Style = ProgressBarStyle.Continuous;
                SetBusy(false);
                RebuildPlan();
            }
        }

        void OnBackupsLink(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (e.Link.LinkData as string == "change")
            {
                var path = PickFolder("Choose where backups are saved.", _settings.BackupFolder);
                if (path == null) return;
                _settings.BackupFolder = path;
                SaveSettings();
                UpdateBackupsLink();
                return;
            }
            Directory.CreateDirectory(_settings.BackupFolder);
            Process.Start("explorer.exe", "\"" + _settings.BackupFolder + "\"");
        }

        void UpdateBackupsLink()
        {
            const string prefix = "Backups: ";
            const string change = "change";
            var text = prefix + _settings.BackupFolder + "  (" + change + ")";
            _backups.Text = text;
            _backups.Links.Clear();
            _backups.Links.Add(prefix.Length, _settings.BackupFolder.Length, "open");
            _backups.Links.Add(text.Length - change.Length - 1, change.Length, "change");
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            SetImportEnabled(!busy && _plan != null && !_plan.FormatCheck.Failed && !_plan.AllAlreadyImported);
            _restore.Enabled = !busy;
            _source.Enabled = _target.Enabled = _player.Enabled = !busy;
            _progress.Visible = busy;
            _progress.Value = 0;
            UseWaitCursor = busy;
        }

        void SetImportEnabled(bool enabled)
        {
            _import.Enabled = enabled;
            _import.BackColor = enabled ? Accent : Color.FromArgb(170, 190, 182);
        }

        void SaveSettings()
        {
            _settings.Source = _source.Text.Trim();
            _settings.Target = _target.Text.Trim();
            _settings.Player = SelectedPlayer ?? _settings.Player;
            _settings.Save();
        }

        void Log(string message)
        {
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        }

        string PickFolder(string description, string start)
        {
            using (var dialog = new FolderBrowserDialog { Description = description, ShowNewFolderButton = false })
            {
                if (Directory.Exists(start)) dialog.SelectedPath = start;
                else if (File.Exists(start)) dialog.SelectedPath = Path.GetDirectoryName(start);
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        static void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        static string DroppedPath(DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            return files != null && files.Length > 0 ? files[0] : null;
        }

        sealed class PlayerItem
        {
            public readonly string Path;
            public PlayerItem(string path) { Path = path; }
            public override string ToString()
            {
                var exists = Directory.Exists(Path);
                var count = exists ? AzimuthTarget.ExistingContainers(Path).Count : 0;
                return Path + (exists ? "   (" + count + " saved item" + (count == 1 ? "" : "s") + ")" : "   (will be created)");
            }
        }
    }
}
