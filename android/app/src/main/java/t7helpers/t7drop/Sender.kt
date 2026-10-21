package t7helpers.t7drop

import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import androidx.core.content.IntentCompat
import t7helpers.t7drop.data.Store
import t7helpers.t7drop.transfer.Scheduler
import t7helpers.t7drop.transfer.Transfers
import t7helpers.t7drop.net.Api

/** What another app shared, or the user picked: files, and perhaps a text. */
class Shared(val uris: List<Uri>, val text: String?) {
    val isEmpty: Boolean get() = uris.isEmpty() && text.isNullOrBlank()

    companion object {
        fun fromIntent(intent: Intent): Shared {
            val uris = mutableListOf<Uri>()
            when (intent.action) {
                Intent.ACTION_SEND -> IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java)?.let { uris.add(it) }
                Intent.ACTION_SEND_MULTIPLE ->
                    IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Uri::class.java)?.let { uris.addAll(it) }
            }
            if (uris.isEmpty()) {
                intent.clipData?.let { clip -> (0 until clip.itemCount).mapNotNullTo(uris) { clip.getItemAt(it).uri } }
            }
            val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
            val subject = intent.getStringExtra(Intent.EXTRA_SUBJECT)
            val fullText = if (text != null && !subject.isNullOrBlank() && !text.contains(subject)) "$subject\n$text" else text
            return Shared(uris, fullText)
        }
    }
}

/** Encrypts and queues items for the other device, then starts a transfer. */
object Sender {
    /**
     * The device to send to: with two devices, the other one. With more, the first
     * device (the one that invites the others), else the first in the list.
     */
    fun recipient(store: Store): String? {
        val me = store.config()?.deviceId ?: return null
        val others = store.devices().filter { it.id != me }
        return (others.firstOrNull { it.canInvite } ?: others.firstOrNull())?.id
    }

    /** Blocking. Returns a message for the user. */
    fun queue(context: Context, shared: Shared, progress: (String) -> Unit): String {
        val store = Store.get(context)
        val config = store.config() ?: return "Pair t7drop first."
        if (recipient(store) == null) {
            progress("Asking the server for the devices…")
            try {
                store.saveDevices(Api(config.url, config.token).devices())
            } catch (e: Exception) {
                return "Cannot send yet: the server has not told this phone about the other device. Open t7drop while online."
            }
        }
        val to = recipient(store) ?: return "There is no other device to send to. Pair the laptop first."
        val transfers = Transfers(context, store)
        var count = 0
        val problems = mutableListOf<String>()
        for ((i, uri) in shared.uris.withIndex()) {
            val name = displayName(context.contentResolver, uri) ?: "file"
            progress("Encrypting $name" + if (shared.uris.size > 1) " (${i + 1} of ${shared.uris.size})…" else "…")
            try {
                val mime = context.contentResolver.getType(uri) ?: "application/octet-stream"
                val input = context.contentResolver.openInputStream(uri) ?: throw java.io.IOException("cannot read it")
                input.use { transfers.queue(it.buffered(), "file", name, mime, to) }
                count++
            } catch (e: Exception) {
                problems.add("$name: ${e.message}")
            }
        }
        val text = shared.text
        if (!text.isNullOrBlank()) {
            transfers.queue(text.byteInputStream(), "text", null, "text/plain; charset=utf-8", to, text.take(300))
            count++
        }
        Scheduler.kick(context)
        val who = store.deviceName(to)
        val sent = when (count) {
            0 -> "Nothing was sent."
            1 -> "Sending to $who."
            else -> "Sending $count items to $who."
        }
        return if (problems.isEmpty()) sent else sent + " Not sent: " + problems.joinToString("; ")
    }

    private fun displayName(resolver: ContentResolver, uri: Uri): String? = try {
        resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) cursor.getString(0) else null
        }
    } catch (e: Exception) {
        null
    } ?: uri.lastPathSegment?.substringAfterLast('/')
}
