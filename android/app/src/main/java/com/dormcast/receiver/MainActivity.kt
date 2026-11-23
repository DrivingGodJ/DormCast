package com.dormcast.receiver

import android.Manifest
import android.annotation.SuppressLint
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.webkit.JavascriptInterface
import android.webkit.PermissionRequest
import android.webkit.WebChromeClient
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors

class MainActivity : AppCompatActivity() {

    private lateinit var homeContainer: View
    private lateinit var playerContainer: View
    private lateinit var webView: WebView
    private lateinit var inputJoinUrl: EditText
    private lateinit var savedRoomsList: LinearLayout
    private lateinit var discoveredList: LinearLayout
    private lateinit var discoveredHint: TextView
    private lateinit var txtCurrentRoom: TextView
    private lateinit var btnBatteryOptimize: Button

    private lateinit var discovery: LanDiscovery
    private val probePool = Executors.newCachedThreadPool()
    private val roomOnlineMap = mutableMapOf<String, Boolean>()
    private var discoveredHosts: List<DiscoveredHost> = emptyList()
    private var currentRoom: SavedRoom? = null

    private val scanLauncher = registerForActivityResult(ScanContract()) { result ->
        val raw = result.contents ?: return@registerForActivityResult
        handleJoinUrlInput(raw)
    }

    private val notifPermLauncher = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    private val toggleReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.action == PlaybackForegroundService.BROADCAST_TOGGLE_PLAYBACK) {
                webView.evaluateJavascript("window.DormCastPlayer && window.DormCastPlayer.toggle();", null)
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        homeContainer = findViewById(R.id.homeContainer)
        playerContainer = findViewById(R.id.playerContainer)
        webView = findViewById(R.id.webView)
        inputJoinUrl = findViewById(R.id.inputJoinUrl)
        savedRoomsList = findViewById(R.id.savedRoomsList)
        discoveredList = findViewById(R.id.discoveredList)
        discoveredHint = findViewById(R.id.discoveredHint)
        txtCurrentRoom = findViewById(R.id.txtCurrentRoom)
        btnBatteryOptimize = findViewById(R.id.btnBatteryOptimize)

        setupWebView()
        requestNotificationPermissionIfNeeded()

        findViewById<Button>(R.id.btnScanQr).setOnClickListener { launchQrScanner() }
        findViewById<Button>(R.id.btnConnectUrl).setOnClickListener {
            handleJoinUrlInput(inputJoinUrl.text.toString())
        }
        findViewById<Button>(R.id.btnRefreshLan).setOnClickListener { refreshAllSources() }
        findViewById<Button>(R.id.btnBackRooms).setOnClickListener { leaveRoomToHome() }
        findViewById<Button>(R.id.btnReloadPage).setOnClickListener { webView.reload() }
        btnBatteryOptimize.setOnClickListener { requestIgnoreBatteryOptimization() }

        discovery = LanDiscovery(this) { hosts ->
            discoveredHosts = hosts
            renderDiscoveredHosts()
        }

        val filter = IntentFilter(PlaybackForegroundService.BROADCAST_TOGGLE_PLAYBACK)
        ContextCompat.registerReceiver(this, toggleReceiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (playerContainer.visibility == View.VISIBLE) {
                    // Keep music playing in background when pressing Home/Back on player screen?
                    // Return to room picker cleanly; user can press Home to minimize while playing.
                    leaveRoomToHome()
                } else {
                    isEnabled = false
                    onBackPressedDispatcher.onBackPressed()
                }
            }
        })

        renderSavedRooms()
        refreshAllSources()
    }

    override fun onResume() {
        super.onResume()
        updateBatteryButtonState()
        if (homeContainer.visibility == View.VISIBLE) {
            discovery.start()
            probeSavedRooms()
        }
    }

    override fun onPause() {
        super.onPause()
        // Intentionally DO NOT call webView.onPause() or pauseTimers() so background / screen-off
        // WebRTC audio and host synchronization stay alive under PlaybackForegroundService.
        if (homeContainer.visibility == View.VISIBLE) {
            discovery.stop()
        }
    }

    override fun onDestroy() {
        discovery.stop()
        runCatching { unregisterReceiver(toggleReceiver) }
        probePool.shutdownNow()
        stopService(Intent(this, PlaybackForegroundService::class.java))
        webView.destroy()
        super.onDestroy()
    }

    @SuppressLint("SetJavaScriptEnabled")
    private fun setupWebView() {
        webView.setBackgroundColor(Color.parseColor("#101612"))
        webView.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            mediaPlaybackRequiresUserGesture = false
            cacheMode = WebSettings.LOAD_NO_CACHE
            mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
            userAgentString = "$userAgentString DormCastAndroid/0.7.1"
        }
        webView.addJavascriptInterface(DormCastBridge(), "DormCastAndroid")
        webView.webViewClient = object : WebViewClient() {}
        webView.webChromeClient = object : WebChromeClient() {
            override fun onPermissionRequest(request: PermissionRequest) {
                request.grant(request.resources)
            }
        }
    }

    private fun launchQrScanner() {
        val options = ScanOptions().apply {
            setDesiredBarcodeFormats(ScanOptions.QR_CODE)
            setPrompt("请扫描电脑端「宿舍之音」二维码（需连接同一 Wi-Fi）")
            setBeepEnabled(false)
            setOrientationLocked(true)
        }
        scanLauncher.launch(options)
    }

    private fun handleJoinUrlInput(raw: String) {
        val room = RoomStore.parseJoinUrl(raw)
        if (room == null) {
            Toast.makeText(this, "无效的加入链接，请扫描宿舍之音主机上的完整二维码（含 #join=...）", Toast.LENGTH_LONG).show()
            return
        }
        RoomStore.upsert(this, room)
        inputJoinUrl.setText("")
        openRoom(room)
    }

    private fun openRoom(room: SavedRoom) {
        currentRoom = room
        RoomStore.upsert(this, room.copy(lastUsedAt = System.currentTimeMillis()))
        discovery.stop()
        homeContainer.visibility = View.GONE
        playerContainer.visibility = View.VISIBLE
        txtCurrentRoom.text = "${room.name} (${room.host}:${room.port})"

        startForegroundPlaybackService(room.name, "正在连接局域网主机…", "", false)
        webView.loadUrl(room.joinUrl)
    }

    private fun leaveRoomToHome() {
        webView.loadUrl("about:blank")
        currentRoom = null
        stopService(Intent(this, PlaybackForegroundService::class.java))
        playerContainer.visibility = View.GONE
        homeContainer.visibility = View.VISIBLE
        renderSavedRooms()
        refreshAllSources()
    }

    private fun refreshAllSources() {
        discovery.stop()
        discovery.start()
        probeSavedRooms()
    }

    private fun probeSavedRooms() {
        val rooms = RoomStore.load(this)
        if (rooms.isEmpty()) {
            renderSavedRooms()
            return
        }
        for (room in rooms) {
            probePool.execute {
                val online = runCatching {
                    val conn = (URL("http://${room.host}:${room.port}/favicon.png").openConnection() as HttpURLConnection).apply {
                        connectTimeout = 1500
                        readTimeout = 1500
                        requestMethod = "GET"
                    }
                    try {
                        conn.responseCode in 200..299
                    } finally {
                        conn.disconnect()
                    }
                }.getOrDefault(false)
                roomOnlineMap[room.id] = online
                runOnUiThread { renderSavedRooms() }
            }
        }
    }

    private fun renderSavedRooms() {
        savedRoomsList.removeAllViews()
        val rooms = RoomStore.load(this)
        if (rooms.isEmpty()) {
            val empty = TextView(this).apply {
                text = "暂无已配对的播放源。点击上方「扫码配对加入广播」连接电脑主机。"
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.dc_muted))
                textSize = 13f
                setPadding(0, 8, 0, 8)
            }
            savedRoomsList.addView(empty)
            return
        }
        for (room in rooms) {
            val online = roomOnlineMap[room.id]
            val card = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setBackgroundColor(ContextCompat.getColor(this@MainActivity, R.color.dc_surface))
                setPadding(36, 28, 24, 28)
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT
                ).apply { bottomMargin = 20 }
                setOnClickListener { openRoom(room) }
                setOnLongClickListener {
                    AlertDialog.Builder(this@MainActivity)
                        .setTitle("移除已保存播放源")
                        .setMessage("确定移除 ${room.name} (${room.host}:${room.port}) 吗？")
                        .setPositiveButton("移除") { _, _ ->
                            RoomStore.remove(this@MainActivity, room.id)
                            renderSavedRooms()
                        }
                        .setNegativeButton("取消", null)
                        .show()
                    true
                }
            }
            val infoCol = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            }
            infoCol.addView(TextView(this).apply {
                text = room.name
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.dc_text))
                textSize = 15f
            })
            infoCol.addView(TextView(this).apply {
                text = "${room.host}:${room.port} · 长按可移除"
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.dc_muted))
                textSize = 12f
            })
            val statusBadge = TextView(this).apply {
                text = when (online) {
                    true -> "● 在线 · 进入"
                    false -> "离线"
                    null -> "探测中…"
                }
                setTextColor(
                    ContextCompat.getColor(
                        this@MainActivity,
                        if (online == true) R.color.dc_accent else R.color.dc_muted
                    )
                )
                textSize = 13f
            }
            card.addView(infoCol)
            card.addView(statusBadge)
            savedRoomsList.addView(card)
        }
    }

    private fun renderDiscoveredHosts() {
        discoveredList.removeAllViews()
        if (discoveredHosts.isEmpty()) {
            discoveredHint.text = "暂未发现局域网内新的宿舍之音主机（请确认电脑已点击「开始广播」且连接同一 Wi-Fi）。"
            return
        }
        discoveredHint.text = "在当前 Wi-Fi 下发现 ${discoveredHosts.size} 台宿舍之音主机："

        for (h in discoveredHosts) {
            val savedRoom = RoomStore.load(this).firstOrNull {
                it.host.equals(h.hostAddress, ignoreCase = true) && it.port == h.port
            }
            val card = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setBackgroundColor(ContextCompat.getColor(this@MainActivity, R.color.dc_raised))
                setPadding(36, 26, 24, 26)
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT
                ).apply { bottomMargin = 16 }
                setOnClickListener {
                    if (savedRoom != null) openRoom(savedRoom)
                    else launchQrScanner()
                }
            }
            val infoCol = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            }
            infoCol.addView(TextView(this).apply {
                text = savedRoom?.name ?: h.serviceName
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.dc_text))
                textSize = 14f
            })
            infoCol.addView(TextView(this).apply {
                text = "${h.hostAddress}:${h.port}"
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.dc_muted))
                textSize = 12f
            })
            card.addView(infoCol)
            card.addView(TextView(this).apply {
                text = if (savedRoom != null) "已配对 · 进入" else "扫码配对 ↗"
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.dc_accent))
                textSize = 13f
            })
            discoveredList.addView(card)
        }
    }

    private fun startForegroundPlaybackService(title: String, artist: String, album: String, playing: Boolean) {
        val intent = Intent(this, PlaybackForegroundService::class.java).apply {
            action = PlaybackForegroundService.ACTION_UPDATE
            putExtra(PlaybackForegroundService.EXTRA_TITLE, title)
            putExtra(PlaybackForegroundService.EXTRA_ARTIST, artist)
            putExtra(PlaybackForegroundService.EXTRA_ALBUM, album)
            putExtra(PlaybackForegroundService.EXTRA_PLAYING, playing)
        }
        ContextCompat.startForegroundService(this, intent)
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                notifPermLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }

    private fun updateBatteryButtonState() {
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        val ignored = pm.isIgnoringBatteryOptimizations(packageName)
        btnBatteryOptimize.text = if (ignored) {
            "✓ 已加入系统电池优化白名单（后台保活已开启）"
        } else {
            "开启后台保活白名单（澎湃OS / MIUI 推荐）"
        }
    }

    @SuppressLint("BatteryLife")
    private fun requestIgnoreBatteryOptimization() {
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        if (pm.isIgnoringBatteryOptimizations(packageName)) {
            Toast.makeText(this, "已处于电池优化白名单中", Toast.LENGTH_SHORT).show()
            return
        }
        runCatching {
            val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                data = Uri.parse("package:$packageName")
            }
            startActivity(intent)
        }.onFailure {
            startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
        }
    }

    inner class DormCastBridge {
        @JavascriptInterface
        fun onMediaUpdate(title: String?, artist: String?, album: String?, playbackState: String?) {
            val safeTitle = title?.takeIf { it.isNotBlank() } ?: "宿舍之音"
            val safeArtist = artist?.takeIf { it.isNotBlank() } ?: "局域网实时广播"
            val safeAlbum = album.orEmpty()
            val isPlaying = playbackState == "playing"
            runOnUiThread {
                currentRoom?.let { room ->
                    if (safeTitle != "正在广播系统声音" && safeTitle != "宿舍之音") {
                        txtCurrentRoom.text = safeTitle
                    }
                    // Update room name if web title reported station name
                    val pageTitle = webView.title?.removeSuffix(" · 一起听")?.trim()
                    if (!pageTitle.isNullOrBlank() && pageTitle != room.name) {
                        RoomStore.updateName(this@MainActivity, room.id, pageTitle)
                        currentRoom = room.copy(name = pageTitle)
                    }
                }
                startForegroundPlaybackService(safeTitle, safeArtist, safeAlbum, isPlaying)
            }
        }

        @JavascriptInterface
        fun onCoverUpdate(dataUrl: String?) {
            val intent = Intent(this@MainActivity, PlaybackForegroundService::class.java).apply {
                action = PlaybackForegroundService.ACTION_COVER
                putExtra(PlaybackForegroundService.EXTRA_COVER_DATA_URL, dataUrl.orEmpty())
            }
            startService(intent)
        }
    }
}
