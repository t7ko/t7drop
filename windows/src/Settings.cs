using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace T7Drop
{
    /// <summary>What to do with new releases on GitHub.</summary>
    public enum UpdateMode
    {
        Auto,    // install them without asking
        Notify,  // offer them
        Off      // check only when asked to
    }

    /// <summary>The settings, as key=value lines in settings.ini in the data folder.</summary>
    class Settings
    {
        public string DownloadFolder;  // null: Downloads\t7drop
        public bool AutoCopy;          // copy received texts to the clipboard at once
        public UpdateMode Updates = UpdateMode.Auto;
        public string SendTo;          // a device id, or null for the usual recipient (Store.Recipient)
        public bool SendToMenu = true; // the entry in Explorer's Send To menu

        public string ReceiveFolder
        {
            get { return string.IsNullOrEmpty(DownloadFolder) ? Paths.DefaultDownloads : DownloadFolder; }
        }

        public static string FilePath(string home)
        {
            return Path.Combine(home, "settings.ini");
        }

        /// <summary>The saved settings, or null when nothing has been saved yet (the first start).</summary>
        public static Settings Load(string home)
        {
            string path = FilePath(home);
            return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : null;
        }

        public void Save(string home)
        {
            Files.WriteAtomically(FilePath(home), new UTF8Encoding(false).GetBytes(Format()));
        }

        public static Settings Parse(string text)
        {
            var settings = new Settings();
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                int equals = line.IndexOf('=');
                if (line.StartsWith("#") || equals < 0)
                    continue;
                string key = line.Substring(0, equals).Trim().ToLowerInvariant();
                string value = line.Substring(equals + 1).Trim();
                switch (key)
                {
                    case "download_folder":
                        settings.DownloadFolder = value.Length > 0 ? value : null;
                        break;
                    case "auto_copy":
                        settings.AutoCopy = IsTrue(value);
                        break;
                    case "updates":
                        settings.Updates = value.Equals("notify", StringComparison.OrdinalIgnoreCase) ? UpdateMode.Notify
                            : value.Equals("off", StringComparison.OrdinalIgnoreCase) ? UpdateMode.Off : UpdateMode.Auto;
                        break;
                    case "send_to":
                        settings.SendTo = value.Length > 0 ? value : null;
                        break;
                    case "send_to_menu":
                        settings.SendToMenu = IsTrue(value);
                        break;
                }
            }
            return settings;
        }

        static bool IsTrue(string value)
        {
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public string Format()
        {
            var text = new StringBuilder();
            text.AppendLine("# t7drop settings. Change them in the Settings window, or edit this file and restart t7drop.");
            text.AppendLine("# download_folder: empty for Downloads\\t7drop");
            text.AppendLine("download_folder=" + (DownloadFolder ?? ""));
            text.AppendLine("auto_copy=" + (AutoCopy ? "true" : "false"));
            text.AppendLine("# updates: auto (install new releases), notify (offer them) or off");
            text.AppendLine("updates=" + Updates.ToString().ToLowerInvariant());
            text.AppendLine("# send_to: a device id; empty for the device paired last");
            text.AppendLine("send_to=" + (SendTo ?? ""));
            text.AppendLine("send_to_menu=" + (SendToMenu ? "true" : "false"));
            return text.ToString();
        }
    }

    static class Paths
    {
        /// <summary>
        /// %LOCALAPPDATA%\T7Drop, or T7DROP_HOME: a test copy with its own data, which leaves the
        /// registry, Send To and the single-instance name of the installed copy alone.
        /// </summary>
        public static string Home
        {
            get
            {
                string test = TestHome;
                return test ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "T7Drop");
            }
        }

        public static string TestHome
        {
            get
            {
                string value = Environment.GetEnvironmentVariable("T7DROP_HOME");
                return string.IsNullOrEmpty(value) ? null : Path.GetFullPath(value);
            }
        }

        public static bool IsTestCopy
        {
            get { return TestHome != null; }
        }

        /// <summary>
        /// Downloads\t7drop, where the user's Downloads folder is (it may have been moved); a test
        /// copy's own downloads folder in its data folder.
        /// </summary>
        public static string DefaultDownloads
        {
            get { return IsTestCopy ? Path.Combine(Home, "downloads") : Path.Combine(KnownDownloads(), "t7drop"); }
        }

        static string KnownDownloads()
        {
            IntPtr path;
            var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out path) == 0)
            {
                try
                {
                    return Marshal.PtrToStringUni(path);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(path);
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    }

    /// <summary>t7drop.log in the data folder: what happened, for finding problems. No texts, names or keys.</summary>
    static class Log
    {
        const long MaxBytes = 1024 * 1024;
        static readonly object writeLock = new object();
        static string path;

        public static void Open(string home)
        {
            path = Path.Combine(home, "t7drop.log");
        }

        public static string FilePath
        {
            get { return path; }
        }

        /// <summary>The test harness prints the log.</summary>
        public static Action<string> Echo { get; set; }

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message;
            Action<string> echo = Echo;
            if (echo != null)
                echo(line);
            if (path == null)
                return;
            lock (writeLock)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        File.Delete(path + ".old");
                        File.Move(path, path + ".old");
                    }
                    File.AppendAllText(path, line + "\r\n", new UTF8Encoding(false));
                }
                catch (Exception)
                {
                    // a log that cannot be written must not stop transfers
                }
            }
        }
    }
}
