package t7helpers.t7drop.net

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import t7helpers.t7drop.Protocol
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.Base64

/**
 * No answer from t7drop: a network error, or a response without t7drop's header (an nginx
 * error page, or an unknown token). Try again later; never drop items because of it.
 */
class TransientException(message: String, cause: Throwable? = null) : IOException(message, cause)

/** t7drop refused the request. */
class ApiException(val status: Int, val code: String, val details: JSONObject) : Exception("$code (HTTP $status)") {
    val transient: Boolean get() = status >= 500 || status == 429 || status == 507
}

/** The t7drop protocol over HTTPS (docs/design.md, "Protocol v1"). Blocking: call it off the main thread. */
class Api(url: String, private val token: String?) {
    private val base = url.trimEnd('/') + "/api/v1"

    /** The server instance of the last answer; it changes when the server restarts. */
    @Volatile
    var instance: String? = null
        private set

    fun request(
        method: String,
        path: String,
        body: ByteArray? = null,
        contentType: String = "application/octet-stream",
        timeoutMillis: Int = 30_000,
        auth: Boolean = true,
        onSent: (Int) -> Unit = {},
    ): ByteArray {
        try {
            val connection = URL(base + path).openConnection() as HttpURLConnection
            connection.requestMethod = method
            connection.connectTimeout = 20_000
            connection.readTimeout = timeoutMillis
            connection.useCaches = false
            connection.instanceFollowRedirects = false // t7drop never redirects; the token stays with its server
            if (auth && token != null) connection.setRequestProperty("Authorization", "Bearer $token")
            if (body != null) {
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", contentType)
                connection.setFixedLengthStreamingMode(body.size)
                connection.outputStream.use { out ->
                    var written = 0
                    while (written < body.size) {
                        val n = minOf(SLICE, body.size - written)
                        out.write(body, written, n)
                        written += n
                        onSent(written)
                    }
                }
            }
            val status = connection.responseCode
            val stream = if (status >= 400) connection.errorStream else connection.inputStream
            val data = stream?.use { it.readBytes() } ?: ByteArray(0)
            val answer = connection.getHeaderField(Protocol.INSTANCE_HEADER)
            if (answer == null) {
                connection.disconnect()
                throw TransientException("HTTP $status without t7drop's header")
            }
            instance = answer
            if (status >= 400) {
                val details = try {
                    JSONObject(String(data, Charsets.UTF_8))
                } catch (e: JSONException) {
                    JSONObject()
                }
                throw ApiException(status, details.optString("error", "error"), details)
            }
            return data
        } catch (e: TransientException) {
            throw e
        } catch (e: IOException) {
            throw TransientException("${e.javaClass.simpleName}: ${e.message}", e)
        }
    }

    private fun call(method: String, path: String, payload: JSONObject? = null, timeoutMillis: Int = 30_000, auth: Boolean = true): JSONObject {
        val body = payload?.toString()?.toByteArray(Charsets.UTF_8) ?: if (method == "POST" || method == "PUT") ByteArray(0) else null
        val data = request(method, path, body, "application/json", timeoutMillis, auth)
        return if (data.isEmpty()) JSONObject() else JSONObject(String(data, Charsets.UTF_8))
    }

    fun pair(code: String, name: String): JSONObject =
        call("POST", "/pair", JSONObject().put("code", code).put("name", name), auth = false)

    fun devices(): JSONArray = call("GET", "/devices").getJSONArray("devices")

    fun createItem(id: String, to: String, size: Long, sha256: String, meta: ByteArray, data: ByteArray?): JSONObject {
        val payload = JSONObject().put("to", to).put("size", size).put("sha256", sha256)
            .put("meta", Base64.getEncoder().encodeToString(meta))
        if (data != null) payload.put("data", Base64.getEncoder().encodeToString(data))
        return call("PUT", "/items/$id", payload)
    }

    /** [onSent] hears how many bytes of the chunk went out so far. */
    fun putChunk(id: String, n: Int, data: ByteArray, onSent: (Int) -> Unit = {}) {
        request("PUT", "/items/$id/chunks/$n", data, timeoutMillis = 120_000, onSent = onSent)
    }

    private companion object {
        const val SLICE = 256 * 1024
    }

    fun getChunk(id: String, n: Int): ByteArray = request("GET", "/items/$id/chunks/$n", timeoutMillis = 120_000)

    fun commit(id: String): JSONObject = call("POST", "/items/$id/commit", timeoutMillis = 120_000)

    fun ack(id: String): JSONObject = call("POST", "/items/$id/ack")

    fun delete(id: String): JSONObject = call("DELETE", "/items/$id")

    fun sync(since: Int, waitSeconds: Int = 0): JSONObject =
        call("GET", "/sync?since=$since&wait=$waitSeconds", timeoutMillis = (waitSeconds + 20) * 1000)
}
