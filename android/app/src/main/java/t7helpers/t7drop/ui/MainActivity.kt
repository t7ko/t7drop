package t7helpers.t7drop.ui

import android.Manifest
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withContext
import t7helpers.t7drop.AppScope
import t7helpers.t7drop.Notifications
import t7helpers.t7drop.Pairing
import t7helpers.t7drop.Sender
import t7helpers.t7drop.Shared
import t7helpers.t7drop.data.Config
import t7helpers.t7drop.data.Store
import t7helpers.t7drop.net.Api
import t7helpers.t7drop.net.ApiException
import t7helpers.t7drop.net.TransientException
import t7helpers.t7drop.transfer.Scheduler

class MainActivity : ComponentActivity() {
    private val store by lazy { Store.get(this) }
    private var invitation by mutableStateOf("")
    private var deviceName by mutableStateOf("phone")
    private var pairing by mutableStateOf(false)
    private var pairError by mutableStateOf<String?>(null)

    /** What the encryption of picked files is doing; null when idle. */
    private var busy by mutableStateOf<String?>(null)

    private val scanner = registerForActivityResult(ScanContract()) { result ->
        result.contents?.let {
            invitation = it
            pair()
        }
    }
    private val pickFiles = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isNotEmpty()) send(Shared(uris, null))
    }
    private val askNotifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { store.touch() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        Notifications.ensureChannels(this)
        deviceName = Settings.Global.getString(contentResolver, Settings.Global.DEVICE_NAME)
            ?: listOfNotNull(Build.MANUFACTURER, Build.MODEL).joinToString(" ").ifEmpty { "phone" }
        handle(intent)
        lifecycleScope.launch { repeatOnLifecycle(Lifecycle.State.RESUMED) { poll() } }
        setContent {
            T7dropTheme {
                val tick by store.changes.collectAsState()
                val paired = remember(tick) { store.config() != null }
                if (!paired) {
                    PairScreen(
                        invitation = invitation,
                        onInvitation = { invitation = it },
                        deviceName = deviceName,
                        onDeviceName = { deviceName = it },
                        busy = pairing,
                        error = pairError,
                        onScan = {
                            scanner.launch(
                                ScanOptions().setPrompt("Scan the QR code of the t7drop invitation")
                                    .setBeepEnabled(false).setOrientationLocked(false)
                                    .setDesiredBarcodeFormats(ScanOptions.QR_CODE),
                            )
                        },
                        onPair = { pair() },
                    )
                } else {
                    HomeScreen(
                        store = store,
                        tick = tick,
                        busy = busy,
                        onSendFiles = { pickFiles.launch(arrayOf("*/*")) },
                        onSendText = { send(Shared(emptyList(), it)) },
                        onAskNotifications = {
                            if (Build.VERSION.SDK_INT >= 33) askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
                        },
                        onRefresh = { AppScope.launch { Scheduler.kick(applicationContext) } },
                    )
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    override fun onResume() {
        super.onResume()
        Notifications.appVisible = true
    }

    override fun onPause() {
        super.onPause()
        Notifications.appVisible = false
    }

    /** A t7drop:pair?... link, from a scanner app or a message. */
    private fun handle(intent: Intent?) {
        val data = intent?.dataString ?: return
        if (!data.startsWith("t7drop:")) return
        if (store.config() == null) invitation = data else toast("This phone is paired already.")
    }

    private fun pair() {
        val text = invitation
        val name = deviceName.trim().ifEmpty { "phone" }
        pairing = true
        pairError = null
        lifecycleScope.launch {
            try {
                withContext(Dispatchers.IO) {
                    val parsed = Pairing.parse(text)
                    val reply = Api(parsed.url, null).pair(parsed.code, name)
                    val config = Config(
                        parsed.url, reply.getString("device_id"), reply.getString("name"),
                        reply.getString("token"), parsed.key,
                    )
                    store.saveConfig(config)
                    try {
                        store.saveDevices(Api(config.url, config.token).devices())
                    } catch (e: Exception) {
                        // the long poll fetches them later
                    }
                }
                invitation = ""
            } catch (e: IllegalArgumentException) {
                pairError = e.message
            } catch (e: TransientException) {
                pairError = "Pairing failed: the code may be wrong, used, or older than 10 minutes, " +
                    "or the server cannot be reached. (${e.message})"
            } catch (e: ApiException) {
                pairError = "The server refused: ${e.message}"
            } finally {
                pairing = false
            }
        }
    }

    private fun send(shared: Shared) {
        AppScope.launch {
            val message = Sender.queue(applicationContext, shared) { busy = it }
            busy = null
            withContext(Dispatchers.Main) { toast(message) }
        }
    }

    /**
     * While the screen is open: a long poll. Whenever something changes for this device,
     * a transfer run picks it up (downloads, deliveries), so the screen stays current.
     */
    private suspend fun poll() = withContext(Dispatchers.IO) {
        var since = -1
        var instance: String? = null
        var pause = 1_000L
        while (isActive) {
            val config = store.config()
            if (config == null) {
                delay(1_000)
                continue
            }
            try {
                val snapshot = runInterruptible { Api(config.url, config.token).sync(if (instance == null) -1 else since, 50) }
                val newInstance = snapshot.getString("instance")
                val version = snapshot.getInt("version")
                if (newInstance != instance || version != since) Scheduler.kick(applicationContext)
                instance = newInstance
                since = version
                if (store.problem.value?.startsWith("Server not reachable") == true) store.problem.value = null
                pause = 1_000L
            } catch (e: TransientException) {
                store.problem.value = "Server not reachable; trying again. (${e.message})"
                instance = null
                delay(pause)
                pause = minOf(pause * 2, 60_000L)
            } catch (e: ApiException) {
                store.problem.value = "The server refused: ${e.message}"
                delay(pause)
                pause = minOf(pause * 2, 60_000L)
            }
        }
    }

    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_LONG).show()
}
