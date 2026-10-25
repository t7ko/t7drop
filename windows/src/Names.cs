using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace T7Drop
{
    /// <summary>File names from other devices, made safe (the rules of cli/t7drop_cli/files.py).</summary>
    static class Names
    {
        const string Forbidden = "<>:\"/\\|?*";
        const int MaxName = 150;
        static readonly HashSet<string> Reserved = new HashSet<string>(new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).Select(i => "COM" + i))
            .Concat(Enumerable.Range(1, 9).Select(i => "LPT" + i)));

        /// <summary>The last path part of name, without characters or names that Windows forbids.</summary>
        public static string Safe(string name)
        {
            string result = (name ?? "").Replace('\\', '/');
            result = result.Substring(result.LastIndexOf('/') + 1);
            // Invisible format characters, such as U+202E that shows "photo<U+202E>gpj.exe" as "photoexe.jpg".
            result = new string(result.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format)
                .Select(c => Forbidden.IndexOf(c) >= 0 || c < ' ' ? '_' : c).ToArray());
            result = result.Trim().TrimEnd('.', ' ');
            if (result.Length == 0)
                result = "file";
            int firstDot = result.IndexOf('.');
            if (Reserved.Contains((firstDot < 0 ? result : result.Substring(0, firstDot)).ToUpperInvariant()))
                result = "_" + result;
            if (result.Length > MaxName)
            {
                int dot = result.LastIndexOf('.');
                string ext = dot > 0 ? result.Substring(dot) : "";
                if (ext.Length > 20)
                    ext = ext.Substring(0, 20);
                int keep = MaxName - ext.Length;
                if (char.IsHighSurrogate(result[keep - 1]))
                    keep--;  // do not cut a character in half
                result = result.Substring(0, keep) + ext;
            }
            return result;
        }

        /// <summary>folder\name, or folder\name (1), (2)... if that exists.</summary>
        public static string UniquePath(string folder, string name)
        {
            string path = Path.Combine(folder, name);
            int dot = name.LastIndexOf('.');
            string root = dot > 0 ? name.Substring(0, dot) : name;
            string ext = dot > 0 ? name.Substring(dot) : "";
            for (int n = 1; File.Exists(path) || Directory.Exists(path); n++)
                path = Path.Combine(folder, root + " (" + n + ")" + ext);
            return path;
        }

        /// <summary>Such as "2.1 MB"; the CLI's human_size.</summary>
        public static string Size(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB" };
            double n = bytes;
            foreach (string unit in units)
            {
                if (n < 1024 || unit == "GB")
                    return unit == "bytes" ? bytes + " bytes" : n.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + unit;
                n /= 1024;
            }
            return bytes + " bytes";
        }
    }
}
