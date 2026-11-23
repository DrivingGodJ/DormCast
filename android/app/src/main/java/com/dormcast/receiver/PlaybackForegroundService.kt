package com.dormcast.receiver

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.support.v4.media.MediaMetadataCompat
import android.support.v4.media.session.MediaSessionCompat
import android.support.v4.media.session.PlaybackStateCompat
import android.util.Base64
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.media.app.NotificationCompat.MediaStyle

class PlaybackForegroundService : Service() {

    companion object {
        const val CHANNEL_ID = "dormcast_playback"
        const val NOTIFICATION_ID = 1001
        const val ACTION_START = "com.dormcast.receiver.ACTION_START"
        const val ACTION_UPDATE = "com.dormcast.receiver.ACTION_UPDATE"
        const val ACTION_COVER = "com.dormcast.receiver.ACTION_COVER"
        const val ACTION_TOGGLE = "com.dormcast.receiver.ACTION_TOGGLE"
        const val ACTION_STOP = "com.dormcast.receiver.ACTION_STOP"
        const val BROADCAST_TOGGLE_PLAYBACK = "com.dormcast.receiver.BROADCAST_TOGGLE"

        const val EXTRA_TITLE = "title"
        const val EXTRA_ARTIST = "artist"
        const val EXTRA_ALBUM = "album"
        const val EXTRA_PLAYING = "playing"
        const val EXTRA_COVER_DATA_URL = "cover"
    }

    private lateinit var mediaSession: MediaSessionCompat
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var audioManager: AudioManager? = null
    private var focusRequest: AudioFocusRequest? = null

    private var title = "宿舍之音"
    private var artist = "局域网实时广播"
    private var album = ""
    private var playing = false
    private var coverBitmap: Bitmap? = null

    override fun onCreate() {
        super.onCreate()
        createChannel()
        audioManager = getSystemService(Context.AUDIO_SERVICE) as AudioManager
        mediaSession = MediaSessionCompat(this, "DormCastReceiver").apply {
            setCallback(object : MediaSessionCompat.Callback() {
                override fun onPlay() { broadcastToggle() }
                override fun onPause() { broadcastToggle() }
                override fun onStop() { stopSelf() }
            })
            isActive = true
        }
        acquireLocks()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_TOGGLE -> {
                broadcastToggle()
                return START_STICKY
            }
            ACTION_COVER -> {
                val dataUrl = intent.getStringExtra(EXTRA_COVER_DATA_URL).orEmpty()
                coverBitmap = decodeDataUrl(dataUrl)
                updateSessionAndNotification()
                return START_STICKY
            }
            ACTION_UPDATE, ACTION_START, null -> {
                intent?.getStringExtra(EXTRA_TITLE)?.takeIf { it.isNotBlank() }?.let { title = it }
                intent?.getStringExtra(EXTRA_ARTIST)?.takeIf { it.isNotBlank() }?.let { artist = it }
                intent?.getStringExtra(EXTRA_ALBUM)?.let { album = it }
                if (intent?.hasExtra(EXTRA_PLAYING) == true) {
                    playing = intent.getBooleanExtra(EXTRA_PLAYING, playing)
                }
                if (playing) requestAudioFocus()
                val notification = buildNotification()
                val fgType = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
                } else {
                    0
                }
                ServiceCompat.startForeground(
                    this,
                    NOTIFICATION_ID,
                    notification,
                    fgType
                )
                updateSessionMetadata()
                return START_STICKY
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        abandonAudioFocus()
        releaseLocks()
        mediaSession.isActive = false
        mediaSession.release()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun broadcastToggle() {
        sendBroadcast(Intent(BROADCAST_TOGGLE_PLAYBACK).setPackage(packageName))
    }

    private fun updateSessionAndNotification() {
        updateSessionMetadata()
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(NOTIFICATION_ID, buildNotification())
    }

    private fun updateSessionMetadata() {
        val meta = MediaMetadataCompat.Builder()
            .putString(MediaMetadataCompat.METADATA_KEY_TITLE, title)
            .putString(MediaMetadataCompat.METADATA_KEY_ARTIST, artist)
            .putString(MediaMetadataCompat.METADATA_KEY_ALBUM, album)
            .apply { coverBitmap?.let { putBitmap(MediaMetadataCompat.METADATA_KEY_ALBUM_ART, it) } }
            .build()
        mediaSession.setMetadata(meta)

        val state = if (playing) PlaybackStateCompat.STATE_PLAYING else PlaybackStateCompat.STATE_PAUSED
        val actions = PlaybackStateCompat.ACTION_PLAY or PlaybackStateCompat.ACTION_PAUSE or PlaybackStateCompat.ACTION_PLAY_PAUSE
        mediaSession.setPlaybackState(
            PlaybackStateCompat.Builder()
                .setActions(actions)
                .setState(state, PlaybackStateCompat.PLAYBACK_POSITION_UNKNOWN, if (playing) 1f else 0f)
                .build()
        )
    }

    private fun buildNotification(): Notification {
        val openIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val toggleIntent = PendingIntent.getService(
            this,
            1,
            Intent(this, PlaybackForegroundService::class.java).setAction(ACTION_TOGGLE),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val stopIntent = PendingIntent.getService(
            this,
            2,
            Intent(this, PlaybackForegroundService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val toggleLabel = if (playing) "暂停" else "播放"
        val toggleIcon = if (playing) android.R.drawable.ic_media_pause else android.R.drawable.ic_media_play

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_dormcast_stat)
            .setContentTitle(title)
            .setContentText(artist)
            .setSubText(if (playing) "宿舍之音 · 后台播放中" else "宿舍之音 · 已连接")
            .setLargeIcon(coverBitmap)
            .setContentIntent(openIntent)
            .setOngoing(playing)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .addAction(toggleIcon, toggleLabel, toggleIntent)
            .addAction(android.R.drawable.ic_menu_close_clear_cancel, "断开", stopIntent)
            .setStyle(
                MediaStyle()
                    .setMediaSession(mediaSession.sessionToken)
                    .setShowActionsInCompactView(0, 1)
            )
            .build()
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "宿舍之音后台播放",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "保持局域网音乐广播在息屏与后台稳定运行"
                setShowBadge(false)
            }
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            manager.createNotificationChannel(channel)
        }
    }

    private fun requestAudioFocus() {
        val manager = audioManager ?: return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            if (focusRequest == null) {
                focusRequest = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
                    .setAudioAttributes(
                        AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                            .build()
                    )
                    .setAcceptsDelayedFocusGain(true)
                    .setOnAudioFocusChangeListener { }
                    .build()
            }
            focusRequest?.let { manager.requestAudioFocus(it) }
        }
    }

    private fun abandonAudioFocus() {
        val manager = audioManager ?: return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            focusRequest?.let { manager.abandonAudioFocusRequest(it) }
        }
    }

    private fun acquireLocks() {
        runCatching {
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "DormCast::PlaybackWakeLock").apply {
                setReferenceCounted(false)
                acquire(12 * 60 * 60 * 1000L)
            }
        }
        runCatching {
            val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                WifiManager.WIFI_MODE_FULL_LOW_LATENCY
            } else {
                @Suppress("DEPRECATION")
                WifiManager.WIFI_MODE_FULL_HIGH_PERF
            }
            wifiLock = wifi.createWifiLock(mode, "DormCast::PlaybackWifiLock").apply {
                setReferenceCounted(false)
                acquire()
            }
        }
    }

    private fun releaseLocks() {
        wakeLock?.let { if (it.isHeld) runCatching { it.release() } }
        wakeLock = null
        wifiLock?.let { if (it.isHeld) runCatching { it.release() } }
        wifiLock = null
    }

    private fun decodeDataUrl(dataUrl: String): Bitmap? {
        val comma = dataUrl.indexOf(',')
        if (comma < 0 || !dataUrl.startsWith("data:image/")) return null
        return runCatching {
            val bytes = Base64.decode(dataUrl.substring(comma + 1), Base64.DEFAULT)
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
        }.getOrNull()
    }
}
