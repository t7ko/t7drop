using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace T7Drop
{
    /// <summary>
    /// JSON through .NET Framework's JavaScriptSerializer: objects come as
    /// Dictionary&lt;string, object&gt;, arrays as object[], numbers as int, long or decimal.
    /// </summary>
    static class Json
    {
        static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        }

        public static object Parse(string text)
        {
            return Serializer().DeserializeObject(text);
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            var value = Parse(text) as Dictionary<string, object>;
            if (value == null)
                throw new FormatException("not a JSON object");
            return value;
        }

        public static string Write(object value)
        {
            return Serializer().Serialize(value);
        }

        /// <summary>The object in a JSON file; null if there is no such file, or it is damaged.</summary>
        public static Dictionary<string, object> ReadFile(string path)
        {
            try
            {
                return ParseObject(Files.ReadText(path));
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;  // not JSON
            }
            catch (FormatException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>Writes a file so that readers see the old or the new content, never a part.</summary>
        public static void WriteFile(string path, object value)
        {
            Files.WriteAtomically(path, new UTF8Encoding(false).GetBytes(Write(value)));
        }

        public static string Str(IDictionary<string, object> o, string key)
        {
            object value;
            return o != null && o.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        public static long Long(IDictionary<string, object> o, string key, long fallback)
        {
            object value;
            if (o == null || !o.TryGetValue(key, out value) || value == null)
                return fallback;
            try
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public static bool Bool(IDictionary<string, object> o, string key)
        {
            object value;
            return o != null && o.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        public static Dictionary<string, object> Obj(IDictionary<string, object> o, string key)
        {
            object value;
            return o != null && o.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        public static object[] Arr(IDictionary<string, object> o, string key)
        {
            object value;
            return o != null && o.TryGetValue(key, out value) && value is object[] ? (object[])value : new object[0];
        }
    }

    static class Files
    {
        const uint MoveFileReplaceExisting = 1, MoveFileWriteThrough = 8;

        /// <summary>
        /// Writes path through a temporary file beside it, then renames that over it: readers see
        /// the old content or the new, never a part, and never a missing file. ReadText and
        /// ReadBytes share delete access, so that a reader never blocks the rename.
        /// </summary>
        public static void WriteAtomically(string path, byte[] data)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
            for (int attempt = 1; ; attempt++)
            {
                if (MoveFileEx(temp, path, MoveFileReplaceExisting | MoveFileWriteThrough))
                    return;
                int error = Marshal.GetLastWin32Error();
                if (attempt == 20)
                {
                    TryDelete(temp);
                    throw new IOException("Could not replace " + path + ": " + new System.ComponentModel.Win32Exception(error).Message);
                }
                Thread.Sleep(50);  // a program without delete sharing (a virus scanner) has it open
            }
        }

        public static string ReadText(string path)
        {
            return Encoding.UTF8.GetString(ReadBytes(path));
        }

        public static byte[] ReadBytes(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var data = new byte[stream.Length];
                int done = 0;
                while (done < data.Length)
                {
                    int read = stream.Read(data, done, data.Length - done);
                    if (read <= 0)
                        break;
                    done += read;
                }
                return done == data.Length ? data : data.Take(done).ToArray();
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string existing, string replacement, uint flags);

        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
                // in use: a later clean-up deletes it
            }
        }

        public static void TryDeleteFolder(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception)
            {
                // in use: a later clean-up deletes it
            }
        }

        public static string Sha256(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return Hex.Of(sha.ComputeHash(stream));
        }

        public const string ZoneStream = ":Zone.Identifier";
        public const string InternetZone = "[ZoneTransfer]\r\nZoneId=3\r\n";

        /// <summary>
        /// Marks a file as downloaded from the internet (the Mark of the Web), as browsers do, so
        /// that SmartScreen and Office's Protected View treat it so (docs/security.md). Returns
        /// false where the folder cannot carry it (a FAT or exFAT drive has no alternate streams).
        /// .NET Framework refuses a colon in a path, hence CreateFile.
        /// </summary>
        public static bool MarkFromInternet(string path)
        {
            using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = CreateFile(path + ZoneStream, GenericWrite,
                0, IntPtr.Zero, CreateAlways, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return false;
                using (var stream = new FileStream(handle, FileAccess.Write))
                {
                    byte[] text = Encoding.ASCII.GetBytes(InternetZone);
                    stream.Write(text, 0, text.Length);
                }
                return true;
            }
        }

        /// <summary>The Zone.Identifier stream of a file, or null if it has none.</summary>
        public static string ReadZone(string path)
        {
            using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = CreateFile(path + ZoneStream, GenericRead,
                FileShareRead, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return null;
                using (var stream = new FileStream(handle, FileAccess.Read))
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                    return reader.ReadToEnd();
            }
        }

        const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, CreateAlways = 2, OpenExisting = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
