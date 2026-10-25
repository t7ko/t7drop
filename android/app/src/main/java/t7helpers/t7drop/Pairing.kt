package t7helpers.t7drop

import java.net.URLDecoder
import java.net.URLEncoder
import java.util.Base64

class Invitation(val url: String, val code: String, val key: ByteArray)

/**
 * The invitation that the first device shows as a QR code (cli/t7drop_cli/pairing.py):
 * t7drop:pair?v=1&u=<server URL>&c=<one-time code>&k=<group key, base64url>
 */
object Pairing {
    private const val PREFIX = "t7drop:pair?"

    fun b64url(data: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(data)

    fun fromB64url(text: String): ByteArray = Base64.getUrlDecoder().decode(text)

    /** Throws IllegalArgumentException with a message for the user if [text] is no invitation. */
    fun parse(text: String): Invitation {
        val value = text.trim()
        require(value.startsWith(PREFIX)) { "This is not a t7drop invitation." }
        val query = value.substring(PREFIX.length).split('&').filter { it.isNotEmpty() }.associate {
            URLDecoder.decode(it.substringBefore('='), "UTF-8") to URLDecoder.decode(it.substringAfter('=', ""), "UTF-8")
        }
        require(query["v"] == "1") { "This invitation is from another version of t7drop." }
        val url = query["u"]
        val code = query["c"]
        val encodedKey = query["k"]
        require(url != null && code != null && encodedKey != null) { "The invitation is incomplete." }
        val key = try {
            fromB64url(encodedKey)
        } catch (e: IllegalArgumentException) {
            throw IllegalArgumentException("The invitation is damaged.")
        }
        require(key.size == 32 && (url.startsWith("https://") || url.startsWith("http://"))) { "The invitation is damaged." }
        return Invitation(url, code, key)
    }

    /** The host an invitation pairs with, shown before pairing (a link can come from anyone); null if [text] is none. */
    fun serverHost(text: String): String? = try {
        java.net.URI(parse(text).url).host
    } catch (e: Exception) {
        null
    }

    fun make(url: String, code: String, key: ByteArray): String =
        PREFIX + listOf("v" to "1", "u" to url, "c" to code, "k" to b64url(key))
            .joinToString("&") { (name, value) -> name + "=" + URLEncoder.encode(value, "UTF-8") }
}
