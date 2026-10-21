using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace T7Drop
{
    /// <summary>
    /// What was received and sent, newest first, with the state of each item sent. Dropping
    /// files or text on it sends them; Ctrl+V sends the clipboard. Double-click: copy a text
    /// (or open it if it is a link), open a file, retry a failed item.
    /// </summary>
    class HistoryForm : Form
    {
        /// <summary>A line: something received, or something sent.</summary>
        sealed class Row
        {
            public Received In;
            public OutItem Out;

            public string Key
            {
                get { return In != null ? "in:" + In.Id : "out:" + Out.Id; }
            }

            public long Time
            {
                get { return In != null ? In.Time : Out.Created; }
            }
        }

        sealed class BufferedListView : ListView
        {
            public BufferedListView()
            {
                DoubleBuffered = true;
            }
        }

        readonly TrayApp app;
        readonly Store store;
        readonly ListView list;
        readonly TextBox details;
        readonly Label statusLabel;
        readonly ContextMenuStrip rowMenu;

        public HistoryForm(TrayApp app, Store store)
        {
            this.app = app;
            this.store = store;
            int em = Ui.Setup(this, "t7drop", false);
            KeyPreview = true;
            AllowDrop = true;

            statusLabel = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 2 * em + em / 2, Padding = new Padding(0, 0, 0, em / 2) };
            list = new BufferedListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                AllowDrop = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            list.Columns.Add("When", 8 * em);
            list.Columns.Add("", 2 * em);
            list.Columns.Add("What", 26 * em);
            list.Columns.Add("Status", 14 * em);
            details = new TextBox
            {
                Dock = DockStyle.Bottom,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Height = 6 * em,
                BackColor = SystemColors.Window,
            };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, em / 2, 0, 0) };
            buttons.Controls.Add(Ui.Button("Send clipboard", em, () => app.SendClipboard(true)));
            buttons.Controls.Add(Ui.Button("Send files…", em, () => app.ChooseFilesToSend(this)));
            buttons.Controls.Add(Ui.Button("Open received files", em, app.OpenReceivedFolder));
            var splitter = new Panel { Dock = DockStyle.Bottom, Height = em / 2 };

            Padding = new Padding(em);
            Controls.Add(list);
            Controls.Add(splitter);
            Controls.Add(details);
            Controls.Add(buttons);
            Controls.Add(statusLabel);
            ClientSize = new Size(52 * em, 32 * em);
            MinimumSize = new Size(30 * em, 20 * em);

            rowMenu = new ContextMenuStrip();
            rowMenu.Opening += (sender, e) => e.Cancel = !FillMenu();
            list.ContextMenuStrip = rowMenu;
            list.SelectedIndexChanged += delegate { ShowDetails(); };
            list.ClientSizeChanged += delegate { FitColumns(); };
            list.DoubleClick += delegate { DefaultAction(); };
            KeyDown += OnKeyDown;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            list.DragEnter += OnDragEnter;
            list.DragDrop += OnDragDrop;
            RefreshRows();
            RefreshStatus();
        }

        /// <summary>"What" takes the width the other columns leave.</summary>
        void FitColumns()
        {
            int others = list.Columns[0].Width + list.Columns[1].Width + list.Columns[3].Width;
            int what = Math.Max(10 * Font.Height, list.ClientSize.Width - others);
            if (list.Columns[2].Width != what)
                list.Columns[2].Width = what;
        }

        // Drag and drop, keys

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length > 0)
                app.Send(new Outgoing { Paths = paths }, true);
            else
            {
                var text = e.Data.GetData(DataFormats.UnicodeText) as string;
                if (!string.IsNullOrEmpty(text))
                    app.Send(new Outgoing { Text = text }, true);
            }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V)
                app.SendClipboard(true);
            else if (e.Control && e.KeyCode == Keys.C && list.Focused)
                CopySelected();
            else if (e.KeyCode == Keys.Delete && list.Focused)
                RemoveSelected();
            else if (e.KeyCode == Keys.Enter && list.Focused)
                DefaultAction();
            else
                return;
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        // The list

        public void RefreshRows()
        {
            var rows = store.History().Select(r => new Row { In = r })
                .Concat(store.Outbox().Select(o => new Row { Out = o }))
                .OrderByDescending(r => r.Time).ToList();
            List<ListViewItem> items = list.Items.Cast<ListViewItem>().ToList();
            if (rows.Select(r => r.Key).SequenceEqual(items.Select(i => ((Row)i.Tag).Key)))
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    items[i].Tag = rows[i];
                    string[] texts = Texts(rows[i]);
                    for (int c = 0; c < texts.Length; c++)
                        if (items[i].SubItems[c].Text != texts[c])
                            items[i].SubItems[c].Text = texts[c];
                }
            }
            else
            {
                var selected = new HashSet<string>(list.SelectedItems.Cast<ListViewItem>().Select(i => ((Row)i.Tag).Key));
                string top = list.TopItem != null ? ((Row)list.TopItem.Tag).Key : null;
                list.BeginUpdate();
                list.Items.Clear();
                foreach (Row row in rows)
                {
                    var item = new ListViewItem(Texts(row)) { Tag = row };
                    if (selected.Contains(row.Key))
                        item.Selected = true;
                    list.Items.Add(item);
                }
                list.EndUpdate();
                ListViewItem topItem = list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((Row)i.Tag).Key == top);
                if (topItem != null && topItem.Index > 0)
                    list.TopItem = topItem;
            }
            ShowDetails();
        }

        public void RefreshStatus()
        {
            statusLabel.Text = app.StatusLine + "\n" + "Drop files or text here, or press Ctrl+V, to send them.";
        }

        string[] Texts(Row row)
        {
            string when = When(row.Time);
            if (row.In != null)
            {
                Received r = row.In;
                return r.IsText
                    ? new[] { when, "↓", Ui.FirstLine(r.Text, 120), "received" }
                    : new[] { when, "↓", r.Name + " (" + Names.Size(r.Size) + ")", File.Exists(r.Path) ? "saved" : "saved, moved since" };
            }
            OutItem o = row.Out;
            string what = o.Kind == "text" ? "“" + Ui.FirstLine(o.Preview, 118) + "”" : o.Name + " (" + Names.Size(o.Size) + ")";
            return new[] { when, "↑", what, OutStatus(o) };
        }

        string OutStatus(OutItem o)
        {
            if (o.CancelRequested && o.State != OutState.Delivered)
                return "cancelling";
            switch (o.State)
            {
                case OutState.Queued: return app.IsOnline ? "waiting" : "waiting (offline)";
                case OutState.Uploading: return "sending " + (o.CSize > 0 ? o.Sent * 100 / o.CSize : 0) + "%";
                case OutState.OnServer: return "on the server";
                case OutState.Delivered: return "delivered";
                default: return "failed: " + o.Error;
            }
        }

        static string When(long unixMs)
        {
            DateTime time = Clock.Local(unixMs);
            if (time.Date == DateTime.Today)
                return time.ToString("t", CultureInfo.CurrentCulture);
            return time.ToString(time.Year == DateTime.Today.Year ? "d MMM, HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        }

        List<Row> Selected()
        {
            return list.SelectedItems.Cast<ListViewItem>().Select(i => (Row)i.Tag).ToList();
        }

        void ShowDetails()
        {
            List<Row> rows = Selected();
            if (rows.Count != 1)
            {
                details.Text = rows.Count == 0 ? "" : rows.Count + " items selected.";
                return;
            }
            Row row = rows[0];
            string text;
            if (row.In != null)
            {
                Received r = row.In;
                string from = "From " + r.From + ", " + Clock.Local(r.Time).ToString("g", CultureInfo.CurrentCulture) + ".";
                text = r.IsText ? r.Text.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n\r\n" + from
                    : r.Path + "\r\n" + Names.Size(r.Size) + ". " + from;
            }
            else
            {
                OutItem o = row.Out;
                text = (o.Kind == "text" ? (o.Preview ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n") : o.Name) + "\r\n\r\n" +
                    "To " + store.DeviceName(o.To) + ", " + Names.Size(o.Size) + ": " + OutStatus(o) + "." +
                    (o.State == OutState.Failed ? " Right-click or double-click to send it again." : "");
            }
            if (details.Text != text)
                details.Text = text;
        }

        /// <summary>Fills the context menu for the selection; false if there is nothing to offer.</summary>
        bool FillMenu()
        {
            rowMenu.Items.Clear();
            List<Row> rows = Selected();
            if (rows.Count == 0)
                return false;
            if (rows.Count == 1)
            {
                Row row = rows[0];
                if (row.In != null && row.In.IsText)
                {
                    Received r = row.In;
                    AddItem("Copy", () => app.CopyText(r.Text), true);
                    string link = Links.FirstLink(r.Text);
                    if (link != null)
                        AddItem("Open link", () => Links.Open(link), false);
                }
                else if (row.In != null)
                {
                    Received r = row.In;
                    AddItem("Open", () => Links.OpenFile(r.Path), true);
                    AddItem("Show in folder", () => Links.ShowInFolder(r.Path), false);
                }
            }
            List<OutItem> failed = rows.Where(r => r.Out != null && r.Out.State == OutState.Failed).Select(r => r.Out).ToList();
            if (failed.Count > 0)
                AddItem(failed.Count == 1 ? "Send again" : "Send " + failed.Count + " items again", () => failed.ForEach(app.Retry), rows.Count == 1);
            List<OutItem> pending = rows.Where(r => r.Out != null && r.Out.IsPending && !r.Out.CancelRequested).Select(r => r.Out).ToList();
            if (pending.Count > 0)
                AddItem(pending.Count == 1 ? "Cancel sending" : "Cancel sending " + pending.Count + " items", () => pending.ForEach(app.Cancel), false);
            if (rows.Any(Removable))
            {
                if (rowMenu.Items.Count > 0)
                    rowMenu.Items.Add(new ToolStripSeparator());
                AddItem("Remove from list", RemoveSelected, false);
            }
            return rowMenu.Items.Count > 0;
        }

        void AddItem(string text, Action action, bool isDefault)
        {
            var item = new ToolStripMenuItem(text, null, delegate { action(); });
            if (isDefault)
                item.Font = new Font(item.Font, FontStyle.Bold);
            rowMenu.Items.Add(item);
        }

        static bool Removable(Row row)
        {
            return row.In != null || row.Out.State == OutState.Delivered || row.Out.State == OutState.Failed;
        }

        void DefaultAction()
        {
            List<Row> rows = Selected();
            if (rows.Count != 1)
                return;
            Row row = rows[0];
            if (row.In != null && row.In.IsText)
            {
                string link = Links.WholeLink(row.In.Text);
                if (link != null)
                    Links.Open(link);
                else
                    app.CopyText(row.In.Text);
            }
            else if (row.In != null)
                Links.OpenFile(row.In.Path);
            else if (row.Out.State == OutState.Failed)
                app.Retry(row.Out);
        }

        void CopySelected()
        {
            List<string> texts = Selected().Where(r => r.In != null && r.In.IsText).Select(r => r.In.Text).ToList();
            if (texts.Count > 0)
                app.CopyText(string.Join("\r\n\r\n", texts));
        }

        void RemoveSelected()
        {
            foreach (Row row in Selected().Where(Removable))
            {
                if (row.In != null)
                    store.RemoveReceived(row.In.Id);
                else
                    store.Remove(row.Out);
            }
        }
    }
}
