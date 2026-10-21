using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace T7Drop
{
    /// <summary>Links in texts, opening files and folders.</summary>
    static class Links
    {
        public const string HomePage = "https://github.com/" + Updater.Repository;
        static readonly Regex Url = new Regex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase);

        /// <summary>The link, if the whole text (trimmed) is one http(s) link; otherwise null.</summary>
        public static string WholeLink(string text)
        {
            string trimmed = (text ?? "").Trim();
            Match match = Url.Match(trimmed);
            return match.Success && match.Index == 0 && match.Length == trimmed.Length ? trimmed : null;
        }

        public static string FirstLink(string text)
        {
            Match match = Url.Match(text ?? "");
            return match.Success ? match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '\'', '"') : null;
        }

        /// <summary>Opens an http(s) link in the browser; other schemes from another device are not run.</summary>
        public static void Open(string url)
        {
            if (url == null || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return;
            Start(url);
        }

        public static void OpenFile(string path)
        {
            if (File.Exists(path))
                Start(path);
            else
                MessageBox.Show(path + " is not there any more.", "t7drop", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void OpenFolder(string folder)
        {
            Directory.CreateDirectory(folder);
            Start(folder);
        }

        /// <summary>Opens the file's folder in Explorer with the file selected.</summary>
        public static void ShowInFolder(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    using (Process.Start("explorer.exe", "/select,\"" + path + "\"")) { }
                }
                catch (Exception)
                {
                    OpenFolder(Path.GetDirectoryName(path));
                }
            }
            else if (path != null && Directory.Exists(Path.GetDirectoryName(path)))
                OpenFolder(Path.GetDirectoryName(path));
        }

        static void Start(string what)
        {
            try
            {
                using (Process.Start(what)) { }
            }
            catch (Exception e)
            {
                MessageBox.Show("Windows could not open " + what + ":\n" + e.Message, "t7drop",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }

    /// <summary>What the clipboard holds that can be sent: files, a text, or an image.</summary>
    class Outgoing
    {
        public string[] Paths = new string[0];
        public string Text;
        public byte[] Image;      // PNG
        public string ImageName;

        public bool IsEmpty
        {
            get { return Paths.Length == 0 && string.IsNullOrEmpty(Text) && Image == null; }
        }
    }

    static class ClipboardAccess
    {
        /// <summary>
        /// Copied files, else a text, else an image as PNG; null if there is nothing to send.
        /// Office copies text with a picture of it: the text wins. UI thread only.
        /// </summary>
        public static Outgoing Read()
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return ReadOnce();
                }
                catch (ExternalException)
                {
                    if (attempt == 10)
                        throw;
                    Thread.Sleep(100);  // another program has the clipboard open
                }
            }
        }

        static Outgoing ReadOnce()
        {
            IDataObject data = Clipboard.GetDataObject();
            if (data == null)
                return null;
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null && paths.Length > 0)
                    return new Outgoing { Paths = paths };
            }
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = data.GetData(DataFormats.UnicodeText) as string;
                if (!string.IsNullOrEmpty(text))
                    return new Outgoing { Text = text };
            }
            byte[] png = null;
            if (data.GetDataPresent("PNG"))
            {
                var stream = data.GetData("PNG") as MemoryStream;
                if (stream != null)
                    png = stream.ToArray();
            }
            if (png == null && Clipboard.ContainsImage())
                using (Image image = Clipboard.GetImage())
                    if (image != null)
                        png = ToPng(image);
            if (png != null)
                return new Outgoing { Image = png, ImageName = "clipboard-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".png" };
            return null;
        }

        public static byte[] ToPng(Image image)
        {
            using (var stream = new MemoryStream())
            {
                image.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        /// <summary>Copies text to the clipboard; false if another program held it too long.</summary>
        public static bool CopyText(string text)
        {
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true, 10, 100);
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }
    }

    /// <summary>"t7drop" in Explorer's Send To menu: a shortcut that runs T7Drop.exe --send with the files.</summary>
    static class SendTo
    {
        public const string Argument = "--send";

        static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "t7drop.lnk"); }
        }

        public static bool IsEnabled
        {
            get { return File.Exists(ShortcutPath); }
        }

        /// <summary>Creates the shortcut, or points it at this exe if it was moved; or deletes it.</summary>
        public static void Set(bool enabled)
        {
            string path = ShortcutPath;
            if (!enabled)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            string exe = Application.ExecutablePath;
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                try
                {
                    Type linkType = link.GetType();
                    string target = linkType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
                    string arguments = linkType.InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null) as string;
                    if (File.Exists(path) && string.Equals(target, exe, StringComparison.OrdinalIgnoreCase) && arguments == Argument)
                        return;
                    linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exe });
                    linkType.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { Argument });
                    linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(exe) });
                    linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Send to the phone with t7drop" });
                    linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { exe + ",0" });
                    linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(link);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
