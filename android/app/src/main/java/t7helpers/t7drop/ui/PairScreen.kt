package t7helpers.t7drop.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import t7helpers.t7drop.Pairing

@Composable
fun PairScreen(
    invitation: String,
    onInvitation: (String) -> Unit,
    deviceName: String,
    onDeviceName: (String) -> Unit,
    busy: Boolean,
    error: String?,
    onScan: () -> Unit,
    onPair: () -> Unit,
) {
    Surface(Modifier.fillMaxSize()) {
        Column(
            Modifier.safeDrawingPadding().verticalScroll(rememberScrollState()).padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Text("t7drop", style = MaterialTheme.typography.headlineMedium)
            Text(
                "Pair this phone with your laptop. On the laptop, have t7drop show its invitation " +
                    "(the CLI: t7drop invite), then scan the QR code.",
                style = MaterialTheme.typography.bodyLarge,
            )
            OutlinedTextField(
                value = deviceName,
                onValueChange = onDeviceName,
                label = { Text("This phone's name") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Button(onClick = onScan, enabled = !busy, modifier = Modifier.fillMaxWidth()) { Text("Scan the QR code") }
            Text("Or paste the invitation, the text that starts with t7drop:pair?", style = MaterialTheme.typography.bodyMedium)
            OutlinedTextField(
                value = invitation,
                onValueChange = onInvitation,
                label = { Text("Invitation") },
                minLines = 2,
                modifier = Modifier.fillMaxWidth(),
            )
            // A t7drop: link can come from any app or web page: say where it leads.
            val server = remember(invitation) { Pairing.serverHost(invitation) }
            if (server != null) {
                Text("Server: $server. Pair only if it is your own t7drop server.", style = MaterialTheme.typography.bodyMedium)
            }
            OutlinedButton(onClick = onPair, enabled = !busy && invitation.isNotBlank(), modifier = Modifier.fillMaxWidth()) {
                Text("Pair")
            }
            if (busy) CircularProgressIndicator()
            if (error != null) Text(error, color = MaterialTheme.colorScheme.error)
        }
    }
}
