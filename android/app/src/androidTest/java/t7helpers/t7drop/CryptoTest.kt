package t7helpers.t7drop

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import org.junit.runner.RunWith
import t7helpers.t7drop.crypto.DecryptException
import t7helpers.t7drop.crypto.ItemCrypto
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import kotlin.random.Random

/**
 * The encryption with the real libsodium of the app, on a device: the test vectors that
 * every t7drop client must pass (testdata/vectors, written by tests/make_vectors.py), and
 * round trips at the edges of the message size.
 */
@RunWith(AndroidJUnit4::class)
class CryptoTest {
    private val assets = InstrumentationRegistry.getInstrumentation().context.assets
    private val item = "4f0dbea2-6e87-4e9d-91a2-cd5a7e5b6c05"

    private fun read(name: String) = assets.open("vectors/$name").use { it.readBytes() }

    private fun seal(key: ByteArray, id: String, plain: ByteArray): ByteArray {
        val out = ByteArrayOutputStream()
        assertEquals(plain.size.toLong(), ItemCrypto.sealStream(key, id, ByteArrayInputStream(plain), out))
        return out.toByteArray()
    }

    private fun open(key: ByteArray, id: String, blob: ByteArray): ByteArray {
        val out = ByteArrayOutputStream()
        ItemCrypto.openStream(key, id, ByteArrayInputStream(blob), out)
        return out.toByteArray()
    }

    private fun sha256(data: ByteArray) = MessageDigest.getInstance("SHA-256").digest(data).joinToString("") { "%02x".format(it) }

    @Test
    fun theVectorsDecrypt() {
        val vectors = JSONObject(String(read("vectors.json"), Charsets.UTF_8))
        val key = vectors.getString("key_hex").chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        assertEquals(ItemCrypto.MESSAGE_BYTES, vectors.getInt("message_bytes"))
        val items = vectors.getJSONArray("items")
        for (i in 0 until items.length()) {
            val v = items.getJSONObject(i)
            val meta = ItemCrypto.openMeta(key, v.getString("id"), read(v.getString("meta_file")))
            assertEquals(v.getJSONObject("meta").toString(), JSONObject(meta.toString()).toString())
            val plain = open(key, v.getString("id"), read(v.getString("data_file")))
            assertEquals(v.getLong("plain_size"), plain.size.toLong())
            assertEquals(v.getString("plain_sha256"), sha256(plain))
            if (v.has("plain_text")) assertEquals(v.getString("plain_text"), String(plain, Charsets.UTF_8))
        }
    }

    @Test
    fun theVectorChangesAreRefused() {
        val vectors = JSONObject(String(read("vectors.json"), Charsets.UTF_8))
        val key = vectors.getString("key_hex").chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        val items = (0 until vectors.getJSONArray("items").length()).map { vectors.getJSONArray("items").getJSONObject(it) }
            .associateBy { it.getString("name") }
        val cases = vectors.getJSONArray("must_fail")
        for (i in 0 until cases.length()) {
            val case = cases.getJSONObject(i)
            val v = items.getValue(case.getString("item"))
            var blob = read(v.getString(case.getString("file") + "_file"))
            var id = v.getString("id")
            when (case.getString("change")) {
                "truncate" -> blob = blob.copyOf(case.getInt("length"))
                "flip" -> blob = blob.copyOf().also { it[case.getInt("offset")] = (it[case.getInt("offset")].toInt() xor 1).toByte() }
                "append" -> blob += byteArrayOf(case.getInt("byte").toByte())
                "item_id" -> id = case.getString("id")
                "swap" -> blob = read(v.getString(case.getString("with") + "_file"))
            }
            assertThrows(case.toString(), DecryptException::class.java) {
                if (case.getString("file") == "meta") ItemCrypto.openMeta(key, id, blob) else open(key, id, blob)
            }
        }
    }

    @Test
    fun streamsRoundTripAtTheMessageEdges() {
        val m = ItemCrypto.MESSAGE_BYTES
        val key = ItemCrypto.newKey()
        for (size in listOf(0, 1, 1000, m - 1, m, m + 1, 3 * m + 5)) {
            val plain = Random.nextBytes(size)
            val blob = seal(key, item, plain)
            assertEquals(ItemCrypto.sealedSize(size.toLong()), blob.size.toLong())
            assertArrayEquals(plain, open(key, item, blob))
        }
    }

    @Test
    fun damagedStreamsAreRefused() {
        val m = ItemCrypto.MESSAGE_BYTES
        val key = ItemCrypto.newKey()
        val blob = seal(key, item, Random.nextBytes(2 * m + 10))
        val oneMessage = ItemCrypto.HEADER_BYTES + m + ItemCrypto.A_BYTES
        for (bad in listOf(blob.copyOf(oneMessage), blob.copyOf(blob.size - 1), blob.copyOf(5), blob + byteArrayOf(0))) {
            assertThrows(DecryptException::class.java) { open(key, item, bad) }
        }
        assertThrows(DecryptException::class.java) { open(ItemCrypto.newKey(), item, blob) }
        assertThrows(DecryptException::class.java) { open(key, "5a1ecfb3-7f98-4fae-a2b3-de6b8f6c7d06", blob) }
    }

    @Test
    fun metaRoundTripsAndIsBoundToItsItem() {
        val key = ItemCrypto.newKey()
        val meta = JSONObject().put("v", 1).put("kind", "file").put("name", "\u0444\u0430\u0439\u043b.txt").put("size", 3)
        val blob = ItemCrypto.sealMeta(key, item, meta)
        assertEquals(meta.toString(), ItemCrypto.openMeta(key, item, blob).toString())
        assertThrows(DecryptException::class.java) { ItemCrypto.openMeta(key, "5a1ecfb3-7f98-4fae-a2b3-de6b8f6c7d06", blob) }
    }
}
