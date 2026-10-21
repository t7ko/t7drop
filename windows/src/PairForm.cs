using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace T7Drop
{
    /// <summary>
    /// Pairs this computer: as the first device of a group, with a one-time code from
    /// t7drop-admin and a new group key; or with an invitation (t7drop:pair?...) from a device
    /// that is paired already, whose group key it takes.
    /// </summary>
    class PairForm : Form
    {
        /// <summary>Raised once this computer is paired and the pairing is saved.</summary>
        public event Action<Config> Paired;

        readonly Store store;
        readonly TextBox urlBox, codeBox, nameBox;
        readonly Button pairButton, cancelButton;
        readonly Label statusLabel;

        public PairForm(Store store)
        {
            this.store = store;
            int em = Ui.Setup(this, "Pair t7drop", true);
            int width = 30 * em;
            urlBox = new TextBox { Width = 22 * em, Anchor = AnchorStyles.Left };
            codeBox = new TextBox { Width = 22 * em, Anchor = AnchorStyles.Left };
            nameBox = new TextBox { Width = 22 * em, Anchor = AnchorStyles.Left, Text = Environment.MachineName };
            statusLabel = new Label { AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(3, em / 2, 3, 0) };
            pairButton = Ui.Button("Pair", em, OnPair);
            cancelButton = Ui.Button("Cancel", em, Close);

            TableLayoutPanel layout = Ui.Table(2, em);
            Ui.AddWide(layout, Ui.Paragraph("Pair this computer with your t7drop server. On the server, run " +
                "\"t7drop-admin invite <name>\": it prints a one-time code that works for 10 minutes. Afterwards this " +
                "computer shows a QR code for pairing the phone.", width));
            Ui.AddWide(layout, Ui.Paragraph("To join the devices of a computer that is paired already, paste its " +
                "invitation (t7drop:pair?…) as the code instead; the server address then comes from it.", width));
            Ui.AddRow(layout, Ui.Caption("Server address"), urlBox);
            Ui.AddRow(layout, Ui.Caption("Pairing code"), codeBox);
            Ui.AddRow(layout, Ui.Caption("This computer's name"), nameBox);
            Ui.AddWide(layout, statusLabel);
            Ui.AddWide(layout, Ui.ButtonRow(em, cancelButton, pairButton));
            Ui.Fit(this, layout, em);
            AcceptButton = pairButton;
            CancelButton = cancelButton;
            urlBox.Text = "https://";
            Shown += delegate
            {
                urlBox.Focus();
                urlBox.Select(urlBox.TextLength, 0);
            };
        }

        async void OnPair()
        {
            string url = urlBox.Text.Trim().TrimEnd('/');
            string code = codeBox.Text.Trim();
            string name = nameBox.Text.Trim();
            Invitation invitation = null;
            if (code.StartsWith(Pairing.Prefix, StringComparison.Ordinal))
            {
                try
                {
                    invitation = Pairing.Parse(code);
                }
                catch (FormatException e)
                {
                    ShowProblem(e.Message);
                    return;
                }
                url = invitation.Url;
                code = invitation.Code;
            }
            else if (!Api.IsServerUrl(url))
            {
                ShowProblem("The server address is the https:// address of your t7drop server, with its path.");
                urlBox.Focus();
                return;
            }
            if (code.Length == 0)
            {
                ShowProblem("Enter the code that t7drop-admin printed.");
                codeBox.Focus();
                return;
            }
            if (name.Length == 0)
                name = Environment.MachineName;
            SetBusy(true);
            try
            {
                Dictionary<string, object> reply = await Task.Run(() => new Api(url, null).Pair(code, name, CancellationToken.None));
                var config = new Config
                {
                    Url = url, DeviceId = Json.Str(reply, "device_id"), DeviceName = Json.Str(reply, "name"),
                    Token = Json.Str(reply, "token"), Key = invitation != null ? invitation.Key : ItemCrypto.NewKey(),
                    First = invitation == null, Paired = Clock.NowMs,
                };
                store.SaveConfig(config);
                Log.Write("paired as " + config.DeviceId + (config.First ? ", the first device" : ""));
                Action<Config> handler = Paired;
                if (handler != null)
                    handler(config);
                Close();
            }
            catch (TransientException e)
            {
                ShowProblem(e.Message.Contains("without t7drop's header")
                    ? "The server did not take this code. A code works once, for 10 minutes; check it and the address. (" + e.Message + ")"
                    : "The server could not be reached: " + e.Message);
                SetBusy(false);
            }
            catch (ApiException e)
            {
                ShowProblem("The server refused: " + e.Code);
                SetBusy(false);
            }
        }

        void SetBusy(bool busy)
        {
            pairButton.Enabled = !busy;
            urlBox.Enabled = codeBox.Enabled = nameBox.Enabled = !busy;
            if (busy)
            {
                statusLabel.ForeColor = SystemColors.GrayText;
                statusLabel.Text = "Pairing…";
            }
        }

        void ShowProblem(string text)
        {
            statusLabel.ForeColor = Color.Firebrick;
            statusLabel.Text = text;
        }
    }
}
