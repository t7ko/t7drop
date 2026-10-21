package t7helpers.t7drop.crypto

import com.goterl.lazysodium.Sodium
import com.goterl.lazysodium.SodiumAndroid
import com.goterl.lazysodium.interfaces.SecretStream
import org.json.JSONException
import org.json.JSONObject
import java.io.InputStream
import java.io.OutputStream

/** A stream that is not intact: wrong key or item, altered, cut short, or extended. */
class DecryptException(message: String, cause: Throwable? = null) : Exception(message, cause)

/**
 * End-to-end encryption of items, as docs/design.md describes and the CLI does
 * (cli/t7drop_cli/crypto.py): libsodium secretstream (XChaCha20-Poly1305) under the
 * group key. An item is two streams: the meta, one FINAL message of JSON; and the
 * content, in messages of [MESSAGE_BYTES] of plaintext, the last one FINAL. Every message
 * carries the associated data "t7drop/1/meta/<item id>" or "t7drop/1/data/<item id>".
 */
object ItemCrypto {
    const val KEY_BYTES = 32
    const val HEADER_BYTES = 24
    const val A_BYTES = 17
    const val MESSAGE_BYTES = 1024 * 1024
    private const val TAG_MESSAGE: Byte = 0
    private const val TAG_FINAL: Byte = 3

    private val sodium: Sodium by lazy {
        SodiumAndroid().also {
            check(it.sodium_init() >= 0) { "libsodium did not start" }
            check(
                it.crypto_secretstream_xchacha20poly1305_abytes() == A_BYTES &&
                    it.crypto_secretstream_xchacha20poly1305_headerbytes() == HEADER_BYTES &&
                    it.crypto_secretstream_xchacha20poly1305_keybytes() == KEY_BYTES &&
                    it.crypto_secretstream_xchacha20poly1305_tag_message() == TAG_MESSAGE &&
                    it.crypto_secretstream_xchacha20poly1305_tag_final() == TAG_FINAL,
            ) { "unexpected libsodium constants" }
        }
    }

    fun newKey(): ByteArray = ByteArray(KEY_BYTES).also { sodium.crypto_secretstream_xchacha20poly1305_keygen(it) }

    fun sealedSize(plainSize: Long): Long {
        val messages = maxOf(1L, (plainSize + MESSAGE_BYTES - 1) / MESSAGE_BYTES)
        return HEADER_BYTES + plainSize + messages * A_BYTES
    }

    private fun ad(part: String, itemId: String) = "t7drop/1/$part/$itemId".toByteArray(Charsets.US_ASCII)

    private fun initPush(key: ByteArray): Pair<SecretStream.State, ByteArray> {
        val state = SecretStream.State()
        val header = ByteArray(HEADER_BYTES)
        check(sodium.crypto_secretstream_xchacha20poly1305_init_push(state, header, key) == 0) { "init_push failed" }
        return state to header
    }

    private fun push(state: SecretStream.State, message: ByteArray, ad: ByteArray, tag: Byte): ByteArray {
        val cipher = ByteArray(message.size + A_BYTES)
        val result = sodium.crypto_secretstream_xchacha20poly1305_push(
            state, cipher, null, message, message.size.toLong(), ad, ad.size.toLong(), tag,
        )
        check(result == 0) { "push failed" }
        return cipher
    }

    private fun initPull(key: ByteArray, header: ByteArray): SecretStream.State {
        val state = SecretStream.State()
        if (sodium.crypto_secretstream_xchacha20poly1305_init_pull(state, header, key) != 0) {
            throw DecryptException("stream header refused")
        }
        return state
    }

    /** The plaintext and the tag of one message. */
    private fun pull(state: SecretStream.State, cipher: ByteArray, ad: ByteArray): Pair<ByteArray, Byte> {
        if (cipher.size < A_BYTES) throw DecryptException("stream cut short")
        val message = ByteArray(cipher.size - A_BYTES)
        val tag = ByteArray(1)
        val result = sodium.crypto_secretstream_xchacha20poly1305_pull(
            state, message, null, tag, cipher, cipher.size.toLong(), ad, ad.size.toLong(),
        )
        if (result != 0) throw DecryptException("stream does not decrypt")
        return message to tag[0]
    }

    fun sealMeta(key: ByteArray, itemId: String, meta: JSONObject): ByteArray {
        val (state, header) = initPush(key)
        return header + push(state, meta.toString().toByteArray(Charsets.UTF_8), ad("meta", itemId), TAG_FINAL)
    }

    fun openMeta(key: ByteArray, itemId: String, blob: ByteArray): JSONObject {
        if (blob.size < HEADER_BYTES + A_BYTES) throw DecryptException("meta too short")
        val state = initPull(key, blob.copyOfRange(0, HEADER_BYTES))
        val (text, tag) = pull(state, blob.copyOfRange(HEADER_BYTES, blob.size), ad("meta", itemId))
        if (tag != TAG_FINAL) throw DecryptException("meta is not one final message")
        return try {
            JSONObject(String(text, Charsets.UTF_8))
        } catch (e: JSONException) {
            throw DecryptException("meta is not JSON", e)
        }
    }

    /** Encrypts all of [input] into [output]; returns the plaintext size. */
    fun sealStream(key: ByteArray, itemId: String, input: InputStream, output: OutputStream): Long {
        val (state, header) = initPush(key)
        output.write(header)
        val ad = ad("data", itemId)
        var total = 0L
        var block = readFull(input, MESSAGE_BYTES)
        while (true) {
            val following = if (block.size == MESSAGE_BYTES) readFull(input, MESSAGE_BYTES) else ByteArray(0)
            val last = following.isEmpty()
            output.write(push(state, block, ad, if (last) TAG_FINAL else TAG_MESSAGE))
            total += block.size
            if (last) return total
            block = following
        }
    }

    /**
     * Decrypts [input] into [output]; returns the plaintext size. Throws DecryptException on
     * any damage, possibly after writing part of the plaintext: write to something temporary.
     */
    fun openStream(key: ByteArray, itemId: String, input: InputStream, output: OutputStream): Long {
        val header = readFull(input, HEADER_BYTES)
        if (header.size != HEADER_BYTES) throw DecryptException("stream too short")
        val state = initPull(key, header)
        val ad = ad("data", itemId)
        var total = 0L
        while (true) {
            val message = readFull(input, MESSAGE_BYTES + A_BYTES)
            val (block, tag) = pull(state, message, ad)
            output.write(block)
            total += block.size
            if (tag == TAG_FINAL) {
                if (input.read() != -1) throw DecryptException("data after the final message")
                return total
            }
            if (tag != TAG_MESSAGE || message.size != MESSAGE_BYTES + A_BYTES) throw DecryptException("stream cut short")
        }
    }

    private fun readFull(input: InputStream, n: Int): ByteArray {
        val buffer = ByteArray(n)
        var filled = 0
        while (filled < n) {
            val read = input.read(buffer, filled, n - filled)
            if (read < 0) break
            filled += read
        }
        return if (filled == n) buffer else buffer.copyOf(filled)
    }
}
