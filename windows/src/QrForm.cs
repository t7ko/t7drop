using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace T7Drop
{
    /// <summary>
    /// Pairs a phone: asks the server for a one-time code and shows the invitation
    /// (t7drop:pair?..., with the group key) as a QR code for 10 minutes. It watches the device
    /// list and says when a new device has paired.
    /// </summary>
    class QrForm : Form
    {
        /// <summary>Raised with the new device's name when it has paired.</summary>
        public event Action<string> DevicePaired;

        readonly Store store;
        readonly Config config;
        readonly PictureBox picture;
        readonly Label timeLabel;
        readonly Button newCodeButton;
        readonly System.Windows.Forms.Timer timer;
        readonly int side;
        DateTime expires;
        HashSet<string> knownDevices = new HashSet<string>();
        bool polling, closed;
        int ticks;

        public QrForm(Store store, Config config)
        {
            this.store = store;
            this.config = config;
            int em = Ui.Setup(this, "Pair a phone with t7drop", true);
            side = 22 * em;
            picture = new PictureBox
            {
                Size = new Size(side, side),
                SizeMode = PictureBoxSizeMode.CenterImage,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.None,
            };
            timeLabel = new Label { AutoSize = true, MaximumSize = new Size(side, 0), Anchor = AnchorStyles.None, Margin = new Padding(3, em / 2, 3, 0) };
            newCodeButton = Ui.Button("New code", em, NewCode);
            Button close = Ui.Button("Close", em, Close);
            TableLayoutPanel layout = Ui.Table(1, em);
            Ui.AddWide(layout, Ui.Paragraph("In t7drop on the phone, tap Pair (after Unpair in its menu, if it is " +
                "paired with another computer) and scan this code. The code works once, for 10 minutes.", side));
            Ui.AddWide(layout, Ui.Paragraph("It holds the key that encrypts everything between your devices: show it " +
                "only to your own phone.", side));
            Ui.AddWide(layout, picture);
            Ui.AddWide(layout, timeLabel);
            Ui.AddWide(layout, Ui.ButtonRow(em, close, newCodeButton));
            Ui.Fit(this, layout, em);
            CancelButton = close;
            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += delegate { OnTick(); };
            Shown += delegate { NewCode(); };
            FormClosed += delegate
            {
                closed = true;
                timer.Stop();
                SetImage(null);
            };
        }

        async void NewCode()
        {
            newCodeButton.Enabled = false;
            timer.Stop();
            SetImage(null);
            timeLabel.ForeColor = SystemColors.ControlText;
            timeLabel.Text = "Asking the server for a code…";
            var api = new Api(config.Url, config.Token);
            try
            {
                object[] before = null;
                Dictionary<string, object> reply = await Task.Run(() =>
                {
                    before = api.Devices(CancellationToken.None);
                    return api.Invite(CancellationToken.None);
                });
                if (closed)
                    return;
                knownDevices = new HashSet<string>(before.OfType<Dictionary<string, object>>().Select(d => Json.Str(d, "id")));
                string invitation = Pairing.Make(config.Url, Json.Str(reply, "code"), config.Key);
                QrCode qr = QrCode.EncodeBytes(Encoding.UTF8.GetBytes(invitation), Ecc.Medium);
                SetImage(qr.ToBitmap(Math.Max(2, side / (qr.Size + 8)), 4));
                expires = DateTime.UtcNow.AddSeconds(Json.Long(reply, "expires_in", 600));
                ticks = 0;
                ShowTimeLeft();
                timer.Start();
            }
            catch (ApiException e)
            {
                ShowProblem(e.Code == "not_allowed"
                    ? "Only a computer paired with a code from t7drop-admin may pair other devices."
                    : e.Code == "too_many_invites"
                        ? "Three codes are waiting unused. Each expires 10 minutes after it was made; try again then."
                        : "The server refused: " + e.Code);
            }
            catch (TransientException e)
            {
                ShowProblem("The server could not be reached: " + e.Message);
            }
            finally
            {
                if (!closed)
                    newCodeButton.Enabled = true;
            }
        }

        void OnTick()
        {
            if (DateTime.UtcNow >= expires)
            {
                timer.Stop();
                SetImage(null);
                ShowProblem("The code has expired. Click New code for another one.");
                return;
            }
            ShowTimeLeft();
            if (++ticks % 3 == 0)
                PollDevices();
        }

        void ShowTimeLeft()
        {
            TimeSpan left = expires - DateTime.UtcNow;
            timeLabel.ForeColor = SystemColors.ControlText;
            timeLabel.Text = "The code works for another " + (int)left.TotalMinutes + ":" + left.Seconds.ToString("00") + ".";
        }

        async void PollDevices()
        {
            if (polling)
                return;
            polling = true;
            try
            {
                var api = new Api(config.Url, config.Token);
                object[] devices = await Task.Run(() => api.Devices(CancellationToken.None));
                if (closed)
                    return;
                store.SaveDevices(devices);
                Dictionary<string, object> added = devices.OfType<Dictionary<string, object>>().FirstOrDefault(d =>
                    !knownDevices.Contains(Json.Str(d, "id")) && Json.Str(d, "id") != config.DeviceId);
                if (added == null)
                    return;
                timer.Stop();
                SetImage(null);
                string name = Json.Str(added, "name");
                timeLabel.ForeColor = Color.DarkGreen;
                timeLabel.Text = "Paired: " + name + ". t7drop now sends to it.";
                Log.Write("a device paired with this computer's code: " + Json.Str(added, "id"));
                Action<string> handler = DevicePaired;
                if (handler != null)
                    handler(name);
            }
            catch (Exception)
            {
                // offline for a moment: the next tick asks again
            }
            finally
            {
                polling = false;
            }
        }

        void ShowProblem(string text)
        {
            timeLabel.ForeColor = Color.Firebrick;
            timeLabel.Text = text;
        }

        void SetImage(Image image)
        {
            Image old = picture.Image;
            picture.Image = image;
            if (old != null)
                old.Dispose();
        }
    }
}
