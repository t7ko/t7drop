package t7helpers.t7drop.data

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.update
import org.json.JSONArray
import org.json.JSONObject
import t7helpers.t7drop.Pairing
import java.io.File

/** The pairing: the server URL, this device, its token and the group key. */
class Config(val url: String, val deviceId: String, val deviceName: String, val token: String, val key: ByteArray) {
    fun toJson(): JSONObject = JSONObject().put("url", url).put("device_id", deviceId)
        .put("device_name", deviceName).put("token", token).put("key", Pairing.b64url(key))

    companion object {
        fun fromJson(j: JSONObject) = Config(
            j.getString("url"), j.getString("device_id"), j.getString("device_name"),
            j.getString("token"), Pairing.fromB64url(j.getString("key")),
        )
    }
}

/** A paired device as the server lists it; created is Unix time in seconds. */
class Device(val id: String, val name: String, val canInvite: Boolean, val created: Long = 0)

/** A received item as the app lists it: a text, or a file saved to Downloads. */
class Received(
    val id: String,
    val kind: String,
    val name: String?,
    val mime: String?,
    val size: Long,
    val text: String?,
    val uri: String?,
    val from: String,
    val time: Long,
) {
    fun toJson(): JSONObject = JSONObject().put("id", id).put("kind", kind).put("name", name ?: JSONObject.NULL)
        .put("mime", mime ?: JSONObject.NULL).put("size", size).put("text", text ?: JSONObject.NULL)
        .put("uri", uri ?: JSONObject.NULL).put("from", from).put("time", time)

    companion object {
        fun fromJson(j: JSONObject) = Received(
            j.getString("id"), j.getString("kind"), j.optStringOrNull("name"), j.optStringOrNull("mime"),
            j.optLong("size"), j.optStringOrNull("text"), j.optStringOrNull("uri"), j.optString("from"), j.getLong("time"),
        )
    }
}

fun JSONObject.optStringOrNull(name: String): String? = if (isNull(name)) null else optString(name)

object OutState {
    const val QUEUED = "queued"
    const val UPLOADING = "uploading"
    const val ON_SERVER = "on_server"
    const val DELIVERED = "delivered"
    const val FAILED = "failed"
}

/**
 * An item to send: a folder with item.json (its state), meta.bin and data.bin (the
 * ciphertext, kept until the recipient has the item). item.json is written last, so a
 * folder without it is an item whose queueing did not finish.
 */
class OutItem(val folder: File, private val json: JSONObject) {
    val id: String get() = json.getString("id")
    val to: String get() = json.getString("to")
    val kind: String get() = json.getString("kind")
    val name: String? get() = json.optStringOrNull("name")
    val preview: String? get() = json.optStringOrNull("preview")
    val size: Long get() = json.getLong("size")
    val csize: Long get() = json.getLong("csize")
    val sha256: String get() = json.getString("sha256")
    val created: Long get() = json.getLong("created")
    val state: String get() = json.getString("state")
    val error: String? get() = json.optStringOrNull("error")

    /** Bytes the server has, about, while uploading. */
    val sentBytes: Long get() = json.optLong("sent_bytes")
    val metaFile get() = File(folder, "meta.bin")
    val dataFile get() = File(folder, "data.bin")
    val label: String get() = name ?: preview?.lineSequence()?.firstOrNull()?.take(80) ?: "Text"

    fun setState(state: String, error: String? = null) {
        json.put("state", state).put("error", error ?: JSONObject.NULL)
        save()
    }

    fun setSentBytes(bytes: Long) {
        json.put("sent_bytes", bytes)
        save()
    }

    fun save() = Store.writeAtomically(File(folder, "item.json"), json.toString().toByteArray())

    fun dropPayload() {
        metaFile.delete()
        dataFile.delete()
    }

    companion object {
        fun load(folder: File): OutItem? = try {
            OutItem(folder, JSONObject(File(folder, "item.json").readText()))
        } catch (e: Exception) {
            null
        }

        fun create(folder: File, json: JSONObject): OutItem = OutItem(folder, json).also { it.save() }
    }
}

/** All that the app keeps, in its private storage. One instance per process. */
class Store private constructor(context: Context) {
    private val dir = context.applicationContext.filesDir
    val outboxDir = File(dir, "outbox")
    val incomingDir = File(dir, "incoming")
    private val configFile = File(dir, "config.bin")
    private val devicesFile = File(dir, "devices.json")
    private val historyFile = File(dir, "history.json")
    private val receivedFile = File(dir, "received.json")

    /** Ids of items refused here (another group's key, another device's, damaged), for 8 days. */
    private val refusedFile = File(dir, "refused.json")
    private val lock = Any()

    /** Raised on every change, so that the screens update. */
    val changes = MutableStateFlow(0)

    /** The connection as the screen shows it; null while all is well. */
    val problem = MutableStateFlow<String?>(null)

    /** What is being downloaded now, for the screen; null when nothing is. */
    val receiving = MutableStateFlow<String?>(null)

    fun touch() = changes.update { it + 1 }

    @Volatile
    private var config: Config? = null

    fun config(): Config? {
        config?.let { return it }
        if (!configFile.exists()) return null
        return try {
            Config.fromJson(JSONObject(String(Secrets.open(configFile.readBytes()), Charsets.UTF_8))).also { config = it }
        } catch (e: Exception) {
            null // the Keystore key is gone: pair again
        }
    }

    fun saveConfig(value: Config) {
        writeAtomically(configFile, Secrets.seal(value.toJson().toString().toByteArray()))
        config = value
        touch()
    }

    /** Unpairs: forgets the pairing, the queue and the lists. Saved files stay in Downloads. */
    fun forget() {
        synchronized(lock) {
            listOf(configFile, devicesFile, historyFile, receivedFile, refusedFile).forEach { it.delete() }
            outboxDir.deleteRecursively()
            incomingDir.deleteRecursively()
            config = null
        }
        problem.value = null
        touch()
    }

    fun devices(): List<Device> = readArray(devicesFile).map {
        Device(it.getString("id"), it.getString("name"), it.optBoolean("can_invite"), it.optLong("created"))
    }

    fun saveDevices(devices: JSONArray) {
        writeAtomically(devicesFile, devices.toString().toByteArray())
        touch()
    }

    fun deviceName(id: String): String = devices().firstOrNull { it.id == id }?.name ?: id

    fun newOutFolder(id: String): File = File(outboxDir, id).also { it.mkdirs() }

    fun outbox(): List<OutItem> =
        (outboxDir.listFiles() ?: emptyArray()).mapNotNull { OutItem.load(it) }.sortedBy { it.created }

    /** Folders whose queueing never finished (no item.json), older than an hour. */
    fun dropBrokenOutFolders(now: Long) {
        (outboxDir.listFiles() ?: emptyArray())
            .filter { !File(it, "item.json").exists() && now - it.lastModified() > 3_600_000 }
            .forEach { it.deleteRecursively() }
    }

    /** Delivered items 30 days after their delivery (the last write of item.json), as on Windows. */
    fun dropOldDeliveries(now: Long) {
        val gone = outbox().filter { it.state == OutState.DELIVERED && now - File(it.folder, "item.json").lastModified() > KEEP_DELIVERED_MILLIS }
        gone.forEach { it.folder.deleteRecursively() }
        if (gone.isNotEmpty()) touch()
    }

    /**
     * Partial downloads of items no longer [waiting]: cancelled, expired, or lost in a server
     * restart, after which they may come again; so only once nothing has written them for a day.
     */
    fun dropStaleParts(waiting: Set<String>, now: Long) {
        (incomingDir.listFiles() ?: emptyArray())
            .filter { !(it.name.endsWith(".part") && it.name.removeSuffix(".part") in waiting) }
            .filter { now - it.lastModified() > STALE_PART_MILLIS }
            .forEach { it.delete() }
    }

    fun remove(item: OutItem) {
        item.folder.deleteRecursively()
        touch()
    }

    fun history(): List<Received> =
        synchronized(lock) { readArray(historyFile) }.map { Received.fromJson(it) }.sortedByDescending { it.time }

    fun addReceived(item: Received) {
        synchronized(lock) {
            val list = (readArray(historyFile) + item.toJson()).takeLast(MAX_HISTORY)
            writeAtomically(historyFile, JSONArray(list).toString().toByteArray())
            val received = readObject(receivedFile)
            val cutoff = item.time - KEEP_RECEIVED_MILLIS
            received.keys().asSequence().toList().filter { received.getLong(it) < cutoff }.forEach { received.remove(it) }
            received.put(item.id, item.time)
            writeAtomically(receivedFile, received.toString().toByteArray())
        }
        touch()
    }

    /** Takes an entry off the list; the item still counts as received (no second copy). */
    fun removeReceived(id: String) {
        synchronized(lock) {
            val list = readArray(historyFile).filter { it.getString("id") != id }
            writeAtomically(historyFile, JSONArray(list).toString().toByteArray())
        }
        touch()
    }

    fun wasReceived(id: String): Boolean = synchronized(lock) { readObject(receivedFile).has(id) }

    fun wasRefused(id: String): Boolean = synchronized(lock) { readObject(refusedFile).has(id) }

    /** Kept for 8 days, one more than the server keeps an item: not looked at again in a later run. */
    fun markRefused(id: String, now: Long) {
        synchronized(lock) {
            val refused = readObject(refusedFile)
            refused.keys().asSequence().toList().filter { now - refused.getLong(it) > KEEP_REFUSED_MILLIS }.forEach { refused.remove(it) }
            refused.put(id, now)
            writeAtomically(refusedFile, refused.toString().toByteArray())
        }
    }

    private fun readArray(file: File): List<JSONObject> = try {
        val array = JSONArray(file.readText())
        (0 until array.length()).map { array.getJSONObject(it) }
    } catch (e: Exception) {
        emptyList()
    }

    private fun readObject(file: File): JSONObject = try {
        JSONObject(file.readText())
    } catch (e: Exception) {
        JSONObject()
    }

    companion object {
        private const val MAX_HISTORY = 300
        private const val KEEP_RECEIVED_MILLIS = 30L * 24 * 3600 * 1000
        private const val KEEP_REFUSED_MILLIS = 8L * 24 * 3600 * 1000
        private const val KEEP_DELIVERED_MILLIS = 30L * 24 * 3600 * 1000
        private const val STALE_PART_MILLIS = 24L * 3600 * 1000

        @Volatile
        private var instance: Store? = null

        fun get(context: Context): Store =
            instance ?: synchronized(this) { instance ?: Store(context).also { instance = it } }

        fun writeAtomically(file: File, data: ByteArray) {
            file.parentFile?.mkdirs()
            val tmp = File(file.path + ".tmp")
            tmp.writeBytes(data)
            if (!tmp.renameTo(file)) throw java.io.IOException("cannot replace $file")
        }
    }
}
