package io.github.ghermin.camtel

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.widget.Toast
import androidx.core.app.NotificationCompat
import androidx.core.content.IntentCompat
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/**
 * Self-update from the latest GitHub release. Runs when the app is opened and no PC is
 * streaming. When the app installed itself (or the PC set it as installer over USB), Android
 * applies the update without asking; otherwise the system shows one confirmation.
 */
object Updater {
    private const val TAG = "CamTelUpdater"
    private const val LATEST_API = "https://api.github.com/repos/Ghermin/camtel/releases/latest"
    private const val CHECK_INTERVAL_MS = 15 * 60 * 1000L
    private const val CHANNEL = "camtel_updates"

    @Volatile private var running = false

    fun maybeUpdate(context: Context) {
        if (BuildConfig.DEBUG || running) return
        val prefs = context.getSharedPreferences("camtel", Context.MODE_PRIVATE)
        val now = System.currentTimeMillis()
        if (now - prefs.getLong("lastUpdateCheck", 0) < CHECK_INTERVAL_MS) return
        prefs.edit().putLong("lastUpdateCheck", now).apply()
        running = true
        val app = context.applicationContext
        Thread({
            try {
                val release = latest() ?: return@Thread
                if (!isNewer(release.version, BuildConfig.VERSION_NAME) || CamState.clients > 0) return@Thread
                toast(app, "Mise à jour de CamTel vers ${release.version}…")
                val apk = download(app, release) ?: return@Thread
                if (CamState.clients > 0) return@Thread // a PC connected meanwhile: try again next launch
                prefs.edit().putString("updatingTo", release.version).apply()
                install(app, apk)
            } catch (e: Exception) {
                Log.w(TAG, "update failed", e)
            } finally {
                running = false
            }
        }, "camtel-update").start()
    }

    private data class Release(val version: String, val apkUrl: String, val size: Long)

    private fun latest(): Release? {
        val conn = URL(LATEST_API).openConnection() as HttpURLConnection
        conn.setRequestProperty("Accept", "application/vnd.github+json")
        conn.setRequestProperty("User-Agent", "CamTel-Android/${BuildConfig.VERSION_NAME}")
        conn.connectTimeout = 8000
        conn.readTimeout = 8000
        try {
            if (conn.responseCode != 200) return null
            val json = JSONObject(conn.inputStream.bufferedReader().readText())
            val version = json.getString("tag_name").trimStart('v')
            val assets = json.getJSONArray("assets")
            for (i in 0 until assets.length()) {
                val a = assets.getJSONObject(i)
                if (a.getString("name") == "CamTel.apk")
                    return Release(version, a.getString("browser_download_url"), a.getLong("size"))
            }
            return null
        } finally {
            conn.disconnect()
        }
    }

    fun isNewer(candidate: String, current: String): Boolean {
        val a = candidate.split('.').map { it.toIntOrNull() ?: 0 }
        val b = current.split('.').map { it.toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(a.size, b.size)) {
            val x = a.getOrElse(i) { 0 }
            val y = b.getOrElse(i) { 0 }
            if (x != y) return x > y
        }
        return false
    }

    private fun download(context: Context, release: Release): File? {
        val file = File(context.cacheDir, "update.apk")
        val conn = URL(release.apkUrl).openConnection() as HttpURLConnection
        conn.instanceFollowRedirects = true
        conn.connectTimeout = 10000
        conn.readTimeout = 30000
        try {
            conn.inputStream.use { input -> file.outputStream().use { input.copyTo(it) } }
        } finally {
            conn.disconnect()
        }
        return if (file.length() == release.size) file else null
    }

    private fun install(context: Context, apk: File) {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            if (Build.VERSION.SDK_INT >= 31) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val id = installer.createSession(params)
        installer.openSession(id).use { session ->
            session.openWrite("CamTel.apk", 0, apk.length()).use { out ->
                apk.inputStream().use { it.copyTo(out) }
                session.fsync(out)
            }
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or
                (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
            val pending = PendingIntent.getBroadcast(
                context, id, Intent(context, InstallResultReceiver::class.java), flags
            )
            session.commit(pending.intentSender)
        }
    }

    private fun toast(context: Context, text: String) =
        Handler(Looper.getMainLooper()).post { Toast.makeText(context, text, Toast.LENGTH_LONG).show() }

    /** Shows the system confirmation when Android requires one (first self-update). */
    class InstallResultReceiver : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
                PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                    val confirm = IntentCompat.getParcelableExtra(intent, Intent.EXTRA_INTENT, Intent::class.java)
                    confirm?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    confirm?.let { context.startActivity(it) }
                }
                PackageInstaller.STATUS_SUCCESS -> Unit
                else -> {
                    Log.w(TAG, "install failed: ${intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)}")
                    context.getSharedPreferences("camtel", Context.MODE_PRIVATE).edit().remove("updatingTo").apply()
                }
            }
        }
    }

    /** After a self-update the app process is gone: offer to reopen it. */
    class UpdatedReceiver : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
            val prefs = context.getSharedPreferences("camtel", Context.MODE_PRIVATE)
            // Updates pushed by the PC over USB relaunch the app themselves.
            prefs.getString("updatingTo", null) ?: return
            prefs.edit().remove("updatingTo").apply()
            val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            if (Build.VERSION.SDK_INT >= 26)
                nm.createNotificationChannel(NotificationChannel(CHANNEL, "Mises à jour", NotificationManager.IMPORTANCE_DEFAULT))
            val open = PendingIntent.getActivity(
                context, 0, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE
            )
            nm.notify(
                2, NotificationCompat.Builder(context, CHANNEL)
                    .setSmallIcon(R.drawable.ic_videocam)
                    .setContentTitle("CamTel mis à jour (${BuildConfig.VERSION_NAME})")
                    .setContentText("Touchez pour rouvrir CamTel")
                    .setContentIntent(open)
                    .setAutoCancel(true)
                    .build()
            )
        }
    }
}
