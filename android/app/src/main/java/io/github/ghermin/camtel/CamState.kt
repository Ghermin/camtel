package io.github.ghermin.camtel

import android.os.Handler
import android.os.Looper
import android.util.Size
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Shared camera settings. The phone UI and the PC both read and change this state;
 * every change bumps [version] so the PC (long-polling /status) and the UI stay in sync.
 */
object CamState {
    val RESOLUTIONS = listOf(Size(640, 480), Size(1280, 720), Size(1920, 1080))

    @Volatile var front = false
    @Volatile var torch = false
    @Volatile var hasFlash = true
    @Volatile var resIndex = 1
    @Volatile var quality = 80
    @Volatile var zoom = 0f
    @Volatile var mirror = false
    @Volatile var blackout = false
    /** false: fixed 30 fps (smooth). true: let auto-exposure drop the frame rate in low light (brighter). */
    @Volatile var night = false
    /** Exposure compensation in EV, within [exposureMin, exposureMax] reported by the camera. */
    @Volatile var exposure = 0f
    @Volatile var exposureMin = -2f
    @Volatile var exposureMax = 2f

    // Live info (not part of the version, polled by the UI once per second).
    @Volatile var frameWidth = 0
    @Volatile var frameHeight = 0
    @Volatile var fps = 0
    @Volatile var cameraFps = 0
    @Volatile var encodeMs = 0
    @Volatile var clients = 0

    private val lock = Object()
    @Volatile var version = 1L
        private set

    private val main = Handler(Looper.getMainLooper())
    private val listeners = CopyOnWriteArrayList<() -> Unit>()

    fun addListener(l: () -> Unit) = listeners.add(l)
    fun removeListener(l: () -> Unit) = listeners.remove(l)

    /** Call after any setting change (from any thread). */
    fun changed() {
        synchronized(lock) {
            version++
            lock.notifyAll()
        }
        main.post { listeners.forEach { it() } }
    }

    /** Blocks until [version] > [since] or the timeout elapses. */
    fun awaitChange(since: Long, timeoutMs: Long) {
        synchronized(lock) {
            val deadline = System.currentTimeMillis() + timeoutMs
            while (version <= since) {
                val remaining = deadline - System.currentTimeMillis()
                if (remaining <= 0) return
                lock.wait(remaining)
            }
        }
    }

    val resolution: Size get() = RESOLUTIONS[resIndex]

    fun toJson(name: String): String {
        val s = resolution
        return """{"app":"CamTel","version":$version,"name":"${name.replace("\"", "")}",""" +
            """"camera":"${if (front) "front" else "back"}","torch":$torch,"hasFlash":$hasFlash,""" +
            """"resolution":"${s.width}x${s.height}","quality":$quality,"zoom":$zoom,"mirror":$mirror,""" +
            """"blackout":$blackout,"night":$night,"exposure":$exposure,"exposureMin":$exposureMin,""" +
            """"exposureMax":$exposureMax,"width":$frameWidth,"height":$frameHeight,"fps":$fps,""" +
            """"cameraFps":$cameraFps,"encodeMs":$encodeMs,"clients":$clients}"""
    }
}
