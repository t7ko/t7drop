using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace T7Drop.Tests
{
    /// <summary>
    /// `Tests.exe agent &lt;home&gt; &lt;command&gt; ...`: the app's transfer engine (Store, Transfers,
    /// Worker) without a window, with its data in &lt;home&gt;. tests/test_windows.py drives it
    /// against a local server, with the Python CLI as the other device.
    ///   pair &lt;url&gt; &lt;code&gt; &lt;name&gt;      as the first device, with a new group key
    ///   join &lt;invitation&gt; &lt;name&gt;
    ///   invite                         prints an invitation
    ///   send-file &lt;path&gt;... | send-text &lt;text&gt;
    ///   status | retry &lt;id&gt; | cancel &lt;id&gt;
    ///   run &lt;seconds&gt; [--until-received N] [--until-delivered] [--fail-acks] [--downloads &lt;dir&gt;]
    ///     runs the worker, printing RECEIVED, STATUS and LOG lines; exit code 3 on time-out
    /// </summary>
    static class Agent
    {
        public static int Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: Tests.exe agent <home> <command> ...");
                return 2;
            }
            string home = Path.GetFullPath(args[0]);
            string command = args[1];
            var store = new Store(home);
            Log.Open(home);
            Sodium.Load(home);
            try
            {
                switch (command)
                {
                    case "pair":
                        return Pair(store, args[2], args[3], args[4], ItemCrypto.NewKey(), true);
                    case "join":
                        Invitation invitation = Pairing.Parse(args[2]);
                        return Pair(store, invitation.Url, invitation.Code, args[3], invitation.Key, false);
                    case "invite":
                        Config config = store.LoadConfig();
                        Dictionary<string, object> reply = new Api(config.Url, config.Token).Invite(CancellationToken.None);
                        Console.WriteLine(Pairing.Make(config.Url, Json.Str(reply, "code"), config.Key));
                        return 0;
                    case "send-file":
                    case "send-text":
                        Transfers transfers = NewTransfers(store, home);
                        string to = Recipient(store, transfers);
                        foreach (string value in args.Skip(2))
                        {
                            OutItem item = command == "send-file" ? transfers.QueueFile(value, to) : transfers.QueueText(value, to);
                            Console.WriteLine("queued " + item.Id);
                        }
                        return 0;
                    case "status":
                        foreach (OutItem item in store.Outbox())
                            Console.WriteLine(item.Id + " " + item.State + " " + (item.Error ?? ""));
                        return 0;
                    case "retry":
                        Transfers retrying = NewTransfers(store, home);
                        retrying.Retry(store.Outbox().First(i => i.Id == args[2]), Recipient(store, retrying));
                        return 0;
                    case "cancel":
                        store.Outbox().First(i => i.Id == args[2]).Update(d => d["cancel"] = true);
                        return 0;
                    case "run":
                        return RunWorker(store, home, args.Skip(2).ToArray());
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.GetType().Name + ": " + e.Message);
                return 1;
            }
            Console.Error.WriteLine("Unknown command: " + command);
            return 2;
        }

        static int Pair(Store store, string url, string code, string name, byte[] key, bool first)
        {
            url = url.TrimEnd('/');
            Dictionary<string, object> reply = new Api(url, null).Pair(code, name, CancellationToken.None);
            store.SaveConfig(new Config
            {
                Url = url, DeviceId = Json.Str(reply, "device_id"), DeviceName = Json.Str(reply, "name"),
                Token = Json.Str(reply, "token"), Key = key, First = first, Paired = Clock.NowMs,
            });
            Console.WriteLine("device " + Json.Str(reply, "device_id"));
            return 0;
        }

        static Transfers NewTransfers(Store store, string home)
        {
            return new Transfers(store, store.LoadConfig(), () => Path.Combine(home, "downloads"));
        }

        static string Recipient(Store store, Transfers transfers)
        {
            string to = store.Recipient(transfers.Config, null);
            if (to == null)
            {
                transfers.RefreshDevices(CancellationToken.None, true);
                to = store.Recipient(transfers.Config, null);
            }
            if (to == null)
                throw new InvalidOperationException("no other device");
            return to;
        }

        static int RunWorker(Store store, string home, string[] args)
        {
            double seconds = double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
            int untilReceived = -1;
            bool untilDelivered = false, failAcks = false;
            string downloads = Path.Combine(home, "downloads");
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--until-received")
                    untilReceived = int.Parse(args[++i]);
                else if (args[i] == "--until-delivered")
                    untilDelivered = true;
                else if (args[i] == "--fail-acks")
                    failAcks = true;
                else if (args[i] == "--downloads")
                    downloads = args[++i];
            }
            Log.Echo = line => Console.WriteLine("LOG " + line);
            var worker = new Worker(store, () => downloads);
            if (failAcks)
                worker.FailAck = id => true;
            int received = 0;
            worker.Received += r =>
            {
                Console.WriteLine("RECEIVED " + r.Id + " " + r.Kind + " " +
                    (r.IsText ? r.Text.Replace("\\", "\\\\").Replace("\n", "\\n") : r.Path));
                Interlocked.Increment(ref received);
            };
            worker.StatusChanged += () => Console.WriteLine("STATUS " + worker.State + " " + worker.Problem);
            worker.Start();
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            bool done = false;
            while (!done && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
                if (untilReceived >= 0 && Volatile.Read(ref received) >= untilReceived)
                    done = true;
                if (untilDelivered && Settled(store))
                    done = true;
            }
            worker.Stop();
            Console.WriteLine(done || (untilReceived < 0 && !untilDelivered) ? "DONE" : "TIMEOUT");
            return done || (untilReceived < 0 && !untilDelivered) ? 0 : 3;
        }

        /// <summary>Every item in the outbox delivered or failed (and every folder read: none was being written).</summary>
        static bool Settled(Store store)
        {
            int folders = Directory.Exists(store.OutboxDir)
                ? Directory.GetDirectories(store.OutboxDir).Count(d => !Path.GetFileName(d).StartsWith(".")) : 0;
            List<OutItem> items = store.Outbox();
            return items.Count == folders && items.All(i => i.State == OutState.Delivered || i.State == OutState.Failed);
        }
    }
}
