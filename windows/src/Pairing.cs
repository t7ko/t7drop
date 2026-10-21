using System;
using System.Collections.Generic;
using System.Linq;

namespace T7Drop
{
    class Invitation
    {
        public string Url, Code;
        public byte[] Key;
    }

    /// <summary>
    /// The invitation that the first device shows as a QR code (cli/t7drop_cli/pairing.py):
    /// t7drop:pair?v=1&amp;u=&lt;server URL&gt;&amp;c=&lt;one-time code&gt;&amp;k=&lt;group key, base64url&gt;
    /// </summary>
    static class Pairing
    {
        public const string Prefix = "t7drop:pair?";

        public static string B64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] FromB64Url(string text)
        {
            string base64 = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4));
        }

        public static string Make(string url, string code, byte[] key)
        {
            return Prefix + "v=1&u=" + Uri.EscapeDataString(url) + "&c=" + Uri.EscapeDataString(code) +
                "&k=" + B64Url(key);
        }

        /// <summary>Throws FormatException with a message for the user if text is no invitation.</summary>
        public static Invitation Parse(string text)
        {
            string value = (text ?? "").Trim();
            if (!value.StartsWith(Prefix, StringComparison.Ordinal))
                throw new FormatException("This is not a t7drop invitation.");
            var query = new Dictionary<string, string>();
            foreach (string part in value.Substring(Prefix.Length).Split('&').Where(p => p.Length > 0))
            {
                int equals = part.IndexOf('=');
                string name = Decode(equals < 0 ? part : part.Substring(0, equals));
                query[name] = equals < 0 ? "" : Decode(part.Substring(equals + 1));
            }
            string version, url, code, encodedKey;
            query.TryGetValue("v", out version);
            if (version != "1")
                throw new FormatException("This invitation is from another version of t7drop.");
            if (!query.TryGetValue("u", out url) || !query.TryGetValue("c", out code) || !query.TryGetValue("k", out encodedKey))
                throw new FormatException("The invitation is incomplete.");
            byte[] key;
            try
            {
                key = FromB64Url(encodedKey);
            }
            catch (FormatException)
            {
                throw new FormatException("The invitation is damaged.");
            }
            if (key.Length != Sodium.KeyBytes || !Api.IsServerUrl(url))
                throw new FormatException("The invitation is damaged.");
            return new Invitation { Url = url, Code = code, Key = key };
        }

        static string Decode(string text)
        {
            return Uri.UnescapeDataString(text.Replace('+', ' '));
        }
    }
}
