using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace T7Drop.Tests
{
    /// <summary>
    /// A console test runner with no framework. scripts/build.ps1 runs it as
    /// `Tests.exe test &lt;built exe&gt; &lt;repository&gt;`: the encryption against testdata/vectors,
    /// the QR encoder against segno (qr-vectors.txt), names, invitations, settings, the store and
    /// the updater's parts that need no GitHub. `Tests.exe agent ...` (Agent.cs) drives the
    /// transfer engine without a window, for tests/test_windows.py.
    /// </summary>
    static partial class TestMain
    {
        static int failures;
        static string scratch;

        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length > 0 && args[0] == "agent")
                return Agent.Run(args.Skip(1).ToArray());
            if (args.Length != 3 || args[0] != "test")
            {
                Console.Error.WriteLine("Usage: Tests.exe test <built exe> <repository> | Tests.exe agent ...");
                return 2;
            }
            string builtExe = args[1], repository = args[2];
            scratch = Path.Combine(Path.GetTempPath(), "T7Drop-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(scratch);
            try
            {
                Sodium.Load(scratch);
                Console.WriteLine("libsodium " + Sodium.Version);
                CryptoTests(Path.Combine(repository, "testdata", "vectors"));
                QrTests(Path.Combine(repository, "windows", "tests", "qr-vectors.txt"));
                PairingTests();
                NamesTests();
                JsonTests();
                SettingsTests();
                StoreTests();
                QueueTests();
                UpdaterTests(builtExe);
            }
            catch (Exception e)
            {
                failures++;
                Console.WriteLine("FAIL with an exception: " + e);
            }
            finally
            {
                try
                {
                    Directory.Delete(scratch, true);
                }
                catch (Exception)
                {
                    // libsodium.dll stays loaded: the folder goes with the next clean-up of %TEMP%
                }
            }
            Console.WriteLine(failures == 0 ? "All tests passed." : failures + " test(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        static void CryptoTests(string vectors)
        {
            Dictionary<string, object> v = Json.ParseObject(File.ReadAllText(Path.Combine(vectors, "vectors.json"), Encoding.UTF8));
            byte[] key = Hex.Parse(Json.Str(v, "key_hex"));
            Equal((long)ItemCrypto.MessageBytes, Json.Long(v, "message_bytes", 0), "message size");
            var items = new Dictionary<string, Dictionary<string, object>>();
            foreach (Dictionary<string, object> item in Json.Arr(v, "items").OfType<Dictionary<string, object>>())
            {
                string name = Json.Str(item, "name"), id = Json.Str(item, "id");
                items[name] = item;
                Dictionary<string, object> meta = ItemCrypto.OpenMeta(key, id, File.ReadAllBytes(Path.Combine(vectors, Json.Str(item, "meta_file"))));
                Equal(Json.Write(Json.Obj(item, "meta")), Json.Write(meta), "vector " + name + ": meta");
                byte[] plain = Unseal(key, id, File.ReadAllBytes(Path.Combine(vectors, Json.Str(item, "data_file"))));
                Equal(Json.Long(item, "plain_size", -1), (long)plain.Length, "vector " + name + ": size");
                Equal(Json.Str(item, "plain_sha256"), Sha256(plain), "vector " + name + ": content");
                if (Json.Str(item, "plain_text") != null)
                    Equal(Json.Str(item, "plain_text"), Encoding.UTF8.GetString(plain), "vector " + name + ": text");
            }
            int refusals = 0;
            foreach (Dictionary<string, object> change in Json.Arr(v, "must_fail").OfType<Dictionary<string, object>>())
            {
                Dictionary<string, object> item = items[Json.Str(change, "item")];
                string file = Json.Str(change, "file");
                byte[] blob = File.ReadAllBytes(Path.Combine(vectors, Json.Str(item, file + "_file")));
                string id = Json.Str(item, "id");
                switch (Json.Str(change, "change"))
                {
                    case "truncate": blob = blob.Take((int)Json.Long(change, "length", 0)).ToArray(); break;
                    case "flip": blob[Json.Long(change, "offset", 0)] ^= 1; break;
                    case "append": blob = blob.Concat(new[] { (byte)Json.Long(change, "byte", 0) }).ToArray(); break;
                    case "item_id": id = Json.Str(change, "id"); break;
                    case "swap": blob = File.ReadAllBytes(Path.Combine(vectors, Json.Str(item, Json.Str(change, "with") + "_file"))); break;
                    default: Fail("unknown change " + Json.Str(change, "change")); break;
                }
                byte[] damaged = blob;
                string what = "refused: " + Json.Str(change, "why") + " (" + Json.Str(change, "item") + " " + file + ")";
                if (file == "meta")
                    Throws(typeof(DecryptException), () => ItemCrypto.OpenMeta(key, id, damaged), what);
                else
                    Throws(typeof(DecryptException), () => Unseal(key, id, damaged), what);
                refusals++;
            }
            Equal(true, items.Count == 3 && refusals == 6, "all vectors read: " + items.Count + " items, " + refusals + " changes");

            byte[] newKey = ItemCrypto.NewKey();
            Equal(32, newKey.Length, "a new key has 32 bytes");
            Equal(false, newKey.SequenceEqual(ItemCrypto.NewKey()), "new keys differ");
            const string item1 = "4f0dbea2-6e87-4e9d-91a2-cd5a7e5b6c05", item2 = "5a1ecfb3-7f98-4fae-a2b3-de6b8f6c7d06";
            int m = ItemCrypto.MessageBytes;
            var random = new Random(7);
            foreach (int size in new[] { 0, 1, 1000, m - 1, m, m + 1, 3 * m + 5 })
            {
                var plain = new byte[size];
                random.NextBytes(plain);
                byte[] blob = Seal(newKey, item1, plain);
                Equal(ItemCrypto.SealedSize(size), (long)blob.Length, "sealed size of " + size);
                Equal(Sha256(plain), Sha256(Unseal(newKey, item1, blob)), "round trip of " + size);
            }
            var two = new byte[2 * m + 10];
            random.NextBytes(two);
            byte[] sealedTwo = Seal(newKey, item1, two);
            int oneMessage = Sodium.HeaderBytes + m + Sodium.ABytes;
            Throws(typeof(DecryptException), () => Unseal(newKey, item1, sealedTwo.Take(oneMessage).ToArray()), "cut after a whole message");
            Throws(typeof(DecryptException), () => Unseal(newKey, item1, sealedTwo.Take(sealedTwo.Length - 1).ToArray()), "last byte missing");
            Throws(typeof(DecryptException), () => Unseal(newKey, item1, sealedTwo.Take(5).ToArray()), "header cut");
            Throws(typeof(DecryptException), () => Unseal(ItemCrypto.NewKey(), item1, sealedTwo), "another key");
            Throws(typeof(DecryptException), () => Unseal(newKey, item2, sealedTwo), "another item");
            var meta2 = new Dictionary<string, object> { { "v", 1 }, { "kind", "file" }, { "name", "файл <1>.txt" }, { "size", 3 } };
            byte[] metaBlob = ItemCrypto.SealMeta(newKey, item1, meta2);
            Equal(Json.Write(meta2), Json.Write(ItemCrypto.OpenMeta(newKey, item1, metaBlob)), "meta round trip");
            Throws(typeof(DecryptException), () => ItemCrypto.OpenMeta(newKey, item2, metaBlob), "meta of another item");
            Throws(typeof(DecryptException), () => ItemCrypto.OpenMeta(newKey, item1, Seal(newKey, item1, Encoding.UTF8.GetBytes("{}"))),
                "content used as meta");
        }

        static byte[] Seal(byte[] key, string id, byte[] plain)
        {
            var output = new MemoryStream();
            Equal((long)plain.Length, ItemCrypto.SealStream(key, id, new MemoryStream(plain), output), "sealed plaintext size");
            return output.ToArray();
        }

        static byte[] Unseal(byte[] key, string id, byte[] blob)
        {
            var output = new MemoryStream();
            ItemCrypto.OpenStream(key, id, new MemoryStream(blob), output);
            return output.ToArray();
        }

        static void QrTests(string vectorsFile)
        {
            int count = 0;
            foreach (string line in File.ReadAllLines(vectorsFile).Where(l => l.Length > 0))
            {
                string[] f = line.Split(' ');
                Ecc level = (Ecc)"LMQH".IndexOf(f[2]);
                int version = int.Parse(f[1]), mask = int.Parse(f[3]), length = int.Parse(f[4]);
                byte[] data = Enumerable.Range(0, length).Select(i => (byte)((i * 7 + 3) % 256)).ToArray();
                QrCode qr;
                if (f[0] == "fixed")
                    qr = QrCode.EncodeBytes(data, level, version, mask);
                else
                {
                    QrCode picked = QrCode.EncodeBytes(data, (Ecc)"LMQH".IndexOf(f[5]));
                    Equal(version + " " + level, picked.Version + " " + picked.ErrorCorrection,
                        "QR picks segno's version and level for " + length + " bytes at " + f[5]);
                    Equal(true, picked.Mask >= 0 && picked.Mask < 8, "QR picks a mask");
                    qr = QrCode.EncodeBytes(data, level, version, mask);
                }
                Equal(f[f.Length - 1], Sha256(Encoding.ASCII.GetBytes(qr.Rows())), "QR symbol: " + line.Substring(0, line.Length - 65));
                count++;
            }
            Equal(182, count, "QR vectors read");
            string invitation = Pairing.Make("https://example.org/abcdefghijkl", "ABCDE-23456", new byte[32]);
            QrCode code = QrCode.EncodeBytes(Encoding.UTF8.GetBytes(invitation), Ecc.Medium);
            using (var bitmap = code.ToBitmap(4, 4))
            {
                Equal((code.Size + 8) * 4, bitmap.Width, "QR bitmap size");
                Equal(System.Drawing.Color.White.ToArgb(), bitmap.GetPixel(0, 0).ToArgb(), "QR border is light");
                Equal(System.Drawing.Color.Black.ToArgb(), bitmap.GetPixel(16, 16).ToArgb(), "QR finder corner is dark");
            }
        }

        static void PairingTests()
        {
            byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            // As cli/t7drop_cli/pairing.py writes it.
            const string python = "t7drop:pair?v=1&u=https%3A%2F%2Fexample.org%2Fabc&c=ABCDE-23456&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
            Equal(python, Pairing.Make("https://example.org/abc", "ABCDE-23456", key), "an invitation as the CLI writes it");
            Invitation parsed = Pairing.Parse(python);
            Equal("https://example.org/abc|ABCDE-23456|" + Hex.Of(key), parsed.Url + "|" + parsed.Code + "|" + Hex.Of(parsed.Key), "the CLI's invitation reads");
            Invitation plus = Pairing.Parse("t7drop:pair?v=1&u=https%3A%2F%2Fex.org%3A8443%2Fa+b%2Bc&c=X&k=" + new string('_', 42) + "8");
            Equal("https://ex.org:8443/a b+c", plus.Url, "+ is a space, %2B a plus (Python's urlencode)");
            Equal(true, plus.Key.All(b => b == 255), "a base64url key with - and _");
            foreach (string bad in new[] { "https://example.org", "t7drop:pair?v=2&u=x&c=y&k=z", python.Replace("k=", "x="),
                python.Replace("k=AAEC", "k=AE"), python.Replace("https%3A", "ftp%3A") })
                Throws(typeof(FormatException), () => Pairing.Parse(bad), "not an invitation: " + bad);
            Equal(true, Api.IsServerUrl("https://example.org/prefix"), "a server URL");
            Equal(false, Api.IsServerUrl("example.org/prefix"), "no scheme");
            Equal(false, Api.IsServerUrl("https://example.org/?q=1"), "a query");
        }

        static void NamesTests()
        {
            // As cli/t7drop_cli/files.py makes them.
            var cases = new Dictionary<string, string>
            {
                { "photo.jpg", "photo.jpg" }, { "a/b\\c.txt", "c.txt" }, { "con.txt", "_con.txt" }, { "COM1", "_COM1" },
                { "what?.txt", "what_.txt" }, { " spaced . ", "spaced" }, { "...", "file" }, { "", "file" },
                { "tab\there", "tab_here" }, { "LPT9.tar.gz", "_LPT9.tar.gz" }, { "файл.txt", "файл.txt" },
                { "a:b*c|d.png", "a_b_c_d.png" },
            };
            foreach (KeyValuePair<string, string> c in cases)
                Equal(c.Value, Names.Safe(c.Key), "safe name of '" + c.Key + "'");
            Equal(new string('x', 145) + ".jpeg", Names.Safe(new string('x', 200) + ".jpeg"), "a long name keeps its extension");
            Equal(new string('y', 150), Names.Safe(new string('y', 200)), "a long name without extension");
            Equal("file", Names.Safe(null), "no name");
            string folder = Path.Combine(scratch, "unique");
            Directory.CreateDirectory(folder);
            Equal(Path.Combine(folder, "a.txt"), Names.UniquePath(folder, "a.txt"), "a free name");
            File.WriteAllText(Path.Combine(folder, "a.txt"), "");
            File.WriteAllText(Path.Combine(folder, "a (1).txt"), "");
            Equal(Path.Combine(folder, "a (2).txt"), Names.UniquePath(folder, "a.txt"), "a taken name gets a number");
            Equal("2.0 MB", Names.Size(2 * 1024 * 1024 + 100), "sizes");
            Equal("17 bytes", Names.Size(17), "small sizes");
        }

        static void JsonTests()
        {
            Dictionary<string, object> o = Json.ParseObject("{\"a\":1,\"b\":1800000000000,\"c\":\"x\\u00e9\",\"d\":[1,2],\"e\":null,\"f\":true,\"g\":{\"h\":2}}");
            Equal(1L, Json.Long(o, "a", 0), "an int");
            Equal(1800000000000L, Json.Long(o, "b", 0), "a long");
            Equal("xé", Json.Str(o, "c"), "a string with an escape");
            Equal(2, Json.Arr(o, "d").Length, "an array");
            Equal(null, Json.Str(o, "e"), "null");
            Equal(true, Json.Bool(o, "f"), "a bool");
            Equal(2L, Json.Long(Json.Obj(o, "g"), "h", 0), "an object");
            Equal(-1L, Json.Long(o, "missing", -1), "a missing key");
            Equal(0, Json.Arr(o, "missing").Length, "a missing array");
            Throws(typeof(FormatException), () => Json.ParseObject("[1]"), "an array is no object");
            string path = Path.Combine(scratch, "x.json");
            Json.WriteFile(path, o);
            Json.WriteFile(path, new Dictionary<string, object> { { "k", "v" } });
            Equal("v", Json.Str(Json.ReadFile(path), "k"), "a file written twice");
            Equal(false, File.Exists(path + ".tmp"), "no temporary file left");
            File.WriteAllText(path, "{broken");
            Equal(null, Json.ReadFile(path), "a damaged file reads as missing");
            Equal(null, Json.ReadFile(Path.Combine(scratch, "none.json")), "a missing file");
        }

        static void SettingsTests()
        {
            Settings defaults = Settings.Parse("");
            Equal(false, defaults.AutoCopy, "auto copy is off by default");
            Equal(UpdateMode.Auto, defaults.Updates, "updates install by default");
            Equal(true, defaults.SendToMenu, "Send To is on by default");
            Equal(true, defaults.ReceiveFolder.EndsWith("\\t7drop"), "received files go to ...\\t7drop: " + defaults.ReceiveFolder);
            var settings = new Settings { DownloadFolder = @"D:\in", AutoCopy = true, Updates = UpdateMode.Notify, SendTo = "abc", SendToMenu = false };
            Settings back = Settings.Parse(settings.Format());
            Equal(settings.Format(), back.Format(), "settings survive the file");
            Equal(@"D:\in", back.ReceiveFolder, "a chosen folder");
            Equal(UpdateMode.Off, Settings.Parse("updates = OFF\r\nunknown=1\r\n# x=y").Updates, "case, spaces, unknown keys");
            string home = Path.Combine(scratch, "settings");
            Directory.CreateDirectory(home);
            Equal(null, Settings.Load(home), "no settings before the first save");
            settings.Save(home);
            Equal(settings.Format(), Settings.Load(home).Format(), "saved settings load");
        }

        static void StoreTests()
        {
            var store = new Store(Path.Combine(scratch, "store"));
            Equal(null, store.LoadConfig(), "not paired at first");
            var config = new Config
            {
                Url = "https://example.org/x", DeviceId = "aaaaaaaaaaaa", DeviceName = "laptop", Token = "secret-token",
                Key = ItemCrypto.NewKey(), First = true, Paired = 1800000000000,
            };
            store.SaveConfig(config);
            byte[] onDisk = File.ReadAllBytes(Path.Combine(store.Home, "pairing.dat"));
            Equal(false, Encoding.UTF8.GetString(onDisk).Contains("secret-token"), "the token is not readable on disk");
            Config loaded = store.LoadConfig();
            Equal(config.Token + Hex.Of(config.Key) + config.First + config.Paired,
                loaded.Token + Hex.Of(loaded.Key) + loaded.First + loaded.Paired, "the pairing survives DPAPI");

            store.SaveDevices(new object[]
            {
                Device("aaaaaaaaaaaa", "laptop", 200, true), Device("cccccccccccc", "old phone", 100, false),
                Device("dddddddddddd", "laptop-cli", 150, true), Device("eeeeeeeeeeee", "phone", 300, false),
            });
            Equal("eeeeeeeeeeee", store.Recipient(config, null), "the first device sends to the newest device it invited");
            Equal("dddddddddddd", store.Recipient(config, "dddddddddddd"), "unless the settings name another");
            Equal("eeeeeeeeeeee", store.Recipient(config, "gone00000000"), "a chosen device that is gone is ignored");
            store.SaveDevices(new object[] { Device("aaaaaaaaaaaa", "laptop", 200, true), Device("cccccccccccc", "old phone", 100, false) });
            Equal(null, store.Recipient(config, null), "not to a device of another group, paired before this one");
            store.SaveDevices(new object[]
            {
                Device("aaaaaaaaaaaa", "laptop", 200, true), Device("cccccccccccc", "old phone", 100, false),
                Device("dddddddddddd", "laptop-cli", 150, true), Device("eeeeeeeeeeee", "phone", 300, false),
            });
            var invited = new Config { DeviceId = "eeeeeeeeeeee", First = false };
            Equal("aaaaaaaaaaaa", store.Recipient(invited, null), "an invited device sends to the newest that may invite");
            Equal("phone", store.DeviceName("eeeeeeeeeeee"), "device names");
            store.SaveDevices(new object[] { Device("aaaaaaaaaaaa", "laptop", 200, true) });
            Equal(null, store.Recipient(config, null), "nobody to send to");

            Equal(false, store.WasReceived("x1"), "nothing received yet");
            store.MarkReceived("x1");
            store.MarkReceived("x2");
            Equal(true, store.WasReceived("x1") && store.WasReceived("x2"), "received ids are kept");
            int changes = 0;
            store.Changed += () => changes++;
            store.AddReceived(new Received { Id = "x1", Kind = "text", Text = "hello", From = "phone", Time = 1 });
            store.AddReceived(new Received { Id = "x2", Kind = "file", Name = "a.jpg", Path = @"C:\a.jpg", Size = 5, Time = 2 });
            Equal("x2 x1", string.Join(" ", store.History().Select(r => r.Id)), "the history, newest first");
            Equal("hello", store.History()[1].Text, "texts in the history");
            store.RemoveReceived("x1");
            Equal("x2", string.Join(" ", store.History().Select(r => r.Id)), "removed from the history");
            Equal(3, changes, "each change is announced");
            store.Forget();
            Equal(null, store.LoadConfig(), "unpaired");
        }

        static Dictionary<string, object> Device(string id, string name, long created, bool canInvite)
        {
            return new Dictionary<string, object> { { "id", id }, { "name", name }, { "created", created }, { "can_invite", canInvite } };
        }

        static void QueueTests()
        {
            var store = new Store(Path.Combine(scratch, "queue"));
            var config = new Config
            {
                Url = "https://example.org/x", DeviceId = "aaaaaaaaaaaa", DeviceName = "laptop", Token = "t", Key = ItemCrypto.NewKey(),
            };
            var transfers = new Transfers(store, config, () => Path.Combine(scratch, "in"));
            string source = Path.Combine(scratch, "photo.jpg");
            var content = new byte[3 * 1024 * 1024 + 7];
            new Random(3).NextBytes(content);
            File.WriteAllBytes(source, content);
            OutItem file = transfers.QueueFile(source, "bbbbbbbbbbbb");
            OutItem text = transfers.QueueText("Привет, t7drop", "bbbbbbbbbbbb");
            Equal(2, store.Outbox().Count, "two items queued");
            Equal(true, Protocol.IsItemId(file.Id), "a random lowercase UUID: " + file.Id);
            Equal(OutState.Queued, file.State, "queued");
            Equal("photo.jpg", file.Name, "the file's name");
            Equal("image/jpeg", Json.Str(file.Meta, "mime"), "the file's type");
            Equal((long)content.Length, file.Size, "plaintext size");
            byte[] data = File.ReadAllBytes(file.DataFile);
            Equal(ItemCrypto.SealedSize(content.Length), (long)data.Length, "ciphertext size");
            Equal(file.CSize, (long)data.Length, "csize");
            Equal(Sha256(data), file.Sha256, "sha256 of the ciphertext");
            Equal(Sha256(content), Sha256(Unseal(config.Key, file.Id, data)), "the ciphertext decrypts");
            Dictionary<string, object> meta = ItemCrypto.OpenMeta(config.Key, file.Id, File.ReadAllBytes(file.MetaFile));
            Equal("file photo.jpg bbbbbbbbbbbb aaaaaaaaaaaa 1", Json.Str(meta, "kind") + " " + Json.Str(meta, "name") + " " +
                Json.Str(meta, "to") + " " + Json.Str(meta, "from") + " " + Json.Str(meta, "v"), "meta");
            Equal("Привет, t7drop", Encoding.UTF8.GetString(Unseal(config.Key, text.Id, File.ReadAllBytes(text.DataFile))), "a text");
            Equal("Привет, t7drop", text.Preview, "a text's preview");
            Equal(0, Directory.GetDirectories(store.OutboxDir).Count(d => Path.GetFileName(d).StartsWith(".")), "no unfinished folder left");

            file.SetState(OutState.Failed, "the recipient is no longer paired");
            store.SaveDevices(new object[] { Device("aaaaaaaaaaaa", "laptop", 1, true), Device("cccccccccccc", "phone", 2, false) });
            transfers.Retry(store.Outbox().First(i => i.Id == file.Id), "cccccccccccc");
            OutItem retried = store.Outbox().First(i => i.Id == file.Id);
            Equal(OutState.Queued + " cccccccccccc", retried.State + " " + retried.To, "a retry goes to the paired device");
            Equal("cccccccccccc", Json.Str(ItemCrypto.OpenMeta(config.Key, file.Id, File.ReadAllBytes(retried.MetaFile)), "to"),
                "its meta names the new recipient");
            retried.Update(d => d["cancel"] = true);
            Equal(true, store.Outbox().First(i => i.Id == file.Id).CancelRequested, "a cancel request is saved");

            string big = Path.Combine(scratch, "big.bin");
            using (var f = File.Create(big))
                f.SetLength(Protocol.MaxItemBytes);
            Throws(typeof(TooLargeException), () => transfers.QueueFile(big, "bbbbbbbbbbbb"), "a file over 200 MB is refused at once");
            File.Delete(big);
            Equal(2, store.Outbox().Count, "nothing queued for it");
            store.CleanUp();
            Equal(2, store.Outbox().Count, "clean-up keeps pending items");
        }

        static void UpdaterTests(string builtExe)
        {
            Equal(new Version(1, 2, 3, 0), Updater.ParseVersion("v1.2.3"), "a tag with v");
            Equal(null, Updater.ParseVersion("v1.2.0-beta"), "a pre-release tag is no version");
            Equal(true, Updater.ParseVersion("v99.0") > Updater.CurrentVersion, "a higher tag is newer");
            Equal(false, Updater.ParseVersion("v" + Updater.Text(Updater.CurrentVersion)) > Updater.CurrentVersion,
                "the same tag is not newer");

            const string json = @"{""html_url"":""https://github.com/t7ko/t7drop/releases/tag/v0.2.0"",""tag_name"":""v0.2.0"",
                ""assets"":[{""name"":""t7drop-0.2.0.apk"",""browser_download_url"":""https://x/t7drop-0.2.0.apk"",""size"":9000000,""digest"":""sha256:00""},
                {""name"":""T7Drop.exe"",""browser_download_url"":""https://github.com/d/T7Drop.exe"",""size"":81920,""digest"":""sha256:ABCdef01""}]}";
            Release release = Updater.Parse(json);
            Equal(new Version(0, 2, 0, 0), release.Version, "release version");
            Equal("https://github.com/d/T7Drop.exe", release.DownloadUrl, "the exe, not the APK");
            Equal(81920L, release.Size, "asset size");
            Equal("abcdef01", release.Sha256, "digest, lower case");
            Equal(null, Updater.Parse(@"{""tag_name"":""v0.3.0"",""assets"":[{""name"":""t7drop-0.3.0.apk"",""browser_download_url"":""u""}]}"),
                "a release with only the APK");

            var good = new Release { Version = Updater.CurrentVersion, Size = new FileInfo(builtExe).Length, Sha256 = FileSha256(builtExe) };
            Throws(null, () => Updater.Verify(builtExe, good), "the built exe passes");
            Throws(typeof(InvalidDataException), () => Updater.Verify(builtExe,
                new Release { Version = good.Version, Size = good.Size + 1 }), "a wrong size fails");
            Throws(typeof(InvalidDataException), () => Updater.Verify(builtExe,
                new Release { Version = good.Version, Sha256 = "00" }), "a wrong SHA-256 fails");
            Throws(typeof(InvalidDataException), () => Updater.Verify(builtExe,
                new Release { Version = new Version(9, 9, 9, 0) }), "a wrong version fails");
            Throws(typeof(InvalidDataException), () => Updater.Verify(typeof(TestMain).Assembly.Location,
                new Release { Version = good.Version }), "another program fails");

            string folder = Path.Combine(scratch, "update");
            Directory.CreateDirectory(folder);
            string exe = Path.Combine(folder, "T7Drop.exe");
            File.WriteAllText(exe, "old");
            File.WriteAllText(exe + ".new", "new");
            Updater.Swap(exe, exe + ".new");
            Equal("new", File.ReadAllText(exe), "swap puts the new exe in place");
            Updater.SwapBack(exe, exe + ".new");
            Equal("old", File.ReadAllText(exe), "swap back restores the old exe");
            Updater.Swap(exe, exe + ".new");
            Equal(false, Updater.FinishUpdate(new string[0], exe), "a normal start is no update");
            Equal(false, File.Exists(exe + ".old"), "the next start deletes the old exe");
            Equal(true, Updater.FinishUpdate(new[] { "--updated-from", "999999" }, exe), "a start by an update");

            Equal(SecurityProtocolType.SystemDefault, ServicePointManager.SecurityProtocol,
                "HTTPS uses the TLS versions of Windows (the TargetFramework attribute works)");
        }

        static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return Hex.Of(sha.ComputeHash(data));
        }

        static string FileSha256(string path)
        {
            return Sha256(File.ReadAllBytes(path));
        }

        static void Throws(Type expected, Action action, string what)
        {
            Type thrown = null;
            try
            {
                action();
            }
            catch (Exception e)
            {
                thrown = e.GetType();
            }
            Equal(expected, thrown, what);
        }

        static void Fail(string what)
        {
            failures++;
            Console.WriteLine("FAIL " + what);
        }

        static void Equal<T>(T expected, T actual, string what)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
                return;
            Fail(what + ": expected <" + expected + ">, got <" + actual + ">");
        }
    }
}
