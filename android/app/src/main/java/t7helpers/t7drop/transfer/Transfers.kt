package t7helpers.t7drop.transfer

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import org.json.JSONArray
import org.json.JSONObject
import t7helpers.t7drop.Protocol
import t7helpers.t7drop.crypto.DecryptException
import t7helpers.t7drop.crypto.ItemCrypto
import t7helpers.t7drop.data.OutItem
import t7helpers.t7drop.data.OutState
import t7helpers.t7drop.data.Received
import t7helpers.t7drop.data.Store
import t7helpers.t7drop.files.Names
import t7helpers.t7drop.net.Api
import t7helpers.t7drop.net.ApiException
import t7helpers.t7drop.net.TransientException
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FilterOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.io.RandomAccessFile
import java.security.DigestOutputStream
import java.security.MessageDigest
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

class TooLargeException(message: String) : IOException(message)

/**
 * What a client does (docs/design.md, "Reliability"), as the CLI's agent does it: encrypts
 * and queues items; uploads them, resuming by chunks; receives, saves and acknowledges.
 * The item id is made when the item is queued, so retries never create a second item;
 * the ciphertext stays until the recipient acknowledges; an item the server lost (a
 * restart) is uploaded again; received ids are remembered, so nothing is saved twice.
 * Blocking: run it off the main thread.
 */
class Transfers(
    private val context: Context,
    private val store: Store,
    /** Called before an upload or download of more than a megabyte: a chance to go foreground. */
    private val beforeBigTransfer: () -> Unit = {},
    private val onReceived: (Received) -> Unit = {},
) {
    private val config = store.config() ?: throw IllegalStateException("not paired")
    private val api = Api(config.url, config.token)

    // Queueing

    fun queue(input: InputStream, kind: String, name: String?, mime: String, to: String, preview: String? = null): OutItem {
        val id = UUID.randomUUID().toString()
        val folder = store.newOutFolder(id)
        try {
            val digest = MessageDigest.getInstance("SHA-256")
            val counter = CountingStream(DigestOutputStream(File(folder, "data.bin").outputStream().buffered(), digest))
            val size = counter.use { ItemCrypto.sealStream(config.key, id, input, it) }
            if (ItemCrypto.sealedSize(size) > Protocol.MAX_ITEM_BYTES) {
                throw TooLargeException("${name ?: "The text"} is larger than the server takes (200 MB).")
            }
            val now = System.currentTimeMillis()
            val meta = JSONObject().put("v", 1).put("kind", kind).put("mime", mime).put("size", size)
                .put("from", config.deviceId).put("to", to).put("created", now)
            if (name != null) meta.put("name", name)
            File(folder, "meta.bin").writeBytes(ItemCrypto.sealMeta(config.key, id, meta))
            val json = JSONObject().put("id", id).put("to", to).put("kind", kind)
                .put("name", name ?: JSONObject.NULL).put("preview", preview ?: JSONObject.NULL)
                .put("size", size).put("csize", counter.count).put("sha256", hex(digest.digest()))
                .put("created", now).put("state", OutState.QUEUED).put("error", JSONObject.NULL).put("sent", 0)
            return OutItem.create(folder, json).also { store.touch() }
        } catch (e: Throwable) {
            folder.deleteRecursively()
            throw e
        }
    }

    // Both directions

    /** Sends what is queued and receives what is waiting; throws TransientException to retry later. */
    fun runOnce() {
        store.dropBrokenOutFolders(System.currentTimeMillis())
        sendPending()
        sync()
        sendPending()
        refreshDevices()
    }

    fun refreshDevices() {
        try {
            store.saveDevices(api.devices())
        } catch (e: ApiException) {
            // not worth failing a transfer over
        }
    }

    // Sending

    fun sendPending() {
        for (item in store.outbox()) {
            if (item.state != OutState.QUEUED && item.state != OutState.UPLOADING) continue
            if (System.currentTimeMillis() - item.created > Protocol.GIVE_UP_MILLIS) {
                item.setState(OutState.FAILED, "not delivered in 7 days")
                store.touch()
                continue
            }
            upload(item)
        }
    }

    private fun upload(item: OutItem) {
        val meta = item.metaFile.readBytes()
        val inline = if (item.csize <= Protocol.INLINE_MAX) item.dataFile.readBytes() else null
        if (item.csize > 1_000_000) beforeBigTransfer()
        try {
            var view = api.createItem(item.id, item.to, item.csize, item.sha256, meta, inline)
            if (view.getString("state") == "uploading") {
                if (item.state != OutState.UPLOADING) item.setState(OutState.UPLOADING)
                val have = view.getJSONArray("have").let { a -> (0 until a.length()).map { a.getInt(it) }.toMutableSet() }
                var done = have.sumOf { chunkLength(item.csize, it) }
                item.setSentBytes(done)
                store.touch()
                RandomAccessFile(item.dataFile, "r").use { file ->
                    for (n in 0 until Protocol.chunkCount(item.csize)) {
                        if (n in have) continue
                        val offset = n.toLong() * Protocol.CHUNK_SIZE
                        val chunk = ByteArray(minOf(Protocol.CHUNK_SIZE.toLong(), item.csize - offset).toInt())
                        file.seek(offset)
                        file.readFully(chunk)
                        var reported = 0
                        api.putChunk(item.id, n, chunk) { sent ->
                            if (sent - reported >= REPORT_BYTES) {
                                reported = sent
                                item.setSentBytes(done + sent)
                                store.touch()
                            }
                        }
                        have.add(n)
                        done += chunk.size
                        item.setSentBytes(done)
                        store.touch()
                    }
                }
                view = api.commit(item.id)
            }
            applyState(item, view.getString("state"))
        } catch (e: ApiException) {
            if (e.transient) throw TransientException(e.message ?: e.code, e)
            if (e.code in CHANGED_UNDER_US) return // a server restart, or another upload of it: next round
            item.setState(OutState.FAILED, describe(e))
            store.touch()
        }
    }

    private fun applyState(item: OutItem, state: String) {
        if (state == "delivered" && item.state != OutState.DELIVERED) {
            item.setState(OutState.DELIVERED)
            item.dropPayload()
            store.touch()
        } else if (state == "ready" && item.state != OutState.ON_SERVER && item.state != OutState.DELIVERED) {
            item.setState(OutState.ON_SERVER)
            store.touch()
        }
    }

    // Receiving

    fun sync() {
        val snapshot = api.sync(-1)
        applyOutbox(snapshot.getJSONArray("outbox"))
        val inbox = snapshot.getJSONArray("inbox")
        for (i in 0 until inbox.length()) receive(inbox.getJSONObject(i))
    }

    private fun applyOutbox(entries: JSONArray) {
        val onServer = (0 until entries.length()).map { entries.getJSONObject(it) }.associateBy { it.getString("id") }
        for (item in store.outbox()) {
            if (item.state == OutState.DELIVERED || item.state == OutState.FAILED) continue
            val entry = onServer[item.id]
            if (entry != null) {
                applyState(item, entry.getString("state"))
            } else if (item.state == OutState.ON_SERVER) {
                item.setState(OutState.QUEUED) // the server lost it (a restart): upload it again
                store.touch()
            }
        }
    }

    private fun receive(entry: JSONObject) {
        val id = entry.getString("id")
        if (!Protocol.isItemId(id) || id in refused) return
        if (store.wasReceived(id)) {
            ack(id)
            return
        }
        val meta = try {
            ItemCrypto.openMeta(config.key, id, Base64.getDecoder().decode(entry.getString("meta")))
        } catch (e: Exception) {
            refused.add(id) // another key: not ours to read
            return
        }
        if (meta.optString("to") != config.deviceId) {
            refused.add(id)
            return
        }
        if (entry.getLong("size") > 1_000_000) beforeBigTransfer()
        val label = if (meta.optString("kind") == "text") "a text" else Names.safe(meta.optString("name"))
        val part = try {
            download(entry) { percent -> store.receiving.value = "Receiving $label: $percent%" }
        } finally {
            store.receiving.value = null
        }
        val from = store.deviceName(meta.optString("from", entry.optString("from")))
        val received = try {
            if (meta.optString("kind") == "text") {
                val text = ByteArrayOutputStream()
                part.inputStream().buffered().use { ItemCrypto.openStream(config.key, id, it, text) }
                Received(id, "text", null, "text/plain", text.size().toLong(), text.toString("UTF-8"), null, from, System.currentTimeMillis())
            } else {
                val name = Names.safe(meta.optString("name"))
                val mime = meta.optString("mime").ifEmpty { "application/octet-stream" }
                var size = 0L
                val uri = saveToDownloads(name, mime) { out ->
                    part.inputStream().buffered().use { size = ItemCrypto.openStream(config.key, id, it, out) }
                }
                Received(id, "file", name, mime, size, null, uri.toString(), from, System.currentTimeMillis())
            }
        } catch (e: DecryptException) {
            refused.add(id)
            return
        } finally {
            part.delete()
        }
        store.addReceived(received)
        onReceived(received)
        ack(id)
    }

    /** Downloads the ciphertext into incoming/, resuming a partial download; returns the file. */
    private fun download(entry: JSONObject, onProgress: (Int) -> Unit): File {
        val id = entry.getString("id")
        store.incomingDir.mkdirs()
        val part = File(store.incomingDir, "$id.part")
        val chunks = Protocol.chunkCount(entry.getLong("size"))
        val done = if (part.exists()) (part.length() / Protocol.CHUNK_SIZE).toInt() else 0
        RandomAccessFile(part, "rw").use { file ->
            file.setLength(done.toLong() * Protocol.CHUNK_SIZE)
            file.seek(done.toLong() * Protocol.CHUNK_SIZE)
            for (n in done until chunks) {
                onProgress(n * 100 / chunks)
                val data = try {
                    api.getChunk(id, n)
                } catch (e: ApiException) {
                    throw TransientException(e.message ?: e.code, e)
                }
                file.write(data)
            }
        }
        val digest = MessageDigest.getInstance("SHA-256")
        part.inputStream().buffered().use { input ->
            val buffer = ByteArray(1 shl 20)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
        }
        if (hex(digest.digest()) != entry.getString("sha256")) {
            part.delete()
            throw TransientException("the download of $id does not match its hash")
        }
        return part
    }

    /** Saves into Downloads/t7drop; the file shows only once it is complete. */
    private fun saveToDownloads(name: String, mime: String, write: (OutputStream) -> Unit): Uri {
        val resolver = context.contentResolver
        val values = ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, name)
            put(MediaStore.Downloads.MIME_TYPE, mime)
            put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/t7drop")
            put(MediaStore.Downloads.IS_PENDING, 1)
        }
        val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: throw IOException("Android did not create a file in Downloads")
        try {
            (resolver.openOutputStream(uri) ?: throw IOException("cannot write $uri")).use { write(it) }
            values.clear()
            values.put(MediaStore.Downloads.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
            return uri
        } catch (e: Throwable) {
            resolver.delete(uri, null, null)
            throw e
        }
    }

    private fun ack(id: String) {
        try {
            api.ack(id)
        } catch (e: ApiException) {
            if (e.code != "unknown_item") throw TransientException(e.message ?: e.code, e)
        }
    }

    private class CountingStream(out: OutputStream) : FilterOutputStream(out) {
        var count = 0L

        override fun write(b: ByteArray, off: Int, len: Int) {
            out.write(b, off, len)
            count += len
        }

        override fun write(b: Int) {
            out.write(b)
            count++
        }
    }

    companion object {
        private val CHANGED_UNDER_US = setOf("unknown_item", "not_uploading", "missing_chunks", "hash_mismatch")
        private const val REPORT_BYTES = 512 * 1024

        private fun chunkLength(size: Long, n: Int): Long = minOf(Protocol.CHUNK_SIZE.toLong(), size - n.toLong() * Protocol.CHUNK_SIZE)

        /** Items that do not decrypt with our key: not downloaded again while the app runs. */
        private val refused: MutableSet<String> = ConcurrentHashMap.newKeySet()

        private fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it) }

        fun describe(e: ApiException): String = when (e.code) {
            "too_large" -> "too large for the server"
            "bad_recipient" -> "the recipient is no longer paired"
            "storage_full" -> "the server is full"
            else -> "refused by the server (${e.code})"
        }
    }
}
