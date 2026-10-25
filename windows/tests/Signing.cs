using System;
using System.IO;
using System.Linq;
using System.Text;

namespace T7Drop.Tests
{
    /// <summary>
    /// The release key of the self-update (Updater.cs, docs/security.md), in Tests.exe because it
    /// already carries libsodium:
    ///   Tests.exe keygen &lt;key file&gt;        makes the key: its 32-byte seed, hex, into a new file;
    ///                                        prints the public key for Updater.ReleaseKeyHex
    ///   Tests.exe sign &lt;key file&gt; &lt;file&gt;   writes &lt;file&gt;.sig, the Ed25519 signature in hex; refuses
    ///                                        a key other than the one Updater.ReleaseKeyHex names
    /// scripts/release.ps1 signs dist\T7Drop.exe this way. The seed is never printed.
    /// </summary>
    static class Signing
    {
        public static int Run(string[] args)
        {
            Sodium.Load(Path.Combine(Path.GetTempPath(), "T7Drop-signing"));
            if (args.Length == 2 && args[0] == "keygen")
                return KeyGen(args[1]);
            if (args.Length == 3 && args[0] == "sign")
                return Sign(args[1], args[2]);
            Console.Error.WriteLine("Usage: Tests.exe keygen <key file> | Tests.exe sign <key file> <file>");
            return 2;
        }

        static int KeyGen(string keyFile)
        {
            if (File.Exists(keyFile))
            {
                Console.Error.WriteLine(keyFile + " exists already. A new key would make every installed copy refuse updates.");
                return 1;
            }
            byte[] seed = Sodium.RandomBytes(Sodium.SignSeedBytes);
            byte[] publicKey, secretKey;
            Sodium.SeedKeyPair(seed, out publicKey, out secretKey);
            byte[] text = Encoding.ASCII.GetBytes(Hex.Of(seed) + "\n");
            using (var file = new FileStream(keyFile, FileMode.CreateNew, FileAccess.Write))
                file.Write(text, 0, text.Length);
            Wipe(seed, secretKey, text);
            Console.WriteLine("Wrote the release key to " + keyFile + ". Its public key, for Updater.ReleaseKeyHex:");
            Console.WriteLine(Hex.Of(publicKey));
            return 0;
        }

        static int Sign(string keyFile, string path)
        {
            string hex = File.ReadAllText(keyFile, Encoding.ASCII).Trim();
            if (hex.Length != 2 * Sodium.SignSeedBytes || !hex.All(Uri.IsHexDigit))
            {
                Console.Error.WriteLine(keyFile + " holds no release key.");
                return 1;
            }
            byte[] seed = Hex.Parse(hex);
            byte[] publicKey, secretKey;
            Sodium.SeedKeyPair(seed, out publicKey, out secretKey);
            try
            {
                if (Hex.Of(publicKey) != Updater.ReleaseKeyHex)
                {
                    Console.Error.WriteLine("The key in " + keyFile + " is not the release key that Updater.ReleaseKeyHex names; " +
                        "installed copies would refuse what it signs.");
                    return 1;
                }
                byte[] data = File.ReadAllBytes(path);
                byte[] signature = Sodium.SignDetached(data, secretKey);
                if (!Sodium.VerifyDetached(signature, data, publicKey))
                {
                    Console.Error.WriteLine("The signature does not verify.");
                    return 1;
                }
                File.WriteAllText(path + ".sig", Hex.Of(signature) + "\n", new UTF8Encoding(false));
                Console.WriteLine("Signed " + path + " into " + path + ".sig");
                return 0;
            }
            finally
            {
                Wipe(seed, secretKey);
            }
        }

        static void Wipe(params byte[][] secrets)
        {
            foreach (byte[] secret in secrets)
                Array.Clear(secret, 0, secret.Length);
        }
    }
}
