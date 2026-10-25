using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace T7Drop
{
    class TooLargeException : Exception
    {
        public TooLargeException(string message) : base(message) { }
    }

    /// <summary>Constants of protocol version 1, shared with the server and the other clients.</summary>
    static class Protocol
    {
        public const int ChunkSize = 4 * 1024 * 1024;
        public const int InlineMax = 64 * 1024;
        public const long MaxItemBytes = 200L * 1024 * 1024;  // the server's limit, in bytes of ciphertext
        public const long GiveUpMs = 7L * 86400 * 1000;       // the server keeps undelivered items as long
        static readonly Regex ItemId = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");

        public static int ChunkCount(long size)
        {
            return (int)Math.Max(1, (size + ChunkSize - 1) / ChunkSize);
        }

        public static long ChunkLength(long size, long n)
        {
            return Math.Min(ChunkSize, size - n * ChunkSize);
        }

        public static bool IsItemId(string value)
        {
            return value != null && ItemId.IsMatch(value);
        }
    }

    /// <summary>
    /// What a client does (docs/design.md, "Reliability"), as the CLI's agent does it: encrypts
    /// and queues items; uploads them, resuming by chunks; receives, saves and acknowledges.
    /// The item id is made when the item is queued, so retries never create a second item; the
    /// ciphertext stays until the recipient acknowledges; an item the server lost (a restart) is
    /// uploaded again; received ids are remembered, so nothing is saved twice. Queueing works on
    /// any thread; the rest runs on the worker's thread.
    /// </summary>
    class Transfers
    {
        static readonly HashSet<string> ChangedUnderUs = new HashSet<string>
            { "unknown_item", "not_uploading", "missing_chunks", "hash_mismatch" };
        static readonly TimeSpan DevicesEvery = TimeSpan.FromMinutes(10);

        readonly Store store;
        public readonly Config Config;
        readonly Api api;
        readonly Func<string> receiveFolder;
        readonly HashSet<string> refused = new HashSet<string>();  // items that do not decrypt: not downloaded again
        string instance;
        long version = -1;
        bool interrupted;  // an upload met a changed server: sync again without a long poll
        DateTime devicesRefreshed = DateTime.MinValue;
        string activity;

        /// <summary>Raised on the worker's thread for each item saved.</summary>
        public event Action<Received> ItemReceived;
        /// <summary>"Sending photo.jpg: 45%" while a transfer runs, then null.</summary>
        public event Action<string> Activity;
        /// <summary>For tests: when it returns true for an item id, its ack fails as if the server were gone.</summary>
        public Func<string, bool> FailAck;

        public Transfers(Store store, Config config, Func<string> receiveFolder)
        {
            this.store = store;
            Config = config;
            this.receiveFolder = receiveFolder;
            api = new Api(config.Url, config.Token);
        }

        public Api Api
        {
            get { return api; }
        }

        // Queueing

        public OutItem QueueFile(string path, string to)
        {
            var info = new FileInfo(path);
            CheckSize(info.Length, info.Name);
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                return Queue(src, "file", info.Name, MimeOf(info.Name), to, null);
        }

        public OutItem QueueText(string text, string to)
        {
            string preview = text.Length > 300 ? text.Substring(0, 300) : text;
            return Queue(new MemoryStream(Encoding.UTF8.GetBytes(text)), "text", null, "text/plain; charset=utf-8", to, preview);
        }

        public OutItem QueueBytes(byte[] data, string name, string mime, string to)
        {
            CheckSize(data.Length, name);
            return Queue(new MemoryStream(data), "file", name, mime, to, null);
        }

        static void CheckSize(long plainSize, string name)
        {
            if (ItemCrypto.SealedSize(plainSize) > Protocol.MaxItemBytes)
                throw new TooLargeException((name ?? "The text") + " is larger than the server takes (200 MB).");
        }

        public static string MimeOf(string name)
        {
            try
            {
                return System.Web.MimeMapping.GetMimeMapping(name);
            }
            catch (Exception)
            {
                return "application/octet-stream";
            }
        }

        OutItem Queue(Stream src, string kind, string name, string mime, string to, string preview)
        {
            string id = Guid.NewGuid().ToString("D");
            string folder = store.NewOutFolder(id);
            try
            {
                long size, csize;
                string sha256;
                using (var file = new FileStream(Path.Combine(folder, "data.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (var hashing = new HashingStream(file))
                {
                    size = ItemCrypto.SealStream(Config.Key, id, src, hashing);
                    csize = hashing.Count;
                    sha256 = hashing.Finish();
                    file.Flush(true);
                }
                CheckSize(size, name);
                long now = Clock.NowMs;
                var meta = new Dictionary<string, object>
                {
                    { "v", 1 }, { "kind", kind }, { "mime", mime }, { "size", size }, { "from", Config.DeviceId },
                    { "to", to }, { "created", now },
                };
                if (name != null)
                    meta["name"] = name;
                Files.WriteAtomically(Path.Combine(folder, "meta.bin"), ItemCrypto.SealMeta(Config.Key, id, meta));
                OutItem.Create(folder, new Dictionary<string, object>
                {
                    { "id", id }, { "to", to }, { "kind", kind }, { "name", name }, { "preview", preview },
                    { "size", size }, { "csize", csize }, { "sha256", sha256 }, { "created", now },
                    { "state", OutState.Queued }, { "error", null }, { "sent", 0 }, { "meta", meta },
                });
                OutItem item = store.PublishOutFolder(folder, id);
                Log.Write("queued " + id + ": " + kind + ", " + size + " bytes");
                return item;
            }
            catch
            {
                Files.TryDeleteFolder(folder);
                throw;
            }
        }

        /// <summary>
        /// Queues a failed item again. If its recipient is no longer paired, the item goes to
        /// `to` instead: only its meta, which names the recipient, is encrypted again.
        /// </summary>
        public void Retry(OutItem item, string to)
        {
            Dictionary<string, object> meta = item.Meta;
            if (to != null && to != item.To && meta != null && store.Devices().All(d => d.Id != item.To))
            {
                meta = new Dictionary<string, object>(meta);
                meta["to"] = to;
                Files.WriteAtomically(item.MetaFile, ItemCrypto.SealMeta(Config.Key, item.Id, meta));
                item.Update(d =>
                {
                    d["to"] = to;
                    d["meta"] = meta;
                });
            }
            item.Update(d =>
            {
                d["state"] = OutState.Queued;
                d["error"] = null;
                d["created"] = Clock.NowMs;  // seven more days
                d["sent"] = 0;
                d.Remove("cancel");
            });
            store.Touch();
        }

        // Both directions

        /// <summary>
        /// Sends what is queued and receives what is waiting. With wait &gt; 0 the sync is a long
        /// poll, which `poll` ends early; `round` ends everything. Throws TransientException (or a
        /// transient ApiException) when the server cannot be reached.
        /// </summary>
        public void RunOnce(int wait, CancellationToken round, CancellationToken poll)
        {
            interrupted = false;
            ProcessCancels(round);
            SendPending(round);
            Sync(interrupted ? 0 : wait, round, poll);
            SendPending(round);
            RefreshDevices(round, false);
        }

        public void RefreshDevices(CancellationToken cancel, bool force)
        {
            if (!force && DateTime.UtcNow - devicesRefreshed < DevicesEvery)
                return;
            try
            {
                store.SaveDevices(api.Devices(cancel));
                devicesRefreshed = DateTime.UtcNow;
            }
            catch (ApiException)
            {
                // not worth failing a transfer over
            }
        }

        // Sending

        void ProcessCancels(CancellationToken cancel)
        {
            foreach (OutItem item in store.Outbox().Where(i => i.CancelRequested))
            {
                if (item.State == OutState.Delivered)
                {
                    item.Update(d => d.Remove("cancel"));  // too late: it has arrived
                    continue;
                }
                try
                {
                    api.Delete(item.Id, cancel);
                }
                catch (ApiException e)
                {
                    if (e.Transient)
                        throw;
                    // unknown_item: it never reached the server, or the server lost it
                }
                Log.Write("cancelled " + item.Id);
                store.Remove(item);
            }
        }

        void SendPending(CancellationToken cancel)
        {
            foreach (OutItem item in store.Outbox())
            {
                if (item.CancelRequested || (item.State != OutState.Queued && item.State != OutState.Uploading))
                    continue;
                if (Clock.NowMs - item.Created > Protocol.GiveUpMs)
                {
                    item.SetState(OutState.Failed, "not delivered in 7 days");
                    store.Touch();
                    continue;
                }
                Upload(item, cancel);
            }
        }

        void Upload(OutItem item, CancellationToken cancel)
        {
            byte[] meta = File.ReadAllBytes(item.MetaFile);
            byte[] inline = item.CSize <= Protocol.InlineMax ? File.ReadAllBytes(item.DataFile) : null;
            try
            {
                Dictionary<string, object> view = api.CreateItem(item.Id, item.To, item.CSize, item.Sha256, meta, inline, cancel);
                if (Json.Str(view, "state") == "uploading")
                {
                    if (item.State != OutState.Uploading)
                    {
                        item.SetState(OutState.Uploading);
                        store.Touch();
                    }
                    var have = new HashSet<long>(Json.Arr(view, "have").Select(n => Convert.ToInt64(n)));
                    long done = have.Sum(n => Protocol.ChunkLength(item.CSize, n));
                    using (var file = new FileStream(item.DataFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        for (int n = 0; n < Protocol.ChunkCount(item.CSize); n++)
                        {
                            if (have.Contains(n))
                                continue;
                            item.Reload();
                            if (item.CancelRequested)
                                return;  // the next round deletes it
                            long offset = (long)n * Protocol.ChunkSize;
                            var chunk = new byte[Protocol.ChunkLength(item.CSize, n)];
                            file.Seek(offset, SeekOrigin.Begin);
                            ReadFull(file, chunk);
                            long before = done;
                            string label = item.Label;
                            api.PutChunk(item.Id, n, chunk, sent => Report("Sending", label, before + sent, item.CSize), cancel);
                            done += chunk.Length;
                            item.SetSent(done);
                            store.Touch();
                        }
                    }
                    view = api.Commit(item.Id, cancel);
                }
                ApplyState(item, Json.Str(view, "state"));
            }
            catch (ApiException e)
            {
                if (e.Transient)
                    throw;
                if (ChangedUnderUs.Contains(e.Code))
                {
                    interrupted = true;  // a server restart, or another upload of it: the next round
                    Log.Write("the upload of " + item.Id + " changed on the server (" + e.Code + "); starting it again");
                    return;
                }
                Log.Write("refused " + item.Id + ": " + e.Code);
                item.SetState(OutState.Failed, Describe(e));
                store.Touch();
            }
            finally
            {
                Report(null, null, 0, 0);
            }
        }

        static void ReadFull(Stream stream, byte[] buffer)
        {
            int done = 0;
            while (done < buffer.Length)
            {
                int read = stream.Read(buffer, done, buffer.Length - done);
                if (read <= 0)
                    throw new IOException("The outbox file is shorter than it should be.");
                done += read;
            }
        }

        public static string Describe(ApiException e)
        {
            switch (e.Code)
            {
                case "too_large": return "too large for the server";
                case "bad_recipient": return "the recipient is no longer paired";
                case "conflict": return "the server has another item with this id";
                default: return "refused by the server (" + e.Code + ")";
            }
        }

        void ApplyState(OutItem item, string state)
        {
            if (state == "delivered" && item.State != OutState.Delivered)
            {
                item.SetState(OutState.Delivered);
                item.DropPayload();
                store.Touch();
                Log.Write("delivered " + item.Id);
            }
            else if (state == "ready" && item.State != OutState.OnServer && item.State != OutState.Delivered)
            {
                item.SetState(OutState.OnServer);
                store.Touch();
            }
        }

        // Receiving

        void Sync(int wait, CancellationToken round, CancellationToken poll)
        {
            long since = instance != null && instance == api.Instance ? version : -1;
            Dictionary<string, object> snapshot = api.Sync(since, wait, wait > 0 ? poll : round);
            string newInstance = Json.Str(snapshot, "instance");
            if (instance != null && newInstance != instance)
                Log.Write("the server restarted");
            instance = newInstance;
            version = Json.Long(snapshot, "version", -1);
            ApplyOutbox(Json.Arr(snapshot, "outbox"));
            foreach (Dictionary<string, object> entry in Json.Arr(snapshot, "inbox").OfType<Dictionary<string, object>>())
                Receive(entry, round);
        }

        void ApplyOutbox(object[] entries)
        {
            var onServer = new Dictionary<string, Dictionary<string, object>>();
            foreach (Dictionary<string, object> entry in entries.OfType<Dictionary<string, object>>())
                if (Json.Str(entry, "id") != null)
                    onServer[Json.Str(entry, "id")] = entry;
            foreach (OutItem item in store.Outbox())
            {
                if (item.State == OutState.Delivered || item.State == OutState.Failed)
                    continue;
                Dictionary<string, object> entry;
                if (onServer.TryGetValue(item.Id, out entry))
                    ApplyState(item, Json.Str(entry, "state"));
                else if (item.State == OutState.OnServer)
                {
                    item.SetState(OutState.Queued);  // the server lost it (a restart): upload it again
                    store.Touch();
                    Log.Write("the server lost " + item.Id + "; uploading it again");
                }
            }
        }

        void Receive(Dictionary<string, object> entry, CancellationToken cancel)
        {
            string id = Json.Str(entry, "id");
            if (!Protocol.IsItemId(id) || refused.Contains(id))
                return;
            if (store.WasReceived(id))
            {
                Ack(id, cancel);  // saved before, but the ack did not arrive
                return;
            }
            Dictionary<string, object> meta;
            try
            {
                meta = ItemCrypto.OpenMeta(Config.Key, id, Convert.FromBase64String(Json.Str(entry, "meta") ?? ""));
            }
            catch (Exception e)
            {
                if (!(e is DecryptException || e is FormatException))
                    throw;
                Refuse(id, "does not decrypt with this device's key");
                return;
            }
            if (Json.Str(meta, "to") != Config.DeviceId)
            {
                Refuse(id, "is addressed to another device");
                return;
            }
            bool isText = Json.Str(meta, "kind") == "text";
            string part = Download(entry, isText ? "a text" : Names.Safe(Json.Str(meta, "name")), cancel);
            string fromId = Json.Str(meta, "from") ?? Json.Str(entry, "from");
            if (store.Devices().All(d => d.Id != fromId))
                RefreshDevices(cancel, true);  // paired since the list was fetched: for its name
            string from = store.DeviceName(fromId);
            Received received;
            try
            {
                received = isText ? SaveText(id, part, from) : SaveFile(id, meta, part, from);
            }
            catch (DecryptException e)
            {
                Refuse(id, "did not decrypt (" + e.Message + ")");
                return;
            }
            finally
            {
                Files.TryDelete(part);
            }
            store.MarkReceived(id);
            store.AddReceived(received);
            Log.Write("received " + id + ": " + received.Kind + ", " + received.Size + " bytes");
            Action<Received> handler = ItemReceived;
            if (handler != null)
                handler(received);
            Ack(id, cancel);
        }

        void Refuse(string id, string why)
        {
            refused.Add(id);
            Log.Write("not saved: item " + id + " " + why);
        }

        /// <summary>Downloads the ciphertext into incoming\, resuming a partial download; returns the file.</summary>
        string Download(Dictionary<string, object> entry, string label, CancellationToken cancel)
        {
            string id = Json.Str(entry, "id");
            long size = Json.Long(entry, "size", 0);
            Directory.CreateDirectory(store.IncomingDir);
            string part = Path.Combine(store.IncomingDir, id + ".part");
            int chunks = Protocol.ChunkCount(size);
            try
            {
                using (var file = new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    int done = (int)(file.Length / Protocol.ChunkSize);
                    file.SetLength((long)done * Protocol.ChunkSize);
                    file.Seek((long)done * Protocol.ChunkSize, SeekOrigin.Begin);
                    for (int n = done; n < chunks; n++)
                    {
                        Report("Receiving", label, (long)n * Protocol.ChunkSize, size);
                        byte[] data;
                        try
                        {
                            data = api.GetChunk(id, n, cancel);
                        }
                        catch (ApiException e)
                        {
                            throw new TransientException(e.Message);  // gone meanwhile: the next sync tells
                        }
                        file.Write(data, 0, data.Length);
                        file.Flush(true);
                    }
                }
            }
            finally
            {
                Report(null, null, 0, 0);
            }
            if (Files.Sha256(part) != Json.Str(entry, "sha256"))
            {
                File.Delete(part);
                throw new TransientException("the download of " + id + " does not match its hash");
            }
            return part;
        }

        /// <summary>
        /// Decrypts into a hidden temporary file in the download folder, and gives it its name only
        /// when it is complete: no partial file ever appears under the real name.
        /// </summary>
        Received SaveFile(string id, Dictionary<string, object> meta, string part, string from)
        {
            string folder = receiveFolder();
            Directory.CreateDirectory(folder);
            string name = Names.Safe(Json.Str(meta, "name"));
            string temp = Path.Combine(folder, "." + name + "." + id.Substring(0, 8) + ".t7drop-part");
            try
            {
                if (File.Exists(temp))
                    File.SetAttributes(temp, FileAttributes.Normal);
                using (File.Create(temp)) { }
                File.SetAttributes(temp, FileAttributes.Hidden);
                long size;
                using (var src = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (var dst = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    size = ItemCrypto.OpenStream(Config.Key, id, src, dst);
                    dst.Flush(true);
                }
                File.SetAttributes(temp, FileAttributes.Normal);
                // From another device, perhaps from the web before: marked as from the internet
                // before it gets its name; the rename keeps the mark.
                if (!Files.MarkFromInternet(temp))
                    Log.Write("received " + id + " into a folder without alternate streams: not marked as from the internet");
                string target = Names.UniquePath(folder, name);
                File.Move(temp, target);
                return new Received
                {
                    Id = id, Kind = "file", Name = Path.GetFileName(target), Path = target, Size = size, From = from,
                    Time = Clock.NowMs,
                };
            }
            finally
            {
                Files.TryDelete(temp);  // left only when something failed
            }
        }

        Received SaveText(string id, string part, string from)
        {
            var buffer = new MemoryStream();
            using (var src = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read))
                ItemCrypto.OpenStream(Config.Key, id, src, buffer);
            string text = Encoding.UTF8.GetString(buffer.ToArray());
            return new Received { Id = id, Kind = "text", Text = text, Size = buffer.Length, From = from, Time = Clock.NowMs };
        }

        void Ack(string id, CancellationToken cancel)
        {
            Func<string, bool> fail = FailAck;
            if (fail != null && fail(id))
                throw new TransientException("the ack of " + id + " held back for a test");
            try
            {
                api.Ack(id, cancel);
            }
            catch (ApiException e)
            {
                if (e.Code != "unknown_item")
                    throw new TransientException(e.Message);
            }
        }

        void Report(string verb, string label, long done, long total)
        {
            string text = verb == null ? null
                : verb + " " + label + ": " + (total > 0 ? Math.Min(100, done * 100 / total) : 100) + "%";
            if (text == activity)
                return;
            activity = text;
            Action<string> handler = Activity;
            if (handler != null)
                handler(text);
        }
    }

    /// <summary>Passes writes on, counting them and computing their SHA-256.</summary>
    sealed class HashingStream : Stream
    {
        readonly Stream inner;
        readonly SHA256 sha = SHA256.Create();
        public long Count;

        public HashingStream(Stream inner)
        {
            this.inner = inner;
        }

        public string Finish()
        {
            sha.TransformFinalBlock(new byte[0], 0, 0);
            return Hex.Of(sha.Hash);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            sha.TransformBlock(buffer, offset, count, null, 0);
            Count += count;
        }

        public override void Flush() { inner.Flush(); }
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return Count; } }
        public override long Position { get { return Count; } set { throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                sha.Dispose();
            base.Dispose(disposing);
        }
    }
}
