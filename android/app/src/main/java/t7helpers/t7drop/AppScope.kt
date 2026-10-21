package t7helpers.t7drop

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob

/** Work that must not end with a screen, such as encrypting a shared file. */
object AppScope : CoroutineScope by CoroutineScope(SupervisorJob() + Dispatchers.IO)
