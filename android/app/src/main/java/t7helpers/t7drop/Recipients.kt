package t7helpers.t7drop

import t7helpers.t7drop.data.Device

/** Which device this phone sends to (the Windows app has the same rule in Store.Recipient). */
object Recipients {
    /**
     * The device that invited this phone: the newest device that may invite and was paired
     * before it. An older one belongs to an earlier group (such as a CLI used for tests) and
     * could not decrypt what this phone sends. Failing that, the newest device that may
     * invite, then the newest device.
     */
    fun pick(devices: List<Device>, me: String): String? {
        val mine = devices.firstOrNull { it.id == me }?.created ?: Long.MAX_VALUE
        val others = devices.filter { it.id != me }.sortedByDescending { it.created }
        val inviters = others.filter { it.canInvite }
        return (inviters.firstOrNull { it.created <= mine } ?: inviters.firstOrNull() ?: others.firstOrNull())?.id
    }
}