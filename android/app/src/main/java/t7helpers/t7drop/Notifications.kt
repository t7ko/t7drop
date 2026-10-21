package t7helpers.t7drop

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import t7helpers.t7drop.data.Received
import t7helpers.t7drop.ui.MainActivity

object Notifications {
    const val TRANSFER_ID = 1
    private const val CHANNEL_TRANSFERS = "transfers"
    private const val CHANNEL_RECEIVED = "received"

    /** Whether a screen of the app is open: then received items need no notification. */
    @Volatile
    var appVisible = false

    fun ensureChannels(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_TRANSFERS, "Transfers in progress", NotificationManager.IMPORTANCE_LOW),
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_RECEIVED, "Received items", NotificationManager.IMPORTANCE_DEFAULT),
        )
    }

    private fun openApp(context: Context): PendingIntent = PendingIntent.getActivity(
        context, 0,
        Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    fun transferring(context: Context): Notification {
        ensureChannels(context)
        return NotificationCompat.Builder(context, CHANNEL_TRANSFERS)
            .setSmallIcon(R.drawable.ic_stat_t7drop)
            .setContentTitle("t7drop")
            .setContentText("Transferring…")
            .setOngoing(true)
            .setSilent(true)
            .setContentIntent(openApp(context))
            .build()
    }

    fun received(context: Context, item: Received) {
        if (appVisible) return
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        ensureChannels(context)
        val title = if (item.kind == "text") "Text from ${item.from}" else "${item.name} from ${item.from}"
        val text = if (item.kind == "text") item.text.orEmpty().take(200) else "Saved to Downloads/t7drop"
        val notification = NotificationCompat.Builder(context, CHANNEL_RECEIVED)
            .setSmallIcon(R.drawable.ic_stat_t7drop)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setAutoCancel(true)
            .setContentIntent(openApp(context))
            .build()
        NotificationManagerCompat.from(context).notify(item.id.hashCode(), notification)
    }
}
