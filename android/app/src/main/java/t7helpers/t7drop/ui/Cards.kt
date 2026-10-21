package t7helpers.t7drop.ui

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat

private fun prefs(context: Context) = context.getSharedPreferences("ui", Context.MODE_PRIVATE)

@Composable
private fun InfoCard(title: String, lines: List<String>, buttons: @Composable () -> Unit) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.tertiaryContainer)) {
        Column(Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 12.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onTertiaryContainer)
            lines.forEach {
                Text(
                    "• $it",
                    Modifier.padding(top = 6.dp, end = 8.dp),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onTertiaryContainer,
                )
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) { buttons() }
        }
    }
}

/** Android 13+: without the permission, nothing tells that an item arrived while t7drop was closed. */
@Composable
fun NotificationCard(onAllow: () -> Unit) {
    val context = LocalContext.current
    var dismissed by remember { mutableStateOf(prefs(context).getBoolean("notifications_dismissed", false)) }
    val granted = Build.VERSION.SDK_INT < 33 ||
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
    if (granted || dismissed) return
    InfoCard("Notifications", listOf("Allow them to see when something arrives while t7drop is closed.")) {
        TextButton(onClick = onAllow) { Text("Allow") }
        TextButton(onClick = {
            prefs(context).edit().putBoolean("notifications_dismissed", true).apply()
            dismissed = true
        }) { Text("Not now") }
    }
}

/**
 * On Xiaomi phones only: MIUI and HyperOS settings that apps cannot read. Without them,
 * transfers in the background may wait until t7drop is opened. Stays until Done.
 */
@Composable
fun XiaomiCard() {
    val context = LocalContext.current
    var done by remember { mutableStateOf(prefs(context).getBoolean("xiaomi_done", false)) }
    if (!Build.MANUFACTURER.equals("Xiaomi", ignoreCase = true) || done) return
    InfoCard(
        "Xiaomi phone: check these once",
        listOf(
            "App info → Autostart: on. Without it, transfers wait until you open t7drop.",
            "App info → Battery saver: No restrictions. Without it, a large transfer may stop in the background.",
        ),
    ) {
        TextButton(onClick = {
            val info = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + context.packageName))
            try {
                context.startActivity(info)
            } catch (e: ActivityNotFoundException) {
                // nothing to open
            }
        }) { Text("App info") }
        TextButton(onClick = {
            prefs(context).edit().putBoolean("xiaomi_done", true).apply()
            done = true
        }) { Text("Done") }
    }
}
