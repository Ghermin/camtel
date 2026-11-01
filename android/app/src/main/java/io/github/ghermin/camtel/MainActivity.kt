package io.github.ghermin.camtel

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.hardware.display.DisplayManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.util.Size
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.camera.core.AspectRatio
import androidx.camera.core.Camera
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.core.resolutionselector.AspectRatioStrategy
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import io.github.ghermin.camtel.databinding.ActivityMainBinding
import java.io.ByteArrayOutputStream
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

class MainActivity : AppCompatActivity(), StreamServer.Controller {

    private lateinit var binding: ActivityMainBinding
    private lateinit var analysisExecutor: ExecutorService
    private val mainHandler = Handler(Looper.getMainLooper())
    private val prefs by lazy { getSharedPreferences("camtel", Context.MODE_PRIVATE) }

    private val hub = FrameHub()
    private var server: StreamServer? = null
    private var discovery: Discovery? = null

    private var cameraProvider: ProcessCameraProvider? = null
    private var camera: Camera? = null
    private var preview: Preview? = null
    private var analysis: ImageAnalysis? = null

    @Volatile private var useFront = false
    @Volatile private var torchOn = false
    @Volatile private var resolutionIndex = 1
    @Volatile private var quality = 80
    @Volatile private var zoom = 0f
    @Volatile private var frameWidth = 0
    @Volatile private var frameHeight = 0
    @Volatile private var fps = 0
    private var frameCount = 0
    private var fpsWindowStart = System.nanoTime()

    private val jpegBuffer = ByteArrayOutputStream(512 * 1024)
    private var reusableBitmap: Bitmap? = null

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) startCamera() else {
                Toast.makeText(this, R.string.permission_needed, Toast.LENGTH_LONG).show()
                finish()
            }
        }

    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) {}
        override fun onDisplayRemoved(displayId: Int) {}
        override fun onDisplayChanged(displayId: Int) {
            val rotation = binding.preview.display?.rotation ?: return
            preview?.targetRotation = rotation
            analysis?.targetRotation = rotation
        }
    }

    private val infoUpdater = object : Runnable {
        override fun run() {
            updateInfo()
            mainHandler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        hideSystemBars()

        useFront = prefs.getBoolean("front", false)
        resolutionIndex = prefs.getInt("res", 1).coerceIn(RESOLUTIONS.indices)
        quality = prefs.getInt("quality", 80).coerceIn(30, 100)

        analysisExecutor = Executors.newSingleThreadExecutor()

        server = StreamServer(HTTP_PORT, hub, this).also {
            try {
                it.start()
            } catch (e: Exception) {
                Log.e(TAG, "server start failed", e)
                Toast.makeText(this, "Port $HTTP_PORT indisponible", Toast.LENGTH_LONG).show()
            }
        }
        discovery = Discovery(deviceName(), HTTP_PORT).also { it.start() }

        binding.btnSwitch.setOnClickListener { setFront(!useFront) }
        binding.btnTorch.setOnClickListener { setTorch(!torchOn) }
        binding.btnRes.setOnClickListener { setResolution((resolutionIndex + 1) % RESOLUTIONS.size) }
        binding.btnBlack.setOnClickListener { setBlackout(true) }
        binding.blackout.setOnClickListener { setBlackout(false) }

        (getSystemService(Context.DISPLAY_SERVICE) as DisplayManager)
            .registerDisplayListener(displayListener, mainHandler)

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) ==
            PackageManager.PERMISSION_GRANTED
        ) startCamera() else permissionLauncher.launch(Manifest.permission.CAMERA)
    }

    override fun onResume() {
        super.onResume()
        mainHandler.post(infoUpdater)
    }

    override fun onPause() {
        super.onPause()
        mainHandler.removeCallbacks(infoUpdater)
    }

    override fun onDestroy() {
        super.onDestroy()
        (getSystemService(Context.DISPLAY_SERVICE) as DisplayManager)
            .unregisterDisplayListener(displayListener)
        discovery?.stop()
        server?.stop()
        analysisExecutor.shutdown()
    }

    private fun hideSystemBars() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, binding.root).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    // ---- Camera -----------------------------------------------------------

    private fun startCamera() {
        val future = ProcessCameraProvider.getInstance(this)
        future.addListener({
            cameraProvider = future.get()
            bindCamera()
        }, ContextCompat.getMainExecutor(this))
    }

    private fun bindCamera() {
        val provider = cameraProvider ?: return
        val size = RESOLUTIONS[resolutionIndex]
        val aspect = if (size.width * 3 == size.height * 4) AspectRatio.RATIO_4_3 else AspectRatio.RATIO_16_9
        val selector = ResolutionSelector.Builder()
            .setAspectRatioStrategy(AspectRatioStrategy(aspect, AspectRatioStrategy.FALLBACK_RULE_AUTO))
            .setResolutionStrategy(
                ResolutionStrategy(size, ResolutionStrategy.FALLBACK_RULE_CLOSEST_LOWER_THEN_HIGHER)
            )
            .build()
        val rotation = binding.preview.display?.rotation ?: windowManager.defaultDisplay.rotation

        val newPreview = Preview.Builder()
            .setResolutionSelector(selector)
            .setTargetRotation(rotation)
            .build()
            .also { it.surfaceProvider = binding.preview.surfaceProvider }

        val newAnalysis = ImageAnalysis.Builder()
            .setResolutionSelector(selector)
            .setTargetRotation(rotation)
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
            .setOutputImageRotationEnabled(true)
            .build()
            .also { it.setAnalyzer(analysisExecutor, ::encodeFrame) }

        val cameraSelector = if (useFront) CameraSelector.DEFAULT_FRONT_CAMERA else CameraSelector.DEFAULT_BACK_CAMERA
        try {
            provider.unbindAll()
            camera = provider.bindToLifecycle(this, cameraSelector, newPreview, newAnalysis)
            preview = newPreview
            analysis = newAnalysis
            torchOn = false
            camera?.cameraControl?.setLinearZoom(zoom)
        } catch (e: Exception) {
            Log.e(TAG, "bind failed", e)
            if (useFront) {
                // Device without a usable front camera: fall back to the back one.
                useFront = false
                bindCamera()
            }
        }
    }

    private fun encodeFrame(image: ImageProxy) {
        try {
            frameWidth = image.width
            frameHeight = image.height
            if ((server?.clientCount ?: 0) == 0) return

            val plane = image.planes[0]
            val bitmap: Bitmap
            if (plane.rowStride == image.width * 4) {
                var bmp = reusableBitmap
                if (bmp == null || bmp.width != image.width || bmp.height != image.height) {
                    bmp = Bitmap.createBitmap(image.width, image.height, Bitmap.Config.ARGB_8888)
                    reusableBitmap = bmp
                }
                plane.buffer.rewind()
                bmp.copyPixelsFromBuffer(plane.buffer)
                bitmap = bmp
            } else {
                bitmap = image.toBitmap()
            }

            jpegBuffer.reset()
            bitmap.compress(Bitmap.CompressFormat.JPEG, quality, jpegBuffer)
            hub.publish(jpegBuffer.toByteArray())

            frameCount++
            val now = System.nanoTime()
            if (now - fpsWindowStart >= 1_000_000_000L) {
                fps = frameCount
                frameCount = 0
                fpsWindowStart = now
            }
        } catch (e: Exception) {
            Log.w(TAG, "encode failed", e)
        } finally {
            image.close()
        }
    }

    // ---- Controls ---------------------------------------------------------

    private fun setFront(front: Boolean) {
        if (front == useFront) return
        useFront = front
        prefs.edit().putBoolean("front", front).apply()
        bindCamera()
    }

    private fun setTorch(on: Boolean) {
        val cam = camera ?: return
        if (!cam.cameraInfo.hasFlashUnit()) {
            Toast.makeText(this, "Pas de flash sur cette caméra", Toast.LENGTH_SHORT).show()
            return
        }
        torchOn = on
        cam.cameraControl.enableTorch(on)
    }

    private fun setResolution(index: Int) {
        if (index !in RESOLUTIONS.indices || index == resolutionIndex) return
        resolutionIndex = index
        prefs.edit().putInt("res", index).apply()
        bindCamera()
        val s = RESOLUTIONS[index]
        Toast.makeText(this, "${s.width}x${s.height}", Toast.LENGTH_SHORT).show()
    }

    private fun setZoom(value: Float) {
        zoom = value.coerceIn(0f, 1f)
        camera?.cameraControl?.setLinearZoom(zoom)
    }

    private fun setBlackout(on: Boolean) {
        binding.blackout.visibility = if (on) android.view.View.VISIBLE else android.view.View.GONE
        window.attributes = window.attributes.apply {
            screenBrightness = if (on) 0.01f else WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
        }
    }

    private fun updateInfo() {
        val ips = Discovery.localIpv4Addresses()
        val clients = server?.clientCount ?: 0
        val addr = if (ips.isEmpty()) "Pas de réseau (USB possible)" else ips.joinToString("\n") { "$it:$HTTP_PORT" }
        val state = if (clients > 0) "● $clients PC connecté(s) · $fps fps" else "○ En attente du PC…"
        binding.info.text = "$addr\n$state · ${frameWidth}x$frameHeight"
    }

    private fun deviceName(): String =
        (Build.MODEL ?: "Android").replace('|', ' ')

    // ---- StreamServer.Controller (called from network threads) -----------

    override fun statusJson(): String {
        val s = RESOLUTIONS[resolutionIndex]
        return """{"app":"CamTel","name":"${deviceName()}","camera":"${if (useFront) "front" else "back"}",""" +
            """"torch":$torchOn,"resolution":"${s.width}x${s.height}","width":$frameWidth,"height":$frameHeight,""" +
            """"quality":$quality,"zoom":$zoom,"fps":$fps,"clients":${server?.clientCount ?: 0}}"""
    }

    override fun control(params: Map<String, String>) {
        mainHandler.post {
            params["camera"]?.let {
                when (it) {
                    "front" -> setFront(true)
                    "back" -> setFront(false)
                    "toggle" -> setFront(!useFront)
                }
            }
            params["torch"]?.let {
                when (it) {
                    "on", "1", "true" -> setTorch(true)
                    "off", "0", "false" -> setTorch(false)
                    "toggle" -> setTorch(!torchOn)
                }
            }
            params["res"]?.let { r ->
                val idx = RESOLUTIONS.indexOfFirst { "${it.width}x${it.height}" == r }
                if (idx >= 0) setResolution(idx)
            }
            params["quality"]?.toIntOrNull()?.let {
                quality = it.coerceIn(30, 100)
                prefs.edit().putInt("quality", quality).apply()
            }
            params["zoom"]?.toFloatOrNull()?.let { setZoom(it) }
        }
    }

    companion object {
        private const val TAG = "CamTel"
        const val HTTP_PORT = 4747
        val RESOLUTIONS = listOf(Size(640, 480), Size(1280, 720), Size(1920, 1080))
    }
}
