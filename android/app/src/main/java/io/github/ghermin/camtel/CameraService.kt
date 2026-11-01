package io.github.ghermin.camtel

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.ImageFormat
import android.graphics.Matrix
import android.graphics.Rect
import android.graphics.YuvImage
import android.os.Binder
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CaptureRequest
import android.util.Log
import android.util.Range
import android.view.Surface
import androidx.annotation.OptIn
import androidx.camera.camera2.interop.Camera2CameraInfo
import androidx.camera.camera2.interop.Camera2Interop
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.camera.core.AspectRatio
import androidx.camera.core.Camera
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.resolutionselector.AspectRatioStrategy
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleService
import java.io.ByteArrayOutputStream
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/**
 * Foreground service that owns the camera, the HTTP server and the discovery beacon,
 * so streaming continues when the screen is off or the app is in the background.
 */
class CameraService : LifecycleService(), StreamServer.Controller {

    inner class LocalBinder : Binder() {
        val service: CameraService get() = this@CameraService
    }

    private val binder = LocalBinder()
    private val main = Handler(Looper.getMainLooper())
    private val prefs by lazy { getSharedPreferences("camtel", Context.MODE_PRIVATE) }
    private lateinit var analysisExecutor: ExecutorService

    private val hub = FrameHub()
    private var server: StreamServer? = null
    private var discovery: Discovery? = null

    private var cameraProvider: ProcessCameraProvider? = null
    private var camera: Camera? = null
    /** Receives every encoded JPEG while the UI is visible (the phone shows exactly what the PC gets). */
    @Volatile private var previewListener: ((ByteArray) -> Unit)? = null
    private var targetRotation = Surface.ROTATION_90
    private var started = false

    private val jpegBuffer = ByteArrayOutputStream(512 * 1024)
    private var nv21Buffer: ByteArray? = null
    private var frameCount = 0
    private var cameraFrameCount = 0
    private var encodeNanos = 0L
    private var fpsWindowStart = System.nanoTime()

    val deviceName: String get() = (Build.MODEL ?: "Android").replace('|', ' ')

    override fun onCreate() {
        super.onCreate()
        CamState.front = prefs.getBoolean("front", false)
        CamState.resIndex = prefs.getInt("res", 1).coerceIn(CamState.RESOLUTIONS.indices)
        CamState.quality = prefs.getInt("quality", 80).coerceIn(30, 100)
        CamState.mirror = prefs.getBoolean("mirror", false)
        CamState.night = prefs.getBoolean("night", false)
        CamState.exposure = prefs.getFloat("exposure", 0f)
        CamState.torch = false
        CamState.blackout = false
        analysisExecutor = Executors.newSingleThreadExecutor()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        if (intent?.action == ACTION_STOP) {
            stopEverything()
            return START_NOT_STICKY
        }
        if (!started) {
            started = true
            goForeground()
            server = StreamServer(HTTP_PORT, hub, this).also {
                try {
                    it.start()
                } catch (e: Exception) {
                    Log.e(TAG, "server start failed", e)
                }
            }
            discovery = Discovery(deviceName, HTTP_PORT).also { it.start() }
            val future = ProcessCameraProvider.getInstance(this)
            future.addListener({
                cameraProvider = future.get()
                bindCamera()
            }, ContextCompat.getMainExecutor(this))
        }
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent): IBinder {
        super.onBind(intent)
        return binder
    }

    override fun onDestroy() {
        discovery?.stop()
        server?.stop()
        analysisExecutor.shutdown()
        CamState.clients = 0
        CamState.changed()
        super.onDestroy()
    }

    fun stopEverything() {
        sendBroadcast(Intent(ACTION_FINISH).setPackage(packageName))
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    // ---- Notification -----------------------------------------------------

    private fun goForeground() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL, getString(R.string.channel_name), NotificationManager.IMPORTANCE_LOW)
            )
        }
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, CameraService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE
        )
        val notification: Notification = NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_videocam)
            .setContentTitle(getString(R.string.notif_title))
            .setContentText(getString(R.string.notif_text))
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(R.drawable.ic_power, getString(R.string.stop), stop)
            .build()
        ServiceCompat.startForeground(
            this, NOTIF_ID, notification,
            if (Build.VERSION.SDK_INT >= 30) ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA else 0
        )
    }

    // ---- Camera -----------------------------------------------------------

    /**
     * Attaches (or detaches, with null) the on-screen preview. There is no separate camera
     * preview stream: on this kind of HAL a second stream halves the frame rate, so the UI
     * displays the same JPEG frames that are sent to the PC.
     */
    fun attachPreview(listener: ((ByteArray) -> Unit)?, rotation: Int?) {
        previewListener = listener
        if (rotation != null && rotation != targetRotation) {
            targetRotation = rotation
            bindCamera()
        }
    }

    /** Picks the AE frame-rate range: fixed 30 fps when smooth, widest range up to 30 in night mode. */
    @OptIn(ExperimentalCamera2Interop::class)
    private fun pickFpsRange(selector: CameraSelector): Range<Int>? {
        val provider = cameraProvider ?: return null
        val info = selector.filter(provider.availableCameraInfos).firstOrNull() ?: return null
        val ranges = Camera2CameraInfo.from(info)
            .getCameraCharacteristic(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)
            ?.filter { it.upper == 30 } ?: return null
        return if (CamState.night) ranges.minByOrNull { it.lower }
        else ranges.maxByOrNull { it.lower }
    }

    /**
     * Video-call tuning: the "video record" intent and fast noise reduction keep the HAL at the
     * requested frame rate instead of running heavy still-photo processing on every frame.
     */
    @OptIn(ExperimentalCamera2Interop::class)
    private fun <T> applyCaptureOptions(ext: Camera2Interop.Extender<T>, fpsRange: Range<Int>?) {
        fpsRange?.let { ext.setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) }
        ext.setCaptureRequestOption(CaptureRequest.CONTROL_CAPTURE_INTENT, CaptureRequest.CONTROL_CAPTURE_INTENT_VIDEO_RECORD)
        if (!CamState.night) {
            ext.setCaptureRequestOption(CaptureRequest.NOISE_REDUCTION_MODE, CaptureRequest.NOISE_REDUCTION_MODE_FAST)
            ext.setCaptureRequestOption(CaptureRequest.EDGE_MODE, CaptureRequest.EDGE_MODE_FAST)
        }
    }

    @OptIn(ExperimentalCamera2Interop::class)
    private fun bindCamera() {
        val provider = cameraProvider ?: return
        val cameraSelector =
            if (CamState.front) CameraSelector.DEFAULT_FRONT_CAMERA else CameraSelector.DEFAULT_BACK_CAMERA
        val fpsRange = pickFpsRange(cameraSelector)
        val size = CamState.resolution
        val aspect = if (size.width * 3 == size.height * 4) AspectRatio.RATIO_4_3 else AspectRatio.RATIO_16_9
        val selector = ResolutionSelector.Builder()
            .setAspectRatioStrategy(AspectRatioStrategy(aspect, AspectRatioStrategy.FALLBACK_RULE_AUTO))
            .setResolutionStrategy(
                ResolutionStrategy(size, ResolutionStrategy.FALLBACK_RULE_CLOSEST_LOWER_THEN_HIGHER)
            )
            .build()

        val analysis = ImageAnalysis.Builder()
            .setResolutionSelector(selector)
            .setTargetRotation(targetRotation)
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
            .also { applyCaptureOptions(Camera2Interop.Extender(it), fpsRange) }
            .build()
            .also { it.setAnalyzer(analysisExecutor, ::encodeFrame) }

        val wantTorch = CamState.torch
        try {
            provider.unbindAll()
            camera = provider.bindToLifecycle(this, cameraSelector, analysis)
            CamState.hasFlash = camera?.cameraInfo?.hasFlashUnit() == true
            CamState.torch = wantTorch && CamState.hasFlash
            if (CamState.torch) camera?.cameraControl?.enableTorch(true)
            camera?.cameraControl?.setLinearZoom(CamState.zoom)
            applyExposure()
            CamState.changed()
        } catch (e: Exception) {
            Log.e(TAG, "bind failed", e)
            if (CamState.front) {
                // No usable front camera: fall back to the back one.
                CamState.front = false
                bindCamera()
            }
        }
    }

    private fun encodeFrame(image: ImageProxy) {
        try {
            CamState.frameWidth = image.width
            CamState.frameHeight = image.height
            cameraFrameCount++
            val now = System.nanoTime()
            if (now - fpsWindowStart >= 1_000_000_000L) {
                CamState.fps = frameCount
                CamState.cameraFps = cameraFrameCount
                CamState.encodeMs = if (frameCount > 0) (encodeNanos / frameCount / 1_000_000).toInt() else 0
                frameCount = 0
                cameraFrameCount = 0
                encodeNanos = 0
                fpsWindowStart = now
            }
            val preview = previewListener
            if ((server?.clientCount ?: 0) == 0 && preview == null) return
            val t0 = System.nanoTime()

            val w = image.width
            val h = image.height
            val rotation = image.imageInfo.rotationDegrees
            jpegBuffer.reset()
            if (rotation == 0 || rotation == 180) {
                // Fast path: YUV -> NV21 -> JPEG, no RGB conversion.
                val nv21 = toNv21(image)
                if (rotation == 180) rotate180(nv21, w, h)
                YuvImage(nv21, ImageFormat.NV21, w, h, null)
                    .compressToJpeg(Rect(0, 0, w, h), CamState.quality, jpegBuffer)
            } else {
                // Portrait-mounted sensor output (rare with a landscape-locked UI): rotate a bitmap.
                val src = image.toBitmap()
                val rotated = Bitmap.createBitmap(src, 0, 0, w, h, Matrix().apply { postRotate(rotation.toFloat()) }, true)
                rotated.compress(Bitmap.CompressFormat.JPEG, CamState.quality, jpegBuffer)
                CamState.frameWidth = h
                CamState.frameHeight = w
            }
            val jpeg = jpegBuffer.toByteArray()
            hub.publish(jpeg)
            preview?.invoke(jpeg)
            frameCount++
            encodeNanos += System.nanoTime() - t0
        } catch (e: Exception) {
            Log.w(TAG, "encode failed", e)
        } finally {
            image.close()
        }
    }

    /** Packs a YUV_420_888 image into a reusable NV21 buffer (Y plane, then interleaved V/U). */
    private fun toNv21(image: ImageProxy): ByteArray {
        val w = image.width
        val h = image.height
        val ySize = w * h
        val out = nv21Buffer?.takeIf { it.size == ySize * 3 / 2 } ?: ByteArray(ySize * 3 / 2).also { nv21Buffer = it }
        val (yPlane, uPlane, vPlane) = image.planes

        val yBuf = yPlane.buffer
        if (yPlane.rowStride == w) {
            yBuf.position(0)
            yBuf.get(out, 0, ySize)
        } else {
            for (row in 0 until h) {
                yBuf.position(row * yPlane.rowStride)
                yBuf.get(out, row * w, w)
            }
        }

        val uBuf = uPlane.buffer
        val vBuf = vPlane.buffer
        if (vPlane.pixelStride == 2 && uPlane.pixelStride == 2 && vPlane.rowStride == w && uPlane.rowStride == w) {
            // Semi-planar already: the V buffer is V,U,V,U... minus the final U byte.
            vBuf.position(0)
            val n = minOf(vBuf.remaining(), ySize / 2)
            vBuf.get(out, ySize, n)
            if (n < ySize / 2) out[out.size - 1] = uBuf.get(uBuf.limit() - 1)
        } else {
            var o = ySize
            for (row in 0 until h / 2) {
                for (col in 0 until w / 2) {
                    out[o++] = vBuf.get(row * vPlane.rowStride + col * vPlane.pixelStride)
                    out[o++] = uBuf.get(row * uPlane.rowStride + col * uPlane.pixelStride)
                }
            }
        }
        return out
    }

    private fun rotate180(d: ByteArray, w: Int, h: Int) {
        var i = 0
        var j = w * h - 1
        while (i < j) {
            val t = d[i]; d[i] = d[j]; d[j] = t
            i++; j--
        }
        var a = w * h
        var b = d.size - 2
        while (a < b) {
            val v = d[a]; d[a] = d[b]; d[b] = v
            val u = d[a + 1]; d[a + 1] = d[b + 1]; d[b + 1] = u
            a += 2; b -= 2
        }
    }

    // ---- Settings (main thread) -------------------------------------------

    fun setFront(front: Boolean) {
        if (front == CamState.front) return
        CamState.front = front
        prefs.edit().putBoolean("front", front).apply()
        bindCamera()
    }

    fun setTorch(on: Boolean) {
        val cam = camera ?: return
        if (!cam.cameraInfo.hasFlashUnit()) return
        CamState.torch = on
        cam.cameraControl.enableTorch(on)
        CamState.changed()
    }

    fun setResolution(index: Int) {
        if (index !in CamState.RESOLUTIONS.indices || index == CamState.resIndex) return
        CamState.resIndex = index
        prefs.edit().putInt("res", index).apply()
        bindCamera()
    }

    fun setQuality(q: Int) {
        CamState.quality = q.coerceIn(30, 100)
        prefs.edit().putInt("quality", CamState.quality).apply()
        CamState.changed()
    }

    fun setZoom(value: Float) {
        CamState.zoom = value.coerceIn(0f, 1f)
        camera?.cameraControl?.setLinearZoom(CamState.zoom)
        CamState.changed()
    }

    private fun applyExposure() {
        val state = camera?.cameraInfo?.exposureState ?: return
        if (!state.isExposureCompensationSupported) {
            CamState.exposureMin = 0f
            CamState.exposureMax = 0f
            CamState.exposure = 0f
            return
        }
        val step = state.exposureCompensationStep.toFloat()
        val range = state.exposureCompensationRange
        CamState.exposureMin = range.lower * step
        CamState.exposureMax = range.upper * step
        val index = Math.round(CamState.exposure / step).coerceIn(range.lower, range.upper)
        CamState.exposure = index * step
        camera?.cameraControl?.setExposureCompensationIndex(index)
    }

    fun setExposure(ev: Float) {
        CamState.exposure = ev
        applyExposure()
        prefs.edit().putFloat("exposure", CamState.exposure).apply()
        CamState.changed()
    }

    fun setNight(on: Boolean) {
        if (CamState.night == on) return
        CamState.night = on
        prefs.edit().putBoolean("night", on).apply()
        bindCamera()
    }

    fun setMirror(on: Boolean) {
        CamState.mirror = on
        prefs.edit().putBoolean("mirror", on).apply()
        CamState.changed()
    }

    fun setBlackout(on: Boolean) {
        if (CamState.blackout == on) return
        CamState.blackout = on
        CamState.changed()
    }

    // ---- StreamServer.Controller (network threads) ------------------------

    override fun statusJson(): String = CamState.toJson(deviceName)

    override fun awaitChange(since: Long, timeoutMs: Long) = CamState.awaitChange(since, timeoutMs)

    override fun onClientsChanged(count: Int) {
        CamState.clients = count
        CamState.changed()
    }

    override fun control(params: Map<String, String>) {
        main.post {
            params["camera"]?.let {
                when (it) {
                    "front" -> setFront(true)
                    "back" -> setFront(false)
                    "toggle" -> setFront(!CamState.front)
                }
            }
            params["torch"]?.let { setTorch(parseBool(it, CamState.torch)) }
            params["mirror"]?.let { setMirror(parseBool(it, CamState.mirror)) }
            params["blackout"]?.let { setBlackout(parseBool(it, CamState.blackout)) }
            params["night"]?.let { setNight(parseBool(it, CamState.night)) }
            params["exposure"]?.toFloatOrNull()?.let { setExposure(it) }
            params["res"]?.let { r ->
                val idx = CamState.RESOLUTIONS.indexOfFirst { "${it.width}x${it.height}" == r }
                if (idx >= 0) setResolution(idx)
            }
            params["quality"]?.toIntOrNull()?.let { setQuality(it) }
            params["zoom"]?.toFloatOrNull()?.let { setZoom(it) }
        }
    }

    private fun parseBool(v: String, current: Boolean): Boolean = when (v) {
        "on", "1", "true" -> true
        "off", "0", "false" -> false
        "toggle" -> !current
        else -> current
    }

    companion object {
        private const val TAG = "CamTelService"
        private const val CHANNEL = "camtel"
        private const val NOTIF_ID = 1
        const val HTTP_PORT = 4747
        const val ACTION_STOP = "io.github.ghermin.camtel.STOP"
        const val ACTION_FINISH = "io.github.ghermin.camtel.FINISH"
    }
}
