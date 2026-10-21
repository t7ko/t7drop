using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace T7Drop
{
    /// <summary>
    /// The tray icon and its menu, the windows, notifications, and what starts the worker again:
    /// waking from sleep (SleepWatcher, which also hears Modern Standby) and network changes.
    /// </summary>
    class TrayApp : ApplicationContext
    {
        const int MaxTooltipLength = 127;

        readonly SynchronizationContext ui;
        readonly Store store;
        Settings settings;
        readonly Worker worker;
        readonly NotifyIcon tray;
        readonly Icon[] icons;  // indexed by TrayState
        readonly ToolStripMenuItem pairItem, pairPhoneItem, updateItem;
        readonly System.Windows.Forms.Timer refreshTimer, receivedTimer, statusTimer, networkTimer, wakeTimer, updateTimer;
        readonly SleepWatcher sleepWatcher;
        readonly InstanceServer instanceServer;
        readonly List<Received> justReceived = new List<Received>();
        HistoryForm historyForm;
        SettingsForm settingsForm;
        PairForm pairForm;
        QrForm qrForm;
        Action balloonClick;  // what clicking the last notification does
        int pendingCount, failedCount;
        string recipientName;
        Release update;  // a newer release, found but not installed yet
        DateTime lastUpdateCheck = DateTime.MinValue;  // UTC
        bool checkingUpdate, installing, exiting;

        /// <param name="updated">Whether an update has just started this instance.</param>
        /// <param name="filesToSend">Files from Explorer's Send To.</param>
        public TrayApp(bool updated, string[] filesToSend)
        {
            ui = SynchronizationContext.Current;
            store = new Store(Paths.Home);
            Log.Open(store.Home);
            Log.Write("t7drop " + Updater.Text(Updater.CurrentVersion) + " started" + (Paths.IsTestCopy ? " (a test copy)" : ""));
            Sodium.Load(store.Home);
            settings = Settings.Load(store.Home);
            if (settings == null)
                FirstStart();
            else if (!Paths.IsTestCopy && settings.SendToMenu)
                TrySetSendTo(true);  // points the shortcut here again if the exe was moved
            store.CleanUp();

            int size = SystemInformation.SmallIconSize.Width;
            icons = Enum.GetValues(typeof(TrayState)).Cast<TrayState>().Select(state => Icons.CreateIcon(state, size)).ToArray();

            var historyItem = new ToolStripMenuItem("History…", null, delegate { ShowHistory(); });
            historyItem.Font = new Font(historyItem.Font, FontStyle.Bold);  // what a click does
            pairItem = new ToolStripMenuItem("Pair this computer…", null, delegate { ShowPair(); });
            pairPhoneItem = new ToolStripMenuItem("Pair a phone…", null, delegate { ShowQr(); });
            updateItem = new ToolStripMenuItem("Check for updates", null, delegate { OnUpdateItem(); });
            var menu = new ContextMenuStrip();
            menu.Items.Add(historyItem);
            menu.Items.Add("Send clipboard", null, delegate { SendClipboard(false); });
            menu.Items.Add("Send files…", null, delegate { ChooseFilesToSend(null); });
            menu.Items.Add("Open received files", null, delegate { OpenReceivedFolder(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(pairItem);
            menu.Items.Add(pairPhoneItem);
            menu.Items.Add("Settings…", null, delegate { ShowSettings(); });
            menu.Items.Add(updateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });
            menu.Opening += delegate { UpdateMenu(); };

            tray = new NotifyIcon { Icon = icons[(int)TrayState.Offline], ContextMenuStrip = menu };
            tray.MouseClick += (sender, e) => { if (e.Button == MouseButtons.Left) ShowHistory(); };
            tray.BalloonTipClicked += delegate
            {
                Action click = balloonClick;
                balloonClick = null;
                if (click != null)
                    click();
            };
            tray.Visible = true;
            SetTooltip("t7drop: starting…");
            if (updated)
                Balloon("t7drop updated", "This is version " + Updater.Text(Updater.CurrentVersion) + ".", null);

            refreshTimer = NewTimer(250, RefreshViews);
            receivedTimer = NewTimer(400, AnnounceReceived);
            networkTimer = NewTimer(2000, () => worker.Restart());
            wakeTimer = NewTimer(1000, () => worker.Restart());
            statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            statusTimer.Tick += delegate { UpdateStatus(); };  // the countdown to the next try

            worker = new Worker(store, () => settings.ReceiveFolder);
            worker.StatusChanged += () => ui.Post(delegate { if (!exiting) UpdateStatus(); }, null);
            worker.Received += item => ui.Post(delegate { if (!exiting) OnReceived(item); }, null);
            store.Changed += () => ui.Post(delegate { if (!exiting) Restart(refreshTimer); }, null);
            sleepWatcher = new SleepWatcher((sleeping, time) => ui.Post(delegate { OnSleepChanged(sleeping); }, null));
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            instanceServer = new InstanceServer((command, lines) => ui.Post(delegate { OnInstanceRequest(command, lines); }, null));

            // The first update check waits a minute, so that it does not slow down logging in.
            // Then the timer ticks hourly, and CheckForUpdateDaily goes to GitHub once a day.
            updateTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
            updateTimer.Tick += delegate
            {
                updateTimer.Interval = 60 * 60 * 1000;
                CheckForUpdateDaily();
            };
            updateTimer.Start();

            worker.Start();
            statusTimer.Start();
            RefreshViews();
            if (store.LoadConfig() == null)
                ShowPair();
            if (filesToSend.Length > 0)
                Send(new Outgoing { Paths = filesToSend }, false);
        }

        static System.Windows.Forms.Timer NewTimer(int interval, Action tick)
        {
            var timer = new System.Windows.Forms.Timer { Interval = interval };
            timer.Tick += delegate
            {
                timer.Stop();
                tick();
            };
            return timer;
        }

        static void Restart(System.Windows.Forms.Timer timer)
        {
            timer.Stop();
            timer.Start();
        }

        /// <summary>The first start: Start with Windows and the Send To entry are on (the settings turn them off).</summary>
        void FirstStart()
        {
            settings = new Settings();
            settings.Save(store.Home);
            if (Paths.IsTestCopy)
                return;
            try
            {
                Autostart.Set(true);
            }
            catch (Exception e)
            {
                Log.Write("could not turn on Start with Windows: " + e.Message);
            }
            TrySetSendTo(true);
        }

        static void TrySetSendTo(bool enabled)
        {
            try
            {
                SendTo.Set(enabled);
            }
            catch (Exception e)
            {
                Log.Write("could not change the Send To entry: " + e.Message);
            }
        }

        void OnSleepChanged(bool sleeping)
        {
            if (exiting)
                return;
            Log.Write(sleeping ? "Windows goes to sleep" : "Windows woke up");
            if (!sleeping)
                Restart(wakeTimer);  // standby flips several times: one restart after it settles
        }

        void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            ui.Post(delegate
            {
                if (!exiting)
                    Restart(networkTimer);  // the event comes several times per change
            }, null);
        }

        void OnInstanceRequest(string command, List<string> lines)
        {
            if (exiting)
                return;
            if (command == "send" && lines.Count > 0)
                Send(new Outgoing { Paths = lines.ToArray() }, false);
            else if (command == "show")
                ShowHistory();
            else if (command == "exit")
                ExitThread();  // T7Drop.exe --exit, for scripts and tests
        }

        // State

        public bool IsOnline
        {
            get { return worker.State == Connection.Online; }
        }

        /// <summary>One line for the tooltip and the history window.</summary>
        public string StatusLine
        {
            get
            {
                switch (worker.State)
                {
                    case Connection.NotPaired:
                        return "Not paired: choose Pair this computer in the menu.";
                    case Connection.Connecting:
                        return "Connecting to the server…";
                    case Connection.Online:
                        return worker.Activity ?? (recipientName == null ? "Connected." : "Connected; sends to " + recipientName + ".");
                    default:
                        int seconds = (int)Math.Ceiling((worker.NextTry - DateTime.UtcNow).TotalSeconds);
                        return "The server cannot be reached (" + worker.Problem + "); trying again" +
                            (seconds > 0 ? " in " + (seconds < 90 ? seconds + " s" : (seconds + 30) / 60 + " min") : "") + ".";
                }
            }
        }

        void RefreshViews()
        {
            List<OutItem> outbox = store.Outbox();
            pendingCount = outbox.Count(i => i.IsPending);
            failedCount = outbox.Count(i => i.State == OutState.Failed);
            Config config = store.LoadConfig();
            string to = config == null ? null : store.Recipient(config, settings.SendTo);
            recipientName = to == null ? null : store.DeviceName(to);
            UpdateStatus();
            if (historyForm != null)
                historyForm.RefreshRows();
        }

        void UpdateStatus()
        {
            if (exiting)
                return;
            Connection connection = worker.State;
            TrayState state = connection == Connection.NotPaired ? TrayState.Attention
                : connection != Connection.Online ? TrayState.Offline
                : failedCount > 0 ? TrayState.Attention
                : worker.Activity != null || pendingCount > 0 ? TrayState.Busy
                : TrayState.Online;
            if (tray.Icon != icons[(int)state])
                tray.Icon = icons[(int)state];
            string tip = "t7drop: " + StatusLine;
            if (pendingCount > 0)
                tip += "\n" + pendingCount + (pendingCount == 1 ? " item" : " items") + " to send";
            if (failedCount > 0)
                tip += "\n" + failedCount + " not sent: see History";
            SetTooltip(tip);
            if (historyForm != null)
                historyForm.RefreshStatus();
        }

        /// <summary>
        /// NotifyIcon.Text refuses more than 63 characters on .NET Framework, though Windows shows
        /// 127, so this sets its private field instead (as ping-tray does).
        /// </summary>
        void SetTooltip(string text)
        {
            if (text.Length > MaxTooltipLength)
                text = text.Substring(0, MaxTooltipLength - 1) + "…";
            const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo textField = typeof(NotifyIcon).GetField("text", hidden);
            FieldInfo addedField = typeof(NotifyIcon).GetField("added", hidden);
            MethodInfo updateIcon = typeof(NotifyIcon).GetMethod("UpdateIcon", hidden);
            if (textField != null && addedField != null && updateIcon != null)
            {
                if ((string)textField.GetValue(tray) == text)
                    return;
                textField.SetValue(tray, text);
                if ((bool)addedField.GetValue(tray))
                    updateIcon.Invoke(tray, new object[] { tray.Visible });
            }
            else
                tray.Text = text.Length <= 63 ? text : text.Substring(0, 62) + "…";
        }

        void UpdateMenu()
        {
            Config config = store.LoadConfig();
            pairItem.Visible = config == null;
            pairPhoneItem.Visible = config != null && config.First;
        }

        void Balloon(string title, string text, Action click)
        {
            balloonClick = click;
            tray.ShowBalloonTip(10000, Ui.Shorten(title, 60), Ui.Shorten(text, 250), ToolTipIcon.None);
        }

        // Receiving

        void OnReceived(Received item)
        {
            if (item.IsText && settings.AutoCopy)
                ClipboardAccess.CopyText(item.Text);
            justReceived.Add(item);
            Restart(receivedTimer);  // several items at once get one notification
        }

        void AnnounceReceived()
        {
            List<Received> items = justReceived.ToList();
            justReceived.Clear();
            if (items.Count == 1 && items[0].IsText)
            {
                Received r = items[0];
                string link = Links.WholeLink(r.Text);
                bool copied = settings.AutoCopy;
                Balloon((link != null ? "Link" : "Text") + " from " + r.From + (copied ? " (copied)" : ""),
                    Ui.Shorten(r.Text, 180) + (link != null ? "\nClick to open it." : copied ? "" : "\nClick to copy it."),
                    link != null ? (Action)(() => Links.Open(link)) : () => CopyText(r.Text));
            }
            else if (items.Count == 1)
            {
                Received r = items[0];
                Balloon("Received from " + r.From, r.Name + " (" + Names.Size(r.Size) + ")\nClick to show it in its folder.",
                    () => Links.ShowInFolder(r.Path));
            }
            else if (items.Count > 1)
                Balloon("Received " + items.Count + " items from " + items[0].From,
                    string.Join("\n", items.Take(4).Select(r => r.IsText ? Ui.FirstLine(r.Text, 50) : r.Name)) + "\nClick to see them.",
                    ShowHistory);
        }

        public void CopyText(string text)
        {
            if (!ClipboardAccess.CopyText(text))
                Balloon("Not copied", "Another program is holding the clipboard. Try again.", null);
        }

        public void OpenReceivedFolder()
        {
            Links.OpenFolder(settings.ReceiveFolder);
        }

        // Sending

        public void SendClipboard(bool quiet)
        {
            Outgoing what;
            try
            {
                what = ClipboardAccess.Read();
            }
            catch (ExternalException)
            {
                Balloon("Not sent", "Another program is holding the clipboard. Try again.", null);
                return;
            }
            if (what == null || what.IsEmpty)
            {
                Balloon("Nothing to send", "The clipboard holds no text, picture or files.", null);
                return;
            }
            Send(what, quiet);
        }

        /// <param name="owner">The window to show the dialog over; null from the tray menu.</param>
        public void ChooseFilesToSend(IWin32Window owner)
        {
            using (var dialog = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = "Send files with t7drop" })
            {
                DialogResult result;
                if (owner != null)
                    result = dialog.ShowDialog(owner);
                else
                    using (var anchor = new Form { ShowInTaskbar = false, TopMost = true, StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 })
                    {
                        anchor.Show();  // so that the dialog comes to the front
                        result = dialog.ShowDialog(anchor);
                    }
                if (result == DialogResult.OK)
                    Send(new Outgoing { Paths = dialog.FileNames }, owner != null);
            }
        }

        /// <summary>Encrypts and queues; quiet (from the history window) shows a notification only for problems.</summary>
        public async void Send(Outgoing what, bool quiet)
        {
            if (store.LoadConfig() == null)
            {
                ShowPair();
                return;
            }
            Settings used = settings;
            QueueResult result;
            try
            {
                result = await Task.Run(() => Sender.Queue(store, used, what));
            }
            catch (Exception e)
            {
                Log.Write("could not queue: " + e);
                result = new QueueResult { Error = "Not sent: " + e.Message };
            }
            if (exiting)
                return;
            if (result.Queued > 0)
                worker.Kick();
            if (!quiet || result.Error != null || result.Problems.Count > 0)
                Balloon(result.Queued == 0 ? "Not sent" : "t7drop", result.Summary, ShowHistory);
        }

        public void Retry(OutItem item)
        {
            Config config = store.LoadConfig();
            if (config == null)
                return;
            try
            {
                new Transfers(store, config, () => settings.ReceiveFolder).Retry(item, store.Recipient(config, settings.SendTo));
            }
            catch (Exception e)
            {
                MessageBox.Show("Could not send it again:\n" + e.Message, "t7drop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            worker.Kick();
        }

        public void Cancel(OutItem item)
        {
            item.Update(d => d["cancel"] = true);  // the worker deletes it on the server, then here
            store.Touch();
            worker.Kick();
        }

        // Windows

        void ShowHistory()
        {
            if (historyForm == null)
            {
                historyForm = new HistoryForm(this, store);
                historyForm.FormClosed += delegate { historyForm = null; };
                historyForm.Show();
            }
            if (historyForm.WindowState == FormWindowState.Minimized)
                historyForm.WindowState = FormWindowState.Normal;
            historyForm.Activate();
        }

        void ShowSettings()
        {
            if (settingsForm == null)
            {
                Config config = store.LoadConfig();
                settingsForm = new SettingsForm(store, settings,
                    config != null && config.First ? (Action)ShowQr : null, config != null ? (Action)Unpair : null);
                settingsForm.Saved += saved =>
                {
                    settings = saved;
                    RefreshViews();
                    CheckForUpdateDaily();  // in case updates were just turned on
                };
                settingsForm.FormClosed += delegate { settingsForm = null; };
                settingsForm.Show();
            }
            settingsForm.Activate();
        }

        void ShowPair()
        {
            if (store.LoadConfig() != null)
                return;
            if (pairForm == null)
            {
                pairForm = new PairForm(store);
                pairForm.Paired += config =>
                {
                    worker.Kick();
                    RefreshViews();
                    if (config.First)
                        ui.Post(delegate { ShowQr(); }, null);  // after the pairing window has closed
                };
                pairForm.FormClosed += delegate { pairForm = null; };
                pairForm.Show();
            }
            pairForm.Activate();
        }

        void ShowQr()
        {
            Config config = store.LoadConfig();
            if (config == null || !config.First)
                return;
            if (qrForm == null)
            {
                qrForm = new QrForm(store, config);
                qrForm.DevicePaired += name =>
                {
                    RefreshViews();
                    worker.Kick();
                };
                qrForm.FormClosed += delegate { qrForm = null; };
                qrForm.Show();
            }
            qrForm.Activate();
        }

        void Unpair()
        {
            const string question = "Unpair this computer? It forgets its pairing and the items still waiting to be sent. " +
                "Pairing it again takes a new code from t7drop-admin, and then the phone pairs again with its QR code.\n\n" +
                "The server keeps this computer in its list until you revoke it with t7drop-admin.";
            if (MessageBox.Show(question, "t7drop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            store.Forget();
            Log.Write("unpaired");
            worker.Restart();
            RefreshViews();
        }

        // Updates

        /// <summary>Once a day, unless updates are off: installs a newer release, or offers it.</summary>
        async void CheckForUpdateDaily()
        {
            if (settings.Updates == UpdateMode.Off || checkingUpdate || installing
                || DateTime.UtcNow - lastUpdateCheck < TimeSpan.FromHours(23))
                return;
            checkingUpdate = true;
            Release found;
            try
            {
                found = await Task.Run(() => Updater.FindUpdateAsync());
            }
            catch (Exception)
            {
                return;  // offline, or GitHub is unreachable: the next hourly tick tries again
            }
            finally
            {
                checkingUpdate = false;
            }
            lastUpdateCheck = DateTime.UtcNow;
            if (found == null || exiting)
                return;
            bool offeredBefore = update != null && update.Version == found.Version;
            Offer(found);
            if (settings.Updates == UpdateMode.Auto)
                InstallUpdate(false);
            else if (!offeredBefore)
                Balloon("t7drop " + Updater.Text(found.Version) + " is available", "Click here to install it.", () => InstallUpdate(true));
        }

        /// <summary>The menu's update item: installs the release on offer, or else checks GitHub now.</summary>
        async void OnUpdateItem()
        {
            if (update != null)
            {
                InstallUpdate(true);
                return;
            }
            if (checkingUpdate || installing)
                return;
            checkingUpdate = true;
            Release found;
            try
            {
                found = await Task.Run(() => Updater.FindUpdateAsync());
            }
            catch (Exception e)
            {
                MessageBox.Show("Could not check for updates:\n" + e.Message, "t7drop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                checkingUpdate = false;
            }
            lastUpdateCheck = DateTime.UtcNow;
            string current = Updater.Text(Updater.CurrentVersion);
            if (found == null)
            {
                MessageBox.Show("t7drop " + current + " is the latest version.", "t7drop", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Offer(found);
            string question = "t7drop " + Updater.Text(found.Version) + " is available; this is " + current +
                ".\n\nInstall it now? t7drop restarts; transfers continue after it.";
            if (MessageBox.Show(question, "t7drop", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                InstallUpdate(true);
        }

        void Offer(Release release)
        {
            update = release;
            updateItem.Text = "Install version " + Updater.Text(release.Version);
        }

        /// <summary>
        /// Downloads the release on offer, puts it in place of this exe, starts it and exits.
        /// Errors go to a message box when the user asked for the update, otherwise to a notification.
        /// </summary>
        async void InstallUpdate(bool userAsked)
        {
            if (installing || update == null)
                return;
            installing = true;
            Release release = update;
            string exe = Application.ExecutablePath;
            try
            {
                string downloaded = await Task.Run(() => Updater.DownloadAsync(release, exe));
                if (exiting)
                    return;
                Log.Write("installing version " + Updater.Text(release.Version));
                Updater.Install(exe, downloaded);
            }
            catch (Exception e)
            {
                installing = false;
                string reason = e is UnauthorizedAccessException
                    ? "Windows does not let it replace " + exe + ". Move T7Drop.exe to a folder " +
                        "you can write to, such as %LOCALAPPDATA%\\Programs."
                    : e.Message;
                Log.Write("update failed: " + reason);
                if (userAsked)
                    MessageBox.Show("Could not update t7drop:\n" + reason, "t7drop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    Balloon("t7drop could not update itself", reason, null);
                return;
            }
            ExitThread();  // the new version waits for this process to end
        }

        protected override void ExitThreadCore()
        {
            exiting = true;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            sleepWatcher.Dispose();
            foreach (System.Windows.Forms.Timer timer in new[] { refreshTimer, receivedTimer, statusTimer, networkTimer, wakeTimer, updateTimer })
                timer.Stop();
            instanceServer.Dispose();
            worker.Stop();
            foreach (Form form in new Form[] { historyForm, settingsForm, pairForm, qrForm })
                if (form != null)
                    form.Close();
            tray.Visible = false;  // otherwise the icon stays until the mouse passes over it
            tray.Dispose();
            Log.Write("exited");
            base.ExitThreadCore();
        }
    }
}
