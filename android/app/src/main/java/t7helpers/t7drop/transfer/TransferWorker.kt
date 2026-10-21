package t7helpers.t7drop.transfer

import android.content.Context
import android.content.pm.ServiceInfo
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.ForegroundInfo
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkInfo
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import t7helpers.t7drop.Notifications
import t7helpers.t7drop.data.Store
import t7helpers.t7drop.net.ApiException
import t7helpers.t7drop.net.TransientException
import java.io.IOException
import java.util.concurrent.TimeUnit

/**
 * Sends what is queued and receives what is waiting, once. WorkManager runs it when the
 * network is up, keeps it across the app being closed and the phone restarting, and runs
 * it again with growing pauses while the server cannot be reached. No permanent service:
 * a notification shows only while a large transfer runs.
 */
class TransferWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val store = Store.get(applicationContext)
        if (store.config() == null) return@withContext Result.success()
        var foreground = false
        val transfers = Transfers(
            applicationContext,
            store,
            beforeBigTransfer = {
                if (!foreground) {
                    foreground = true
                    try {
                        runBlocking { setForeground(getForegroundInfo()) }
                    } catch (e: Exception) {
                        // Android may refuse from the background; the transfer goes on without.
                    }
                }
            },
            onReceived = { Notifications.received(applicationContext, it) },
        )
        try {
            transfers.runOnce()
            store.problem.value = null
            Result.success()
        } catch (e: TransientException) {
            store.problem.value = "Server not reachable; trying again. (${e.message})"
            Result.retry()
        } catch (e: ApiException) {
            store.problem.value = "The server refused: ${e.message}"
            if (e.transient) Result.retry() else Result.failure()
        } catch (e: IOException) {
            store.problem.value = "Could not save: ${e.message}"
            Result.retry()
        }
    }

    override suspend fun getForegroundInfo(): ForegroundInfo =
        ForegroundInfo(
            Notifications.TRANSFER_ID,
            Notifications.transferring(applicationContext),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
        )
}

object Scheduler {
    private const val NAME = "transfer"

    /**
     * Asks for a transfer run soon. A run in progress goes on, and another follows it (once);
     * a run waiting out a retry pause is replaced by one that starts now. Call it off the
     * main thread.
     */
    fun kick(context: Context) {
        val manager = WorkManager.getInstance(context)
        val states = manager.getWorkInfosForUniqueWork(NAME).get().map { it.state }
        val running = WorkInfo.State.RUNNING in states
        val waiting = WorkInfo.State.ENQUEUED in states || WorkInfo.State.BLOCKED in states
        if (running && waiting) return
        val request = OneTimeWorkRequestBuilder<TransferWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 15, TimeUnit.SECONDS)
            .build()
        val policy = if (running) ExistingWorkPolicy.APPEND_OR_REPLACE else ExistingWorkPolicy.REPLACE
        manager.enqueueUniqueWork(NAME, policy, request)
    }
}
