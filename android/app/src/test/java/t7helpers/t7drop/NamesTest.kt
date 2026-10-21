package t7helpers.t7drop

import org.junit.Assert.assertEquals
import org.junit.Test
import t7helpers.t7drop.files.Names

class NamesTest {
    @Test
    fun namesAreMadeSafe() {
        assertEquals("passwd", Names.safe("../../etc/passwd"))
        assertEquals("win.ini", Names.safe("C:\\Windows\\win.ini"))
        assertEquals("a_b_c__d_e_f_.txt", Names.safe("a<b>c:\"d|e?f*.txt"))
        assertEquals("_CON.txt", Names.safe("CON.txt"))
        assertEquals("name", Names.safe("name. . "))
        assertEquals("file", Names.safe(""))
        assertEquals("file", Names.safe(null))
        assertEquals(150, Names.safe("x".repeat(300) + ".jpg").length)
        assertEquals("\u0444\u043e\u0442\u043e.jpg", Names.safe("\u0444\u043e\u0442\u043e.jpg"))
    }

    @Test
    fun chunkCountsMatchTheServer() {
        assertEquals(1, Protocol.chunkCount(1))
        assertEquals(1, Protocol.chunkCount(Protocol.CHUNK_SIZE.toLong()))
        assertEquals(2, Protocol.chunkCount(Protocol.CHUNK_SIZE + 1L))
    }
}
