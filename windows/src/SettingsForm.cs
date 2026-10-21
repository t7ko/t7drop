using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace T7Drop
{
    /// <summary>The download folder, auto-copy, Start with Windows, Send To, the recipient, updates; pairing.</summary>
    class SettingsForm : Form
    {
        public event Action<Settings> Saved;

        readonly Store store;
        readonly Settings settings;
        readonly TextBox folderBox;
        readonly CheckBox autoCopyBox, autostartBox, sendToBox;
        readonly ComboBox recipientBox, updatesBox;
        readonly List<string> recipientIds = new List<string>();
        readonly bool autostartWas, sendToWas;

        /// <param name="pairPhone">Opens the QR code window; null when this computer may not invite.</param>
        /// <param name="unpair">Asks, then unpairs; null when not paired.</param>
        public SettingsForm(Store store, Settings current, Action pairPhone, Action unpair)
        {
            this.store = store;
            settings = Settings.Parse(current.Format());
            int em = Ui.Setup(this, "t7drop " + Updater.Text(Updater.CurrentVersion) + " settings", true);
            int width = 30 * em;

            folderBox = new TextBox { ReadOnly = true, Width = 20 * em, Text = settings.ReceiveFolder, Anchor = AnchorStyles.Left };
            Button change = Ui.Button("Change…", em, ChooseFolder);
            change.MinimumSize = new Size(5 * em, 0);
            var folderRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            folderRow.Controls.Add(folderBox);
            folderRow.Controls.Add(change);

            autoCopyBox = new CheckBox { Text = "Copy received texts to the clipboard at once", AutoSize = true, Checked = settings.AutoCopy };
            autostartWas = !Paths.IsTestCopy && Autostart.IsEnabled;
            autostartBox = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = autostartWas, Enabled = !Paths.IsTestCopy };
            sendToWas = !Paths.IsTestCopy && SendTo.IsEnabled;
            sendToBox = new CheckBox { Text = "Show t7drop in Explorer's Send To menu", AutoSize = true, Checked = sendToWas, Enabled = !Paths.IsTestCopy };

            Config config = store.LoadConfig();
            recipientBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 20 * em, Anchor = AnchorStyles.Left };
            List<Device> others = config == null ? new List<Device>()
                : store.Devices().Where(d => d.Id != config.DeviceId).OrderByDescending(d => d.Created).ToList();
            string usual = config == null ? null : store.Recipient(config, null);
            recipientBox.Items.Add("Usually: " + (usual == null ? "the device paired last" : store.DeviceName(usual)));
            recipientIds.Add(null);
            foreach (Device device in others)
            {
                recipientBox.Items.Add(device.Name + " (" + device.Id + ")");
                recipientIds.Add(device.Id);
            }
            recipientBox.SelectedIndex = Math.Max(0, recipientIds.IndexOf(settings.SendTo));

            updatesBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 14 * em, Anchor = AnchorStyles.Left };
            updatesBox.Items.AddRange(new object[] { "Install automatically", "Ask before installing", "Don't check" });  // in UpdateMode order
            updatesBox.SelectedIndex = (int)settings.Updates;

            Button save = Ui.Button("Save", em, OnSave);
            Button cancel = Ui.Button("Cancel", em, Close);

            TableLayoutPanel layout = Ui.Table(2, em);
            Ui.AddWide(layout, Ui.Caption("Received files go to:"));
            Ui.AddWide(layout, folderRow);
            Ui.AddWide(layout, autoCopyBox);
            Ui.AddWide(layout, autostartBox);
            Ui.AddWide(layout, sendToBox);
            Label sendLabel = Ui.Caption("Send to");
            Ui.AddRow(layout, sendLabel, recipientBox);
            Label updatesLabel = Ui.Caption("Updates");
            Ui.AddRow(layout, updatesLabel, updatesBox);
            sendLabel.Margin = recipientBox.Margin = new Padding(3, em / 2, 3, 3);

            string pairing = config == null ? "This computer is not paired."
                : "Paired as " + config.DeviceName + " (" + config.DeviceId + ") with " + Host(config.Url) + ".";
            Label pairingLabel = Ui.Paragraph(pairing, width);
            pairingLabel.Margin = new Padding(3, em, 3, 3);
            Ui.AddWide(layout, pairingLabel);
            var pairingButtons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            if (pairPhone != null)
                pairingButtons.Controls.Add(Ui.Button("Pair a phone…", em, () => { Close(); pairPhone(); }));
            if (unpair != null)
                pairingButtons.Controls.Add(Ui.Button("Unpair…", em, () => { Close(); unpair(); }));
            Ui.AddWide(layout, pairingButtons);
            string homePage = Links.HomePage.Replace("https://", "");
            LinkLabel home = Ui.Link("Home page: " + homePage, homePage, () => Links.Open(Links.HomePage));
            home.Margin = new Padding(3, em, 3, 0);
            Ui.AddWide(layout, home);
            Ui.AddWide(layout, Ui.ButtonRow(em, cancel, save));
            Ui.Fit(this, layout, em);
            AcceptButton = save;
            CancelButton = cancel;
            Shown += delegate { autoCopyBox.Focus(); };  // not the folder box, whose text would show selected
        }

        static string Host(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host : url;
        }

        void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Where t7drop saves the files it receives",
                SelectedPath = folderBox.Text,
                ShowNewFolderButton = true,
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    folderBox.Text = dialog.SelectedPath;
            }
        }

        void OnSave()
        {
            settings.DownloadFolder = string.Equals(folderBox.Text, Paths.DefaultDownloads, StringComparison.OrdinalIgnoreCase)
                ? null : folderBox.Text;
            settings.AutoCopy = autoCopyBox.Checked;
            settings.SendToMenu = sendToBox.Checked;
            settings.SendTo = recipientIds[recipientBox.SelectedIndex];
            settings.Updates = (UpdateMode)updatesBox.SelectedIndex;
            try
            {
                settings.Save(store.Home);
                if (!Paths.IsTestCopy && autostartBox.Checked != autostartWas)
                    Autostart.Set(autostartBox.Checked);
                if (!Paths.IsTestCopy && sendToBox.Checked != sendToWas)
                    SendTo.Set(sendToBox.Checked);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Could not save the settings:\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Action<Settings> handler = Saved;
            if (handler != null)
                handler(settings);
            Close();
        }
    }
}
