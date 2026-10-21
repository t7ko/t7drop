package t7helpers.t7drop.ui

import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.text.format.DateUtils
import android.text.format.Formatter
import android.util.Patterns
import android.widget.Toast
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch
import t7helpers.t7drop.AppScope
import t7helpers.t7drop.data.OutItem
import t7helpers.t7drop.data.OutState
import t7helpers.t7drop.data.Received
import t7helpers.t7drop.data.Store
import t7helpers.t7drop.net.Api
import t7helpers.t7drop.transfer.Scheduler

private const val MAX_DELIVERED_SHOWN = 20

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(
    store: Store,
    tick: Int,
    busy: String?,
    onSendFiles: () -> Unit,
    onSendText: (String) -> Unit,
    onAskNotifications: () -> Unit,
    onRefresh: () -> Unit,
) {
    val context = LocalContext.current
    val problem by store.problem.collectAsState()
    val receiving by store.receiving.collectAsState()
    val outbox = remember(tick) { store.outbox().reversed() }
    val history = remember(tick) { store.history() }
    var menu by remember { mutableStateOf(false) }
    var writing by remember { mutableStateOf(false) }
    var about by remember { mutableStateOf(false) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("t7drop") },
                actions = {
                    IconButton(onClick = onRefresh) { Icon(Icons.Default.Refresh, contentDescription = "Check now") }
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = "More") }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text("Clear finished items") }, onClick = {
                            menu = false
                            store.outbox().filter { it.state == OutState.DELIVERED || it.state == OutState.FAILED }.forEach { store.remove(it) }
                        })
                        DropdownMenuItem(text = { Text("This phone and the server") }, onClick = {
                            menu = false
                            about = true
                        })
                    }
                },
            )
        },
    ) { padding ->
        LazyColumn(
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = padding.calculateTopPadding() + 8.dp, bottom = padding.calculateBottomPadding() + 16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            problem?.let { message ->
                item { Text(message, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium) }
            }
            item {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    NotificationCard(onAskNotifications)
                    XiaomiCard()
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Button(onClick = onSendFiles, enabled = busy == null) { Text("Send files") }
                        OutlinedButton(onClick = { writing = true }, enabled = busy == null) { Text("Send text") }
                    }
                }
            }
            listOfNotNull(busy, receiving).forEach { message ->
                item {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                        Text(message, style = MaterialTheme.typography.bodyMedium)
                    }
                }
            }
            val pending = outbox.filter { it.state != OutState.DELIVERED }
            val delivered = outbox.filter { it.state == OutState.DELIVERED }.take(MAX_DELIVERED_SHOWN)
            if (pending.isNotEmpty()) {
                item { Heading("Sending") }
                items(pending, key = { "out-" + it.id }) { SentCard(store, it) }
            }
            item { Heading("Received") }
            if (history.isEmpty()) {
                item { Text("Nothing yet. What the laptop sends appears here; files go to Downloads/t7drop.") }
            }
            items(history, key = { "in-" + it.id }) { ReceivedCard(store, it) }
            if (delivered.isNotEmpty()) {
                item { Heading("Sent") }
                items(delivered, key = { "out-" + it.id }) { SentCard(store, it) }
            }
        }
    }

    if (writing) TextDialog(onDismiss = { writing = false }, onSend = {
        writing = false
        onSendText(it)
    })
    if (about) AboutDialog(store, onDismiss = { about = false })
}

@Composable
private fun Heading(text: String) {
    Text(text, style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 8.dp))
}

@Composable
private fun SentCard(store: Store, item: OutItem) {
    val context = LocalContext.current
    val state = when (item.state) {
        OutState.QUEUED -> "Waiting to send"
        OutState.UPLOADING -> "Sending: ${item.sentBytes * 100 / maxOf(1L, item.csize)}%"
        OutState.ON_SERVER -> "On the server, waiting for ${store.deviceName(item.to)}"
        OutState.DELIVERED -> "Delivered"
        else -> "Not sent: ${item.error ?: "failed"}"
    }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(start = 12.dp, end = 4.dp, top = 10.dp)) {
            Text(item.label, style = MaterialTheme.typography.bodyLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Text(
                "$state · ${Formatter.formatShortFileSize(context, item.size)} · ${time(context, item.created)}",
                style = MaterialTheme.typography.bodySmall,
                color = if (item.state == OutState.FAILED) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                if (item.state == OutState.FAILED && item.dataFile.exists()) {
                    TextButton(onClick = {
                        item.setState(OutState.QUEUED)
                        store.touch()
                        AppScope.launch { Scheduler.kick(context.applicationContext) }
                    }) { Text("Try again") }
                }
                TextButton(onClick = { cancel(context, store, item) }) {
                    Text(if (item.state == OutState.DELIVERED || item.state == OutState.FAILED) "Remove" else "Cancel")
                }
            }
        }
    }
}

/** Takes an item off the list; one not delivered is also taken off the server. */
private fun cancel(context: Context, store: Store, item: OutItem) {
    val pending = item.state != OutState.DELIVERED && item.state != OutState.FAILED
    store.remove(item)
    val config = store.config() ?: return
    if (pending) {
        AppScope.launch {
            try {
                Api(config.url, config.token).delete(item.id)
            } catch (e: Exception) {
                // already gone, or the server is unreachable: it expires there
            }
        }
    }
}

@Composable
private fun ReceivedCard(store: Store, item: Received) {
    val context = LocalContext.current
    Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer)) {
        Column(Modifier.padding(start = 12.dp, end = 4.dp, top = 10.dp)) {
            if (item.kind == "text") {
                SelectionContainer {
                    Text(item.text.orEmpty(), style = MaterialTheme.typography.bodyLarge, maxLines = 12, overflow = TextOverflow.Ellipsis)
                }
            } else {
                Text(item.name ?: "file", style = MaterialTheme.typography.bodyLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            Text(
                "From ${item.from} · ${time(context, item.time)}" +
                    if (item.kind == "text") "" else " · " + Formatter.formatShortFileSize(context, item.size),
                style = MaterialTheme.typography.bodySmall,
            )
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                if (item.kind == "text") {
                    val link = firstLink(item.text.orEmpty())
                    TextButton(onClick = { copy(context, item.text.orEmpty()) }) { Text("Copy") }
                    if (link != null) TextButton(onClick = { open(context, Intent(Intent.ACTION_VIEW, Uri.parse(link))) }) { Text("Open link") }
                    TextButton(onClick = {
                        open(context, Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, item.text), null))
                    }) { Text("Share") }
                } else if (item.uri != null) {
                    val uri = Uri.parse(item.uri)
                    TextButton(onClick = {
                        open(context, Intent(Intent.ACTION_VIEW).setDataAndType(uri, item.mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))
                    }) { Text("Open") }
                    TextButton(onClick = {
                        val send = Intent(Intent.ACTION_SEND).setType(item.mime).putExtra(Intent.EXTRA_STREAM, uri)
                            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                        open(context, Intent.createChooser(send, null))
                    }) { Text("Share") }
                }
                TextButton(onClick = { store.removeReceived(item.id) }) { Text("Remove") }
            }
        }
    }
}

@Composable
private fun TextDialog(onDismiss: () -> Unit, onSend: (String) -> Unit) {
    var text by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Send a text") },
        text = { OutlinedTextField(value = text, onValueChange = { text = it }, minLines = 3, modifier = Modifier.fillMaxWidth()) },
        confirmButton = { TextButton(onClick = { onSend(text) }, enabled = text.isNotBlank()) { Text("Send") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@Composable
private fun AboutDialog(store: Store, onDismiss: () -> Unit) {
    val config = store.config() ?: return
    var unpairing by remember { mutableStateOf(false) }
    val devices = remember { store.devices() }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (unpairing) "Unpair this phone?" else "This phone") },
        text = {
            if (unpairing) {
                Text(
                    "The phone forgets the pairing, its queue and its lists; files stay in Downloads. " +
                        "To use t7drop again, scan a new invitation. To shut this phone out of the " +
                        "server for good, also revoke it there.",
                )
            } else {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text("Name: ${config.deviceName} (${config.deviceId})")
                    Text("Server: ${Uri.parse(config.url).host}")
                    Text("Paired devices:")
                    devices.forEach { d ->
                        val notes = listOfNotNull(
                            "this phone".takeIf { d.id == config.deviceId },
                            "invites devices".takeIf { d.canInvite },
                        )
                        Text("• ${d.name}" + if (notes.isEmpty()) "" else " (" + notes.joinToString(", ") + ")")
                    }
                }
            }
        },
        confirmButton = {
            if (unpairing) {
                TextButton(onClick = {
                    store.forget()
                    onDismiss()
                }) { Text("Unpair") }
            } else {
                TextButton(onClick = onDismiss) { Text("Close") }
            }
        },
        dismissButton = {
            if (unpairing) TextButton(onClick = { unpairing = false }) { Text("Cancel") } else TextButton(onClick = { unpairing = true }) { Text("Unpair…") }
        },
    )
}

private fun time(context: Context, millis: Long): String =
    DateUtils.formatDateTime(context, millis, DateUtils.FORMAT_SHOW_TIME or DateUtils.FORMAT_SHOW_DATE or DateUtils.FORMAT_ABBREV_MONTH)

private fun firstLink(text: String): String? {
    val match = Patterns.WEB_URL.matcher(text)
    if (!match.find()) return null
    val link = match.group()
    return if (link.startsWith("http://", true) || link.startsWith("https://", true)) link else "https://$link"
}

private fun copy(context: Context, text: String) {
    context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("t7drop", text))
    Toast.makeText(context, "Copied", Toast.LENGTH_SHORT).show()
}

private fun open(context: Context, intent: Intent) {
    try {
        context.startActivity(intent)
    } catch (e: ActivityNotFoundException) {
        Toast.makeText(context, "No app on this phone opens it.", Toast.LENGTH_LONG).show()
    }
}
