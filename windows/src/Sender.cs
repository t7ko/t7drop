using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace T7Drop
{
    /// <summary>What came of queueing: how many items, for whom, and what could not be queued.</summary>
    class QueueResult
    {
        public int Queued;
        public string Recipient;
        public List<string> Problems = new List<string>();
        public string Error;  // nothing was queued, and why

        public string Summary
        {
            get
            {
                if (Error != null)
                    return Error;
                string sent = Queued == 0 ? "Nothing was sent." : Queued == 1 ? "Sending to " + Recipient + "."
                    : "Sending " + Queued + " items to " + Recipient + ".";
                return Problems.Count == 0 ? sent : sent + "\nNot sent: " + string.Join("; ", Problems);
            }
        }
    }

    /// <summary>Encrypts and queues what the user sends. Blocking: run it off the UI thread.</summary>
    static class Sender
    {
        public static QueueResult Queue(Store store, Settings settings, Outgoing what)
        {
            var result = new QueueResult();
            Config config = store.LoadConfig();
            if (config == null)
            {
                result.Error = "Pair t7drop first.";
                return result;
            }
            var transfers = new Transfers(store, config, () => settings.ReceiveFolder);
            string to = store.Recipient(config, settings.SendTo);
            if (to == null)
            {
                try
                {
                    transfers.RefreshDevices(CancellationToken.None, true);
                }
                catch (TransientException e)
                {
                    result.Error = "The server could not be reached to find the phone (" + e.Message + "). Try again later.";
                    return result;
                }
                to = store.Recipient(config, settings.SendTo);
            }
            if (to == null)
            {
                result.Error = "There is no device to send to yet: pair your phone first (Pair a phone… in t7drop's menu).";
                return result;
            }
            result.Recipient = store.DeviceName(to);
            foreach (string path in what.Paths)
            {
                string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                try
                {
                    if (Directory.Exists(path))
                        QueueFolder(store, transfers, path, to);
                    else
                        transfers.QueueFile(path, to);
                    result.Queued++;
                }
                catch (Exception e)
                {
                    result.Problems.Add(name + ": " + e.Message);
                    Log.Write("could not queue a file: " + e.GetType().Name);
                }
            }
            if (!string.IsNullOrEmpty(what.Text))
            {
                transfers.QueueText(what.Text, to);
                result.Queued++;
            }
            if (what.Image != null)
            {
                try
                {
                    transfers.QueueBytes(what.Image, what.ImageName, "image/png", to);
                    result.Queued++;
                }
                catch (TooLargeException e)
                {
                    result.Problems.Add(e.Message);
                }
            }
            return result;
        }

        /// <summary>A folder goes as &lt;name&gt;.zip.</summary>
        static void QueueFolder(Store store, Transfers transfers, string folder, string to)
        {
            string temp = Path.Combine(store.Home, "tmp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string zip = Path.Combine(temp, Path.GetFileName(folder.TrimEnd('\\', '/')) + ".zip");
                ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, true);
                transfers.QueueFile(zip, to);
            }
            finally
            {
                Files.TryDeleteFolder(temp);
            }
        }
    }
}
