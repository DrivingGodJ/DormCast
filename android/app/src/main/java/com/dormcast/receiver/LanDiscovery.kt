package com.dormcast.receiver

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import java.util.concurrent.ConcurrentHashMap

data class DiscoveredHost(
    val serviceName: String,
    val hostAddress: String,
    val port: Int
)

class LanDiscovery(
    private val context: Context,
    private val onChanged: (List<DiscoveredHost>) -> Unit
) {
    private val mainHandler = Handler(Looper.getMainLooper())
    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private val hosts = ConcurrentHashMap<String, DiscoveredHost>()
    private var multicastLock: WifiManager.MulticastLock? = null
    private var listener: NsdManager.DiscoveryListener? = null
    private var running = false

    fun start() {
        if (running) return
        running = true
        hosts.clear()
        runCatching {
            val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            multicastLock = wifi.createMulticastLock("DormCastMdns").apply {
                setReferenceCounted(false)
                acquire()
            }
        }
        val discoveryListener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                val name = serviceInfo.serviceName ?: return
                if (!name.startsWith("dormcast-", ignoreCase = true)) return
                @Suppress("DEPRECATION")
                nsdManager.resolveService(serviceInfo, object : NsdManager.ResolveListener {
                    override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {}
                    override fun onServiceResolved(resolved: NsdServiceInfo) {
                        @Suppress("DEPRECATION")
                        val addr = resolved.host?.hostAddress ?: return
                        if (resolved.port <= 0) return
                        hosts[name] = DiscoveredHost(name, addr, resolved.port)
                        publish()
                    }
                })
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                val name = serviceInfo.serviceName ?: return
                if (hosts.remove(name) != null) publish()
            }

            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) { stop() }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) { stop() }
        }
        listener = discoveryListener
        runCatching {
            nsdManager.discoverServices("_http._tcp.", NsdManager.PROTOCOL_DNS_SD, discoveryListener)
        }.onFailure { stop() }
    }

    fun stop() {
        if (!running) return
        running = false
        listener?.let { runCatching { nsdManager.stopServiceDiscovery(it) } }
        listener = null
        multicastLock?.let { if (it.isHeld) runCatching { it.release() } }
        multicastLock = null
    }

    private fun publish() {
        val snapshot = hosts.values.sortedBy { it.serviceName }
        mainHandler.post { onChanged(snapshot) }
    }
}
