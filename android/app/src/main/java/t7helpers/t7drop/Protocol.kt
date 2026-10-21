package t7helpers.t7drop

/** Constants of protocol version 1, shared with the server and the other clients (docs/design.md). */
object Protocol {
    const val CHUNK_SIZE = 4 * 1024 * 1024
    const val INLINE_MAX = 64 * 1024
    const val INSTANCE_HEADER = "X-T7drop-Instance"

    /** The server keeps undelivered items for 7 days; the sender gives up then too. */
    const val GIVE_UP_MILLIS = 7L * 24 * 3600 * 1000

    /** The server's limit for one item, in bytes of ciphertext. */
    const val MAX_ITEM_BYTES = 200L * 1024 * 1024

    private val ITEM_ID = Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")

    fun chunkCount(size: Long): Int = maxOf(1L, (size + CHUNK_SIZE - 1) / CHUNK_SIZE).toInt()

    fun isItemId(value: String): Boolean = ITEM_ID.matches(value)
}
