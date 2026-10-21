using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace T7Drop
{
    /// <summary>The stream is not intact: wrong key or item, altered, cut short, or extended.</summary>
    class DecryptException : Exception
    {
        public DecryptException(string message) : base(message) { }
    }

    /// <summary>
    /// End-to-end encryption of items (docs/design.md, "Encryption"), as cli/t7drop_cli/crypto.py
    /// does it: libsodium secretstream (XChaCha20-Poly1305) under the group key. An item is two
    /// streams. The meta stream is one message, tagged FINAL, holding JSON. The content stream
    /// holds the content in messages of MessageBytes of plaintext, the last one (possibly shorter,
    /// possibly empty) tagged FINAL. Every message carries the associated data
    /// "t7drop/1/meta/&lt;item id&gt;" or "t7drop/1/data/&lt;item id&gt;".
    /// </summary>
    static class ItemCrypto
    {
        public const int MessageBytes = 1024 * 1024;

        public static byte[] NewKey()
        {
            return Sodium.RandomBytes(Sodium.KeyBytes);
        }

        static byte[] Ad(string part, string itemId)
        {
            return Encoding.ASCII.GetBytes("t7drop/1/" + part + "/" + itemId);
        }

        public static long SealedSize(long plainSize)
        {
            long messages = Math.Max(1, (plainSize + MessageBytes - 1) / MessageBytes);
            return Sodium.HeaderBytes + plainSize + messages * Sodium.ABytes;
        }

        public static byte[] SealMeta(byte[] key, string itemId, Dictionary<string, object> meta)
        {
            byte[] text = Encoding.UTF8.GetBytes(Json.Write(meta));
            var state = new byte[Sodium.StateBytes];
            var header = new byte[Sodium.HeaderBytes];
            Check(Sodium.crypto_secretstream_xchacha20poly1305_init_push(state, header, CheckKey(key)));
            byte[] ad = Ad("meta", itemId);
            var sealedText = new byte[text.Length + Sodium.ABytes];
            ulong length;
            Check(Sodium.crypto_secretstream_xchacha20poly1305_push(state, sealedText, out length,
                text, (ulong)text.Length, ad, (ulong)ad.Length, Sodium.TagFinal));
            var blob = new byte[header.Length + (int)length];
            Buffer.BlockCopy(header, 0, blob, 0, header.Length);
            Buffer.BlockCopy(sealedText, 0, blob, header.Length, (int)length);
            return blob;
        }

        public static Dictionary<string, object> OpenMeta(byte[] key, string itemId, byte[] blob)
        {
            if (blob.Length < Sodium.HeaderBytes + Sodium.ABytes)
                throw new DecryptException("meta too short");
            var state = new byte[Sodium.StateBytes];
            var header = new byte[Sodium.HeaderBytes];
            Buffer.BlockCopy(blob, 0, header, 0, header.Length);
            if (Sodium.crypto_secretstream_xchacha20poly1305_init_pull(state, header, CheckKey(key)) != 0)
                throw new DecryptException("meta header refused");
            int clen = blob.Length - header.Length;
            var c = new byte[clen];
            Buffer.BlockCopy(blob, header.Length, c, 0, clen);
            var m = new byte[clen];
            byte[] ad = Ad("meta", itemId);
            ulong mlen;
            byte tag;
            if (Sodium.crypto_secretstream_xchacha20poly1305_pull(state, m, out mlen, out tag, c, (ulong)clen, ad, (ulong)ad.Length) != 0)
                throw new DecryptException("meta does not decrypt");
            if (tag != Sodium.TagFinal)
                throw new DecryptException("meta is not one final message");
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(m, 0, (int)mlen);
            }
            catch (ArgumentException)
            {
                throw new DecryptException("meta is not UTF-8");
            }
            Dictionary<string, object> meta;
            try
            {
                meta = Json.Parse(text) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                throw new DecryptException("meta is not JSON");
            }
            if (meta == null)
                throw new DecryptException("meta is not a JSON object");
            return meta;
        }

        /// <summary>Encrypts all that src gives into dst; returns the plaintext size.</summary>
        public static long SealStream(byte[] key, string itemId, Stream src, Stream dst)
        {
            var state = new byte[Sodium.StateBytes];
            var header = new byte[Sodium.HeaderBytes];
            Check(Sodium.crypto_secretstream_xchacha20poly1305_init_push(state, header, CheckKey(key)));
            dst.Write(header, 0, header.Length);
            byte[] ad = Ad("data", itemId);
            var block = new byte[MessageBytes];
            var following = new byte[MessageBytes];
            var sealedBlock = new byte[MessageBytes + Sodium.ABytes];
            long total = 0;
            int blockLength = ReadFull(src, block, MessageBytes);
            while (true)
            {
                int followingLength = blockLength == MessageBytes ? ReadFull(src, following, MessageBytes) : 0;
                bool last = followingLength == 0;
                ulong length;
                Check(Sodium.crypto_secretstream_xchacha20poly1305_push(state, sealedBlock, out length, block,
                    (ulong)blockLength, ad, (ulong)ad.Length, last ? Sodium.TagFinal : Sodium.TagMessage));
                dst.Write(sealedBlock, 0, (int)length);
                total += blockLength;
                if (last)
                    return total;
                byte[] swap = block;
                block = following;
                following = swap;
                blockLength = followingLength;
            }
        }

        /// <summary>
        /// Decrypts src into dst; returns the plaintext size. Throws DecryptException on any
        /// damage, possibly after writing part of the plaintext: write to a temporary file.
        /// </summary>
        public static long OpenStream(byte[] key, string itemId, Stream src, Stream dst)
        {
            var header = new byte[Sodium.HeaderBytes];
            if (ReadFull(src, header, header.Length) != header.Length)
                throw new DecryptException("stream too short");
            var state = new byte[Sodium.StateBytes];
            if (Sodium.crypto_secretstream_xchacha20poly1305_init_pull(state, header, CheckKey(key)) != 0)
                throw new DecryptException("stream header refused");
            byte[] ad = Ad("data", itemId);
            var message = new byte[MessageBytes + Sodium.ABytes];
            var block = new byte[MessageBytes];
            long total = 0;
            while (true)
            {
                int length = ReadFull(src, message, message.Length);
                if (length < Sodium.ABytes)
                    throw new DecryptException("stream cut short");
                ulong blockLength;
                byte tag;
                if (Sodium.crypto_secretstream_xchacha20poly1305_pull(state, block, out blockLength, out tag,
                    message, (ulong)length, ad, (ulong)ad.Length) != 0)
                    throw new DecryptException("stream does not decrypt");
                dst.Write(block, 0, (int)blockLength);
                total += (long)blockLength;
                if (tag == Sodium.TagFinal)
                {
                    if (src.ReadByte() != -1)
                        throw new DecryptException("data after the final message");
                    return total;
                }
                if (tag != Sodium.TagMessage || length != message.Length)
                    throw new DecryptException("stream cut short");
            }
        }

        static int ReadFull(Stream src, byte[] buffer, int count)
        {
            int done = 0;
            while (done < count)
            {
                int read = src.Read(buffer, done, count - done);
                if (read <= 0)
                    break;
                done += read;
            }
            return done;
        }

        static byte[] CheckKey(byte[] key)
        {
            if (key == null || key.Length != Sodium.KeyBytes)
                throw new ArgumentException("The group key must have " + Sodium.KeyBytes + " bytes.");
            return key;
        }

        static void Check(int result)
        {
            if (result != 0)
                throw new InvalidOperationException("libsodium refused to encrypt.");
        }
    }
}
