package t7helpers.t7drop

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import t7helpers.t7drop.data.OutItem
import t7helpers.t7drop.data.OutState
import t7helpers.t7drop.data.Store
import java.io.File
import java.util.UUID

/** What the store keeps and tidies (M5 item 3), on a device's real app storage. */
@RunWith(AndroidJUnit4::class)
class StoreTest {
    private val store = Store.get(InstrumentationRegistry.getInstrumentation().targetContext)
    private val now = System.currentTimeMillis()
    private val day = 24L * 3600 * 1000

    private fun id() = UUID.randomUUID().toString()

    @Test
    fun aRefusalIsKeptForEightDaysAndUnpairingForgetsIt() {
        val refused = id()
        assertFalse(store.wasRefused(refused))
        store.markRefused(refused, now)
        assertTrue(store.wasRefused(refused))
        val old = id()
        store.markRefused(old, now - 9 * day)
        store.markRefused(id(), now)
        assertFalse("older than 8 days: dropped", store.wasRefused(old))
        assertTrue(store.wasRefused(refused))
        store.forget()
        assertFalse("refused under the old key; a new one may read it", store.wasRefused(refused))
    }

    @Test
    fun partialDownloadsOfItemsGoneGoAfterADay() {
        store.incomingDir.mkdirs()
        val waitingId = id()
        val old = File(store.incomingDir, id() + ".part")
        val recent = File(store.incomingDir, id() + ".part")
        val waiting = File(store.incomingDir, "$waitingId.part")
        listOf(old, recent, waiting).forEach { it.writeText("x") }
        assertTrue(old.setLastModified(now - 2 * day) && waiting.setLastModified(now - 2 * day))
        store.dropStaleParts(setOf(waitingId), now)
        assertFalse(old.exists())
        assertTrue("perhaps lost in a server restart: it may come again", recent.exists())
        assertTrue("still waiting: its download goes on", waiting.exists())
        recent.delete()
        waiting.delete()
    }

    @Test
    fun deliveredItemsLeaveTheOutboxAfter30Days() {
        fun item(state: String, age: Long): String {
            val id = id()
            val folder = store.newOutFolder(id)
            OutItem.create(
                folder,
                JSONObject().put("id", id).put("to", "bbbbbbbbbbbb").put("kind", "text").put("size", 1).put("csize", 1)
                    .put("sha256", "00").put("created", now - age).put("state", state),
            )
            assertTrue(File(folder, "item.json").setLastModified(now - age))
            return id
        }
        val old = item(OutState.DELIVERED, 31 * day)
        val recent = item(OutState.DELIVERED, 2 * day)
        val failed = item(OutState.FAILED, 31 * day)
        store.dropOldDeliveries(now)
        val left = store.outbox().map { it.id }.toSet()
        assertFalse(old in left)
        assertTrue(recent in left)
        assertTrue("a failed item waits for Retry or Remove", failed in left)
        store.outbox().filter { it.id in setOf(recent, failed) }.forEach { store.remove(it) }
        assertEquals(emptyList<String>(), store.outbox().map { it.id }.filter { it in setOf(old, recent, failed) })
    }
}
