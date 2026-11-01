package io.github.ghermin.camtel

import android.Manifest
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.hardware.display.DisplayManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.view.View
import android.view.WindowManager
import android.widget.SeekBar
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import io.github.ghermin.camtel.databinding.ActivityMainBinding
import java.util.concurrent.atomic.AtomicBoolean

/** UI only: shows the preview and the controls. The camera itself lives in [CameraService]. */
class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private val mainHandler = Handler(Looper.getMainLooper())
    private var service: CameraService? = null
    private var bound = false

    private val stateListener: () -> Unit = { render() }

    // Double-buffered decode of the streamed JPEGs (called on the camera thread).
    private val previewBitmaps = arrayOfNulls<Bitmap>(2)
    private var previewIndex = 0
    private val drawPending = AtomicBoolean(false)
    private val previewSink: (ByteArray) -> Unit = sink@{ jpeg ->
        if (!drawPending.compareAndSet(false, true)) return@sink
        try {
            val opts = BitmapFactory.Options().apply {
                // The phone only needs a light preview; the PC still gets the full-quality frame.
                inSampleSize = if (CamState.frameWidth >= 1280) 2 else 1
                inPreferredConfig = Bitmap.Config.RGB_565
                inMutable = true
                inBitmap = previewBitmaps[previewIndex]
            }
            val bmp = try {
                BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size, opts)
            } catch (_: IllegalArgumentException) {
                // Size changed: the reusable bitmap no longer fits.
                opts.inBitmap = null
                BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size, opts)
            }
            if (bmp == null) {
                drawPending.set(false)
                return@sink
            }
            previewBitmaps[previewIndex] = bmp
            previewIndex = 1 - previewIndex
            mainHandler.post {
                binding.preview.setImageBitmap(bmp)
                drawPending.set(false)
            }
        } catch (_: Exception) {
            drawPending.set(false)
        }
    }

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            service = (binder as CameraService.LocalBinder).service
            attachPreview()
            render()
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            service = null
        }
    }

    private val finishReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) = finish()
    }

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
            if (result[Manifest.permission.CAMERA] == true) startAndBind() else {
                Toast.makeText(this, R.string.permission_needed, Toast.LENGTH_LONG).show()
                finish()
            }
        }

    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) {}
        override fun onDisplayRemoved(displayId: Int) {}
        override fun onDisplayChanged(displayId: Int) = attachPreview()
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

        binding.btnSwitch.setOnClickListener { service?.setFront(!CamState.front) }
        binding.btnTorch.setOnClickListener { service?.setTorch(!CamState.torch) }
        binding.btnMirror.setOnClickListener { service?.setMirror(!CamState.mirror) }
        binding.btnRes.setOnClickListener {
            service?.setResolution((CamState.resIndex + 1) % CamState.RESOLUTIONS.size)
        }
        binding.btnFps.setOnClickListener { service?.setNight(!CamState.night) }
        binding.btnBlack.setOnClickListener { service?.setBlackout(true) }
        binding.blackout.setOnClickListener { service?.setBlackout(false) }
        binding.btnQuit.setOnClickListener {
            service?.stopEverything() ?: finish()
        }
        binding.zoom.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar?, progress: Int, fromUser: Boolean) {
                if (fromUser) service?.setZoom(progress / 100f)
            }
            override fun onStartTrackingTouch(sb: SeekBar?) {}
            override fun onStopTrackingTouch(sb: SeekBar?) {}
        })

        binding.exposure.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar?, progress: Int, fromUser: Boolean) {
                if (!fromUser) return
                val ev = CamState.exposureMin + (CamState.exposureMax - CamState.exposureMin) * progress / 100f
                service?.setExposure(ev)
            }
            override fun onStartTrackingTouch(sb: SeekBar?) {}
            override fun onStopTrackingTouch(sb: SeekBar?) {}
        })

        ContextCompat.registerReceiver(
            this, finishReceiver, IntentFilter(CameraService.ACTION_FINISH), ContextCompat.RECEIVER_NOT_EXPORTED
        )
        (getSystemService(Context.DISPLAY_SERVICE) as DisplayManager)
            .registerDisplayListener(displayListener, mainHandler)

        val needed = mutableListOf(Manifest.permission.CAMERA)
        if (Build.VERSION.SDK_INT >= 33) needed += Manifest.permission.POST_NOTIFICATIONS
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            startAndBind()
            if (Build.VERSION.SDK_INT >= 33 &&
                ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) !=
                PackageManager.PERMISSION_GRANTED
            ) permissionLauncher.launch(arrayOf(Manifest.permission.POST_NOTIFICATIONS))
        } else {
            permissionLauncher.launch(needed.toTypedArray())
        }
    }

    override fun onStart() {
        super.onStart()
        CamState.addListener(stateListener)
        attachPreview()
        render()
    }

    override fun onResume() {
        super.onResume()
        mainHandler.post(infoUpdater)
    }

    override fun onPause() {
        super.onPause()
        mainHandler.removeCallbacks(infoUpdater)
    }

    override fun onStop() {
        super.onStop()
        CamState.removeListener(stateListener)
        // The view is going away; keep streaming without a preview surface.
        service?.attachPreview(null, null)
        // Leaving the app while the screen was "black" should not leave it black.
        if (CamState.blackout) service?.setBlackout(false)
    }

    override fun onDestroy() {
        super.onDestroy()
        unregisterReceiver(finishReceiver)
        (getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).unregisterDisplayListener(displayListener)
        if (bound) unbindService(connection)
    }

    private fun startAndBind() {
        val intent = Intent(this, CameraService::class.java)
        ContextCompat.startForegroundService(this, intent)
        bound = bindService(intent, connection, Context.BIND_AUTO_CREATE)
    }

    private fun attachPreview() {
        val s = service ?: return
        if (!lifecycle.currentState.isAtLeast(androidx.lifecycle.Lifecycle.State.STARTED)) return
        s.attachPreview(previewSink, binding.root.display?.rotation)
    }

    private fun hideSystemBars() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, binding.root).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    /** Reflects [CamState] in the UI; called on every state change (phone or PC). */
    private fun render() {
        binding.btnSwitch.isActivated = CamState.front
        binding.btnTorch.isActivated = CamState.torch
        binding.btnTorch.setImageResource(if (CamState.torch) R.drawable.ic_flash_on else R.drawable.ic_flash_off)
        binding.btnTorch.isEnabled = CamState.hasFlash
        binding.btnTorch.alpha = if (CamState.hasFlash) 1f else 0.35f
        binding.btnMirror.isActivated = CamState.mirror
        binding.btnRes.text = "${CamState.resolution.height}p"
        binding.btnBlack.isActivated = CamState.blackout
        val z = (CamState.zoom * 100).toInt()
        if (binding.zoom.progress != z) binding.zoom.progress = z
        binding.zoomLabel.text = "$z%"
        binding.btnFps.text = if (CamState.night) "Nuit" else "30 ips"
        binding.btnFps.isActivated = CamState.night
        val span = CamState.exposureMax - CamState.exposureMin
        binding.exposure.isEnabled = span > 0f
        val e = if (span > 0f) ((CamState.exposure - CamState.exposureMin) / span * 100).toInt() else 50
        if (binding.exposure.progress != e) binding.exposure.progress = e
        binding.exposureLabel.text = String.format(java.util.Locale.FRANCE, "%+.1f EV", CamState.exposure)

        // The stream itself is never mirrored; the PC mirrors it when the setting is on, so do the same here.
        binding.preview.scaleX = if (CamState.mirror) -1f else 1f

        binding.blackout.visibility = if (CamState.blackout) View.VISIBLE else View.GONE
        window.attributes = window.attributes.apply {
            screenBrightness =
                if (CamState.blackout) 0.01f else WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
        }
        updateInfo()
    }

    private fun updateInfo() {
        val ips = Discovery.localIpv4Addresses()
        val port = CameraService.HTTP_PORT
        val addr = if (ips.isEmpty()) "Pas de Wi-Fi · USB possible" else ips.joinToString("\n") { "$it:$port" }
        val state = if (CamState.clients > 0) "● PC connecté · ${CamState.fps} ips" else "○ En attente du PC…"
        binding.info.text = "$addr\n$state · ${CamState.frameWidth}×${CamState.frameHeight}"
    }
}
