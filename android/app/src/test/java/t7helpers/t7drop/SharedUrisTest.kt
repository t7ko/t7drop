package t7helpers.t7drop

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SharedUrisTest {
    private val own = "t7helpers.t7drop"

    @Test
    fun anotherAppsContentIsTaken() {
        assertTrue(SharedUris.acceptable("content", "com.google.android.apps.photos.contentprovider", own))
        assertTrue(SharedUris.acceptable("content", "media", own))
        assertTrue(SharedUris.acceptable("CONTENT", "com.android.externalstorage.documents", own))
    }

    @Test
    fun filesAndT7dropsOwnContentAreNot() {
        assertFalse(SharedUris.acceptable("file", null, own))
        assertFalse(SharedUris.acceptable("file", "", own))
        assertFalse(SharedUris.acceptable("content", own, own))
        assertFalse(SharedUris.acceptable("content", "$own.androidx-startup", own))
        assertFalse(SharedUris.acceptable("content", null, own))
        assertFalse(SharedUris.acceptable(null, "media", own))
        assertFalse(SharedUris.acceptable("http", "example.org", own))
    }
}
