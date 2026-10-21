using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace T7Drop
{
    /// <summary>
    /// libsodium, the official x64 build (lib/README.md). The exe carries it as a resource and
    /// writes it into lib\&lt;hash&gt;\libsodium.dll under the data folder at the first start. Once
    /// LoadLibrary has loaded it from there, Windows binds the DllImports below to that module.
    /// </summary>
    static class Sodium
    {
        const string Dll = "libsodium";
        const string ResourceName = "libsodium.dll";
        // The SHA-256 of lib/libsodium.dll; a file that differs is written out again.
        public const string DllSha256 = "f656aeb789bfc3a2ac587fae1d7cfe278bbe8ce73f28da73b4d08282ea8f9fe3";
        public const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        public const int KeyBytes = 32, HeaderBytes = 24, ABytes = 17;
        public const byte TagMessage = 0, TagFinal = 3;

        static readonly object loadLock = new object();
        static bool loaded;
        static int stateBytes;

        public static int StateBytes
        {
            get { return stateBytes; }
        }

        /// <summary>Writes out and loads libsodium once; throws with a message for the user if it cannot.</summary>
        public static void Load(string folder)
        {
            lock (loadLock)
            {
                if (loaded)
                    return;
                string path = Path.Combine(folder, "lib", DllSha256.Substring(0, 12), ResourceName);
                if (!File.Exists(path) || FileSha256(path) != DllSha256)
                    WriteOut(path);
                if (LoadLibrary(path) == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    string reason = new Win32Exception(error).Message;
                    if (error == 126)  // a DLL it needs is missing: the Visual C++ runtime
                        throw new InvalidOperationException("libsodium.dll could not be loaded: " + reason +
                            " It needs the Microsoft Visual C++ Redistributable (x64): " + VcRedistUrl);
                    throw new InvalidOperationException("libsodium.dll could not be loaded from " + path + ": " + reason);
                }
                if (sodium_init() < 0)
                    throw new InvalidOperationException("libsodium could not start (sodium_init failed).");
                stateBytes = (int)crypto_secretstream_xchacha20poly1305_statebytes().ToUInt64();
                if ((int)crypto_secretstream_xchacha20poly1305_headerbytes().ToUInt64() != HeaderBytes
                    || (int)crypto_secretstream_xchacha20poly1305_abytes().ToUInt64() != ABytes
                    || (int)crypto_secretstream_xchacha20poly1305_keybytes().ToUInt64() != KeyBytes
                    || crypto_secretstream_xchacha20poly1305_tag_final() != TagFinal
                    || crypto_secretstream_xchacha20poly1305_tag_message() != TagMessage)
                    throw new InvalidOperationException("This libsodium has other secretstream sizes.");
                loaded = true;
            }
        }

        public static string Version
        {
            get { return Marshal.PtrToStringAnsi(sodium_version_string()); }
        }

        static void WriteOut(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (resource == null)
                    throw new InvalidOperationException("This program was built without libsodium.dll.");
                using (FileStream file = File.Create(temp))
                    resource.CopyTo(file);
            }
            if (FileSha256(temp) != DllSha256)
            {
                File.Delete(temp);
                throw new InvalidOperationException("The libsodium.dll inside this program is damaged.");
            }
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        static string FileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return Hex.Of(sha.ComputeHash(stream));
        }

        public static byte[] RandomBytes(int count)
        {
            var buffer = new byte[count];
            randombytes_buf(buffer, new UIntPtr((uint)count));
            return buffer;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int sodium_init();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr sodium_version_string();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void randombytes_buf(byte[] buffer, UIntPtr size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern UIntPtr crypto_secretstream_xchacha20poly1305_statebytes();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern UIntPtr crypto_secretstream_xchacha20poly1305_headerbytes();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern UIntPtr crypto_secretstream_xchacha20poly1305_abytes();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern UIntPtr crypto_secretstream_xchacha20poly1305_keybytes();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern byte crypto_secretstream_xchacha20poly1305_tag_final();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern byte crypto_secretstream_xchacha20poly1305_tag_message();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int crypto_secretstream_xchacha20poly1305_init_push(byte[] state, byte[] header, byte[] key);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int crypto_secretstream_xchacha20poly1305_push(byte[] state, byte[] c, out ulong clen,
            byte[] m, ulong mlen, byte[] ad, ulong adlen, byte tag);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int crypto_secretstream_xchacha20poly1305_init_pull(byte[] state, byte[] header, byte[] key);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int crypto_secretstream_xchacha20poly1305_pull(byte[] state, byte[] m, out ulong mlen,
            out byte tag, byte[] c, ulong clen, byte[] ad, ulong adlen);
    }

    static class Hex
    {
        public static string Of(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static byte[] Parse(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
            return bytes;
        }
    }
}
