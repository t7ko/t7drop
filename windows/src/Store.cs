using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace T7Drop
{
    /// <summary>This computer's pairing: protected with DPAPI (current user) in pairing.dat.</summary>
    class Config
    {
        public string Url, DeviceId, DeviceName, Token;
        public byte[] Key;
        public bool First;   // paired with a code from t7drop-admin: may invite other devices
        public long Paired;  // Unix time in ms

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "url", Url }, { "device_id", DeviceId }, { "device_name", DeviceName }, { "token", Token },
                { "key", Pairing.B64Url(Key) }, { "first", First }, { "paired", Paired },
            };
        }

        public static Config FromJson(Dictionary<string, object> o)
        {
            return new Config
            {
                Url = Json.Str(o, "url"), DeviceId = Json.Str(o, "device_id"), DeviceName = Json.Str(o, "device_name"),
                Token = Json.Str(o, "token"), Key = Pairing.FromB64Url(Json.Str(o, "key")), First = Json.Bool(o, "first"),
                Paired = Json.Long(o, "paired", 0),
            };
        }
    }

    class Device
    {
        public string Id, Name;
        public long Created;  // Unix time in seconds
        public bool CanInvite;
    }

    /// <summary>An item received: a text, or a file saved in the download folder.</summary>
    class Received
    {
        public string Id, Kind, Name, Path, Text, From;
        public long Size, Time;  // Time: Unix ms

        public bool IsText
        {
            get { return Kind == "text"; }
        }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "id", Id }, { "kind", Kind }, { "name", Name }, { "path", Path }, { "text", Text }, { "from", From },
                { "size", Size }, { "time", Time },
            };
        }

        public static Received FromJson(Dictionary<string, object> o)
        {
            return new Received
            {
                Id = Json.Str(o, "id"), Kind = Json.Str(o, "kind"), Name = Json.Str(o, "name"), Path = Json.Str(o, "path"),
                Text = Json.Str(o, "text"), From = Json.Str(o, "from"), Size = Json.Long(o, "size", 0),
                Time = Json.Long(o, "time", 0),
            };
        }
    }

    static class OutState
    {
        public const string Queued = "queued", Uploading = "uploading", OnServer = "on_server",
            Delivered = "delivered", Failed = "failed";
    }

    /// <summary>
    /// An item to send, in outbox\&lt;id&gt;\: item.json (state), meta.bin and data.bin (the
    /// ciphertext, kept until the recipient has acknowledged). Changes are read-modify-write
    /// under one lock, as the UI (retry, cancel) and the worker both change items.
    /// </summary>
    class OutItem
    {
        static readonly object changeLock = new object();
        public readonly string Folder;
        Dictionary<string, object> data;

        OutItem(string folder, Dictionary<string, object> data)
        {
            Folder = folder;
            this.data = data;
        }

        public static OutItem Load(string folder)
        {
            Dictionary<string, object> data = Json.ReadFile(System.IO.Path.Combine(folder, "item.json"));
            return data == null || Json.Str(data, "id") == null ? null : new OutItem(folder, data);
        }

        public static OutItem Create(string folder, Dictionary<string, object> data)
        {
            Json.WriteFile(System.IO.Path.Combine(folder, "item.json"), data);
            return new OutItem(folder, data);
        }

        public string Id { get { return Json.Str(data, "id"); } }
        public string To { get { return Json.Str(data, "to"); } }
        public string Kind { get { return Json.Str(data, "kind"); } }
        public string Name { get { return Json.Str(data, "name"); } }
        public string Preview { get { return Json.Str(data, "preview"); } }
        public string State { get { return Json.Str(data, "state"); } }
        public string Error { get { return Json.Str(data, "error"); } }
        public long Size { get { return Json.Long(data, "size", 0); } }
        public long CSize { get { return Json.Long(data, "csize", 0); } }
        public string Sha256 { get { return Json.Str(data, "sha256"); } }
        public long Created { get { return Json.Long(data, "created", 0); } }
        public long Changed { get { return Json.Long(data, "changed", Created); } }
        public long Sent { get { return Json.Long(data, "sent", 0); } }
        public bool CancelRequested { get { return Json.Bool(data, "cancel"); } }
        public Dictionary<string, object> Meta { get { return Json.Obj(data, "meta"); } }
        public string MetaFile { get { return System.IO.Path.Combine(Folder, "meta.bin"); } }
        public string DataFile { get { return System.IO.Path.Combine(Folder, "data.bin"); } }

        public bool IsPending
        {
            get { return State == OutState.Queued || State == OutState.Uploading || State == OutState.OnServer; }
        }

        public string Label
        {
            get { return Name ?? "text (" + Names.Size(Size) + ")"; }
        }

        /// <summary>Applies change to the latest item.json; false if the item is gone.</summary>
        public bool Update(Action<Dictionary<string, object>> change)
        {
            lock (changeLock)
            {
                string path = System.IO.Path.Combine(Folder, "item.json");
                if (!Directory.Exists(Folder))
                    return false;
                Dictionary<string, object> fresh = Json.ReadFile(path) ?? data;
                change(fresh);
                fresh["changed"] = Clock.NowMs;
                Json.WriteFile(path, fresh);
                data = fresh;
                return true;
            }
        }

        /// <summary>Reads item.json again, for changes made by the other side (a cancel request).</summary>
        public void Reload()
        {
            lock (changeLock)
            {
                Dictionary<string, object> fresh = Json.ReadFile(System.IO.Path.Combine(Folder, "item.json"));
                if (fresh != null)
                    data = fresh;
            }
        }

        public void SetState(string state, string error = null)
        {
            Update(d =>
            {
                d["state"] = state;
                d["error"] = error;
            });
        }

        public void SetSent(long bytes)
        {
            Update(d => d["sent"] = bytes);
        }

        public void DropPayload()
        {
            Files.TryDelete(MetaFile);
            Files.TryDelete(DataFile);
        }
    }

    static class Clock
    {
        static readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long NowMs
        {
            get { return (long)(DateTime.UtcNow - epoch).TotalMilliseconds; }
        }

        public static DateTime Local(long unixMs)
        {
            return epoch.AddMilliseconds(unixMs).ToLocalTime();
        }
    }

    /// <summary>
    /// The data folder (%LOCALAPPDATA%\T7Drop): pairing.dat, devices.json, outbox\, incoming\
    /// (downloads in progress), received.json (ids saved in the last 30 days, so that none is
    /// saved twice), history.json (what was received), settings.ini, t7drop.log, lib\.
    /// </summary>
    class Store
    {
        public const int MaxHistory = 300;
        const long KeepReceivedMs = 30L * 86400 * 1000;
        const long KeepDeliveredMs = 30L * 86400 * 1000;
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("t7drop pairing v1");

        public readonly string Home, OutboxDir, IncomingDir;
        readonly string pairingPath, devicesPath, receivedPath, historyPath;
        readonly object fileLock = new object();

        /// <summary>Raised on any thread after the outbox or the history changed.</summary>
        public event Action Changed;

        public Store(string home)
        {
            Home = home;
            OutboxDir = Path.Combine(home, "outbox");
            IncomingDir = Path.Combine(home, "incoming");
            pairingPath = Path.Combine(home, "pairing.dat");
            devicesPath = Path.Combine(home, "devices.json");
            receivedPath = Path.Combine(home, "received.json");
            historyPath = Path.Combine(home, "history.json");
            Directory.CreateDirectory(home);
        }

        public void Touch()
        {
            Action handler = Changed;
            if (handler != null)
                handler();
        }

        // Pairing

        /// <summary>The pairing, or null if this computer is not paired (or another user's file is there).</summary>
        public Config LoadConfig()
        {
            if (!File.Exists(pairingPath))
                return null;
            try
            {
                byte[] plain = ProtectedData.Unprotect(Files.ReadBytes(pairingPath), Entropy, DataProtectionScope.CurrentUser);
                return Config.FromJson(Json.ParseObject(Encoding.UTF8.GetString(plain)));
            }
            catch (CryptographicException e)
            {
                Log.Write("pairing.dat does not decrypt for this user: " + e.Message);
                return null;
            }
        }

        public void SaveConfig(Config config)
        {
            byte[] plain = Encoding.UTF8.GetBytes(Json.Write(config.ToJson()));
            Files.WriteAtomically(pairingPath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>Unpairs: the pairing, the devices and the outbox (encrypted with the old key) go.</summary>
        public void Forget()
        {
            File.Delete(pairingPath);
            Files.TryDelete(devicesPath);
            Files.TryDeleteFolder(OutboxDir);
            Files.TryDeleteFolder(IncomingDir);
            Touch();
        }

        // Devices

        public List<Device> Devices()
        {
            Dictionary<string, object> data;
            lock (fileLock)
                data = Json.ReadFile(devicesPath);
            return Json.Arr(data, "devices").OfType<Dictionary<string, object>>().Select(d => new Device
            {
                Id = Json.Str(d, "id"), Name = Json.Str(d, "name"), Created = Json.Long(d, "created", 0),
                CanInvite = Json.Bool(d, "can_invite"),
            }).Where(d => d.Id != null).ToList();
        }

        public void SaveDevices(object[] devices)
        {
            lock (fileLock)
                Json.WriteFile(devicesPath, new Dictionary<string, object> { { "devices", devices } });
        }

        public string DeviceName(string id)
        {
            Device device = Devices().FirstOrDefault(d => d.Id == id);
            return device != null ? device.Name : id;
        }

        /// <summary>
        /// Where items go: the device chosen in the settings while it is paired. Otherwise, on the
        /// first device of a group, the newest device paired after it: every device of its group
        /// was invited by it, so an older one belongs to another group and could not decrypt.
        /// On others, the newest device that may invite and was paired before this one. Null
        /// when there is none.
        /// </summary>
        public string Recipient(Config config, string preferred)
        {
            List<Device> devices = Devices();
            List<Device> others = devices.Where(d => d.Id != config.DeviceId).OrderByDescending(d => d.Created).ToList();
            if (preferred != null && others.Any(d => d.Id == preferred))
                return preferred;
            Device me = devices.FirstOrDefault(d => d.Id == config.DeviceId);
            if (config.First && me != null)
            {
                Device invited = others.FirstOrDefault(d => d.Created >= me.Created);
                return invited == null ? null : invited.Id;
            }
            // Invited: the device that invited this one, paired before it (an older inviter is
            // another group's); as the Android app's Recipients.pick.
            long mine = me != null ? me.Created : long.MaxValue;
            Device best = others.FirstOrDefault(d => d.CanInvite && d.Created <= mine)
                ?? others.FirstOrDefault(d => d.CanInvite) ?? others.FirstOrDefault();
            return best == null ? null : best.Id;
        }

        // Outbox

        /// <summary>A folder to write a new item into; PublishOutFolder then puts it in the outbox.</summary>
        public string NewOutFolder(string id)
        {
            string folder = Path.Combine(OutboxDir, "." + id + ".new");
            Directory.CreateDirectory(folder);
            return folder;
        }

        public OutItem PublishOutFolder(string folder, string id)
        {
            string target = Path.Combine(OutboxDir, id);
            Directory.Move(folder, target);
            Touch();
            return OutItem.Load(target);
        }

        public List<OutItem> Outbox()
        {
            if (!Directory.Exists(OutboxDir))
                return new List<OutItem>();
            return Directory.GetDirectories(OutboxDir).Where(f => !Path.GetFileName(f).StartsWith("."))
                .Select(OutItem.Load).Where(i => i != null).OrderBy(i => i.Created).ToList();
        }

        public void Remove(OutItem item)
        {
            Files.TryDeleteFolder(item.Folder);
            Touch();
        }

        /// <summary>Drops what is left over: unfinished queueing, old deliveries, old downloads.</summary>
        public void CleanUp()
        {
            long now = Clock.NowMs;
            if (Directory.Exists(OutboxDir))
                foreach (string folder in Directory.GetDirectories(OutboxDir))
                {
                    if (Path.GetFileName(folder).StartsWith("."))
                    {
                        if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddHours(-1))
                            Files.TryDeleteFolder(folder);
                        continue;
                    }
                    OutItem item = OutItem.Load(folder);
                    if (item == null && Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1))
                        Files.TryDeleteFolder(folder);
                    else if (item != null && item.State == OutState.Delivered && now - item.Changed > KeepDeliveredMs)
                        Files.TryDeleteFolder(folder);
                }
            if (Directory.Exists(IncomingDir))
                foreach (string file in Directory.GetFiles(IncomingDir))
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-8))
                        Files.TryDelete(file);
        }

        // Received

        public bool WasReceived(string id)
        {
            lock (fileLock)
            {
                Dictionary<string, object> received = Json.ReadFile(receivedPath);
                return received != null && received.ContainsKey(id);
            }
        }

        public void MarkReceived(string id)
        {
            lock (fileLock)
            {
                long now = Clock.NowMs;
                Dictionary<string, object> old = Json.ReadFile(receivedPath) ?? new Dictionary<string, object>();
                var received = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> entry in old)
                    if (now - Json.Long(old, entry.Key, 0) < KeepReceivedMs)
                        received[entry.Key] = entry.Value;
                received[id] = now;
                Json.WriteFile(receivedPath, received);
            }
        }

        public List<Received> History()
        {
            lock (fileLock)
                return Json.Arr(Json.ReadFile(historyPath), "items").OfType<Dictionary<string, object>>()
                    .Select(Received.FromJson).ToList();
        }

        public void AddReceived(Received item)
        {
            lock (fileLock)
            {
                List<Received> items = History();
                items.Insert(0, item);
                SaveHistory(items.Take(MaxHistory));
            }
            Touch();
        }

        public void RemoveReceived(string id)
        {
            lock (fileLock)
                SaveHistory(History().Where(r => r.Id != id));
            Touch();
        }

        void SaveHistory(IEnumerable<Received> items)
        {
            Json.WriteFile(historyPath, new Dictionary<string, object>
            {
                { "items", items.Select(r => (object)r.ToJson()).ToArray() },
            });
        }
    }
}
