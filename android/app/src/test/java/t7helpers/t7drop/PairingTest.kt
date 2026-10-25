package t7helpers.t7drop

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class PairingTest {
    private val key = ByteArray(32) { it.toByte() }

    @Test
    fun anInvitationRoundTrips() {
        val text = Pairing.make("https://example.org/abc", "ABCDE23456", key)
        val parsed = Pairing.parse("  $text\n")
        assertEquals("https://example.org/abc", parsed.url)
        assertEquals("ABCDE23456", parsed.code)
        assertArrayEquals(key, parsed.key)
    }

    @Test
    fun theCliInvitationParses() {
        // As cli/t7drop_cli/pairing.py writes it (urlencode, base64url without padding).
        val text = "t7drop:pair?v=1&u=https%3A%2F%2Fexample.org%2Fabc&c=ABCDE23456&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"
        val parsed = Pairing.parse(text)
        assertEquals("https://example.org/abc", parsed.url)
        assertArrayEquals(key, parsed.key)
    }

    @Test
    fun theServerHostIsShownBeforePairing() {
        assertEquals("example.org", Pairing.serverHost(Pairing.make("https://example.org/abc", "ABCDE23456", key)))
        assertEquals(null, Pairing.serverHost("t7drop:pair?v=1&u=https%3A%2F%2Fx&c=y"))
        assertEquals(null, Pairing.serverHost(""))
    }

    @Test
    fun otherTextIsRefused() {
        for (bad in listOf(
            "https://example.org",
            "t7drop:pair?v=2&u=https%3A%2F%2Fx&c=y&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",
            "t7drop:pair?v=1&u=https%3A%2F%2Fx&c=y",
            "t7drop:pair?v=1&u=https%3A%2F%2Fx&c=y&k=short",
            "t7drop:pair?v=1&u=ftp%3A%2F%2Fx&c=y&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",
        )) {
            assertThrows(IllegalArgumentException::class.java) { Pairing.parse(bad) }
        }
    }
}
