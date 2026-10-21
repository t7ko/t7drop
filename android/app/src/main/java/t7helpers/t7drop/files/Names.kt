package t7helpers.t7drop.files

/** File names from other devices, made safe (the same rules as cli/t7drop_cli/files.py). */
object Names {
    private val forbidden = "<>:\"/\\|?*".toSet()
    private val reserved = setOf("CON", "PRN", "AUX", "NUL") +
        (1..9).map { "COM$it" } + (1..9).map { "LPT$it" }
    private const val MAX_NAME = 150

    fun safe(name: String?): String {
        var result = (name ?: "").replace('\\', '/').substringAfterLast('/')
        result = result.map { if (it in forbidden || it.code < 32) '_' else it }.joinToString("")
        result = result.trim().trimEnd('.', ' ')
        if (result.isEmpty()) result = "file"
        if (result.substringBefore('.').uppercase() in reserved) result = "_$result"
        if (result.length > MAX_NAME) {
            val dot = result.lastIndexOf('.')
            val ext = if (dot > 0) result.substring(dot).take(20) else ""
            result = result.substring(0, MAX_NAME - ext.length) + ext
        }
        return result
    }
}
