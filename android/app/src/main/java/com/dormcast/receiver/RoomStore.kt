package com.dormcast.receiver

import android.content.Context
import android.net.Uri
import org.json.JSONArray
import org.json.JSONObject

data class SavedRoom(
    val id: String,
    val name: String,
    val joinUrl: String,
    val host: String,
    val port: Int,
    val lastUsedAt: Long
)

object RoomStore {
    private const val PREF_NAME = "dormcast_rooms"
    private const val KEY_ROOMS = "rooms_json"
    private val TOKEN_REGEX = Regex("^[a-fA-F0-9]{64}$")

    fun parseJoinUrl(raw: String): SavedRoom? {
        val trimmed = raw.trim()
        val uri = runCatching { Uri.parse(trimmed) }.getOrNull() ?: return null
        val scheme = uri.scheme?.lowercase() ?: return null
        if (scheme != "http" && scheme != "https") return null
        val host = uri.host?.takeIf { it.isNotBlank() } ?: return null
        val port = if (uri.port > 0) uri.port else 80
        val fragment = uri.fragment ?: return null
        val token = fragment.split('&')
            .mapNotNull { part ->
                val idx = part.indexOf('=')
                if (idx > 0 && part.substring(0, idx) == "join") part.substring(idx + 1) else null
            }
            .firstOrNull() ?: return null
        if (!TOKEN_REGEX.matches(token)) return null
        val cleanUrl = "$scheme://$host:$port/#join=$token"
        return SavedRoom(
            id = "$host:$port",
            name = host,
            joinUrl = cleanUrl,
            host = host,
            port = port,
            lastUsedAt = System.currentTimeMillis()
        )
    }

    fun load(context: Context): List<SavedRoom> {
        val prefs = context.getSharedPreferences(PREF_NAME, Context.MODE_PRIVATE)
        val raw = prefs.getString(KEY_ROOMS, null) ?: return emptyList()
        return runCatching {
            val arr = JSONArray(raw)
            buildList {
                for (i in 0 until arr.length()) {
                    val o = arr.getJSONObject(i)
                    add(
                        SavedRoom(
                            id = o.getString("id"),
                            name = o.optString("name", o.getString("host")),
                            joinUrl = o.getString("joinUrl"),
                            host = o.getString("host"),
                            port = o.optInt("port", 8080),
                            lastUsedAt = o.optLong("lastUsedAt", 0L)
                        )
                    )
                }
            }.sortedByDescending { it.lastUsedAt }
        }.getOrDefault(emptyList())
    }

    fun upsert(context: Context, room: SavedRoom) {
        val current = load(context).toMutableList()
        val existing = current.indexOfFirst { it.id.equals(room.id, ignoreCase = true) }
        val merged = if (existing >= 0) {
            val old = current.removeAt(existing)
            room.copy(name = if (room.name == room.host && old.name != old.host) old.name else room.name)
        } else {
            room
        }
        current.add(0, merged)
        save(context, current.take(12))
    }

    fun updateName(context: Context, roomId: String, title: String) {
        if (title.isBlank()) return
        val current = load(context).map {
            if (it.id.equals(roomId, ignoreCase = true)) it.copy(name = title.trim()) else it
        }
        save(context, current)
    }

    fun remove(context: Context, roomId: String) {
        val remaining = load(context).filterNot { it.id.equals(roomId, ignoreCase = true) }
        save(context, remaining)
    }

    private fun save(context: Context, rooms: List<SavedRoom>) {
        val arr = JSONArray()
        for (r in rooms) {
            arr.put(JSONObject().apply {
                put("id", r.id)
                put("name", r.name)
                put("joinUrl", r.joinUrl)
                put("host", r.host)
                put("port", r.port)
                put("lastUsedAt", r.lastUsedAt)
            })
        }
        context.getSharedPreferences(PREF_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_ROOMS, arr.toString())
            .apply()
    }
}
