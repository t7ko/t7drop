package t7helpers.t7drop.ui

import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import t7helpers.t7drop.AppScope
import t7helpers.t7drop.Sender
import t7helpers.t7drop.Shared
import t7helpers.t7drop.data.Store

/**
 * The share target: what another app shares is encrypted and queued at once, while this
 * small screen stays open, because the right to read a shared file ends when it closes.
 * Then a transfer starts in the background, and the screen goes.
 */
class ShareActivity : ComponentActivity() {
    private var progress by mutableStateOf("Preparing…")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (savedInstanceState != null) {
            finish() // a recreated screen: the queueing ran in the first one
            return
        }
        if (Store.get(this).config() == null) {
            toast("Pair t7drop with your laptop first.")
            startActivity(Intent(this, MainActivity::class.java))
            finish()
            return
        }
        val shared = Shared.fromIntent(intent, packageName)
        if (shared.isEmpty) {
            toast("There is nothing to send in what was shared.")
            finish()
            return
        }
        setContent {
            T7dropTheme {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Card {
                        Row(Modifier.padding(20.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                            CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                            Text(progress)
                        }
                    }
                }
            }
        }
        AppScope.launch {
            val message = Sender.queue(applicationContext, shared) { progress = it }
            withContext(Dispatchers.Main) {
                toast(message)
                finish()
            }
        }
    }

    private fun toast(message: String) = Toast.makeText(applicationContext, message, Toast.LENGTH_LONG).show()
}
