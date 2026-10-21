package t7helpers.t7drop

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import t7helpers.t7drop.data.Device

class RecipientsTest {
    private val oldCli = Device("cli", "laptop-cli", true, 100)
    private val oldPhone = Device("old", "phone", false, 150)
    private val laptop = Device("laptop", "laptop", true, 200)
    private val phone = Device("phone", "phone", false, 300)

    @Test
    fun theDeviceThatInvitedThisPhone() {
        assertEquals("laptop", Recipients.pick(listOf(oldCli, oldPhone, laptop, phone), "phone"))
    }

    @Test
    fun notAnInviterPairedLater() {
        val later = Device("later", "another laptop", true, 400)
        assertEquals("laptop", Recipients.pick(listOf(laptop, phone, later), "phone"))
    }

    @Test
    fun fallbacks() {
        assertEquals("laptop", Recipients.pick(listOf(phone, laptop), "unknown"))
        assertEquals("old", Recipients.pick(listOf(oldPhone, phone), "phone"))
        assertNull(Recipients.pick(listOf(phone), "phone"))
    }
}