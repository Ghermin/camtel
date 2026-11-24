package io.github.ghermin.camtel

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.Binder

/**
 * Lets CamTel.exe read the pairing token over USB:
 * `adb shell content query --uri content://io.github.ghermin.camtel.pairing`.
 *
 * Only the adb shell (uid 2000) and root may read it, so other apps on the phone cannot. Anyone
 * who can run adb shell on the phone (USB debugging allowed for that PC) could use the camera anyway.
 */
class PairingProvider : ContentProvider() {
    override fun onCreate() = true

    override fun query(
        uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?,
        sortOrder: String?,
    ): Cursor {
        val uid = Binder.getCallingUid()
        if (uid != SHELL_UID && uid != ROOT_UID) throw SecurityException("adb only")
        val ctx = context ?: throw IllegalStateException()
        return MatrixCursor(arrayOf("id", "token")).apply {
            addRow(arrayOf(Pairing.id(ctx), Pairing.tokenHex(ctx)))
        }
    }

    override fun getType(uri: Uri): String? = null
    override fun insert(uri: Uri, values: ContentValues?): Uri? = throw UnsupportedOperationException()
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int =
        throw UnsupportedOperationException()
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int =
        throw UnsupportedOperationException()

    private companion object {
        const val SHELL_UID = 2000
        const val ROOT_UID = 0
    }
}
