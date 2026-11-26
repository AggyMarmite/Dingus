package app.dingus.tv

import android.content.Context
import android.content.SharedPreferences
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Handler
import android.os.Looper
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.ServerSocket
import java.net.Socket
import java.net.URL
import kotlin.random.Random

/**
 * Settings → About → Move to another Fire TV: one Fire TV sends its playlists, categories, channels and settings to
 * another on the same Wi-Fi. The sender shows a 4-digit code and waits; the receiver finds it by itself (it announces
 * itself on the network) and asks for everything with that code – without the code nothing is given.
 */
object Move {
    private const val TYPE = "_dingus-move._tcp."
    private const val MAIN = "dingus"
    /** Things that belong to this Fire TV only: when its lists were last fetched, its update check, its VPN speed. */
    private val localOnly = setOf("channelsLoadedAt", "guideLoadedAt", "updateCheckedAt", "vpnLastSpeed", "movedFrom")
    private val reminderKeys = setOf("reminders", "seriesReminders")

    /** This Fire TV's name, as set in its own settings ("Living room Fire TV"), else its model. */
    fun deviceName(ctx: Context): String =
        try { android.provider.Settings.Global.getString(ctx.contentResolver, "device_name") } catch (_: Exception) { null }
            ?.takeIf { it.isNotBlank() } ?: android.os.Build.MODEL ?: "Fire TV"

    // ------------------------------------------------------------------ what's copied

    private fun dump(p: SharedPreferences, skip: (String) -> Boolean): JSONObject {
        val o = JSONObject()
        for ((k, v) in p.all) {
            if (skip(k) || v == null) continue
            o.put(k, when (v) {
                is String -> JSONArray().put("s").put(v)
                is Boolean -> JSONArray().put("b").put(v)
                is Int -> JSONArray().put("i").put(v)
                is Long -> JSONArray().put("l").put(v)
                is Float -> JSONArray().put("f").put(v.toDouble())
                is Set<*> -> JSONArray().put("ss").put(JSONArray(v.map { it.toString() }))
                else -> continue
            })
        }
        return o
    }

    private fun load(e: SharedPreferences.Editor, o: JSONObject) {
        for (k in o.keys()) {
            val a = o.optJSONArray(k) ?: continue
            when (a.optString(0)) {
                "s" -> e.putString(k, a.optString(1))
                "b" -> e.putBoolean(k, a.optBoolean(1))
                "i" -> e.putInt(k, a.optInt(1))
                "l" -> e.putLong(k, a.optLong(1))
                "f" -> e.putFloat(k, a.optDouble(1).toFloat())
                "ss" -> e.putStringSet(k, a.optJSONArray(1)?.let { s -> (0 until s.length()).map { s.optString(it) }.toSet() } ?: emptySet())
            }
        }
    }

    /** Everything to send: the settings (playlists, categories, channels, VPN…), and watch progress / My list and reminders if wanted. */
    fun export(ctx: Context, progress: Boolean, reminders: Boolean): JSONObject {
        val main = dump(ctx.getSharedPreferences(MAIN, Context.MODE_PRIVATE)) { k -> k in localOnly || (!reminders && k in reminderKeys) }
        val out = JSONObject().put("v", 1).put("from", deviceName(ctx)).put("main", main)
        if (progress) {
            val pr = JSONObject()
            for (p in Store.playlists) pr.put("progress_${p.id}", dump(ctx.getSharedPreferences("progress_${p.id}", Context.MODE_PRIVATE)) { false })
            out.put("progress", pr)
        }
        return out
    }

    /** What's on offer, for the receiver's list: the name, the version and how many playlists. */
    private fun info(ctx: Context) = JSONObject().put("name", deviceName(ctx)).put("version", BuildConfig.VERSION_NAME)
        .put("playlists", Store.playlists.size).put("reminders", JSONArray(Store.prefsString("reminders", "[]")).length())

    /** Takes [data] in place of what's here (what this Fire TV had is replaced). Call off the main thread. */
    fun import(ctx: Context, data: JSONObject) {
        val main = data.optJSONObject("main") ?: throw IllegalStateException("Nothing was sent")
        val keepLocal = ctx.getSharedPreferences(MAIN, Context.MODE_PRIVATE).all.filterKeys { it in localOnly && it != "channelsLoadedAt" && it != "guideLoadedAt" }
        ctx.getSharedPreferences(MAIN, Context.MODE_PRIVATE).edit().apply {
            clear()
            load(this, main)
            // (Its own update check stays; the lists are fetched fresh for this Fire TV.)
            for ((k, v) in keepLocal) if (v is Long) putLong(k, v) else if (v is String) putString(k, v)
        }.commit()
        data.optJSONObject("progress")?.let { pr ->
            for (name in pr.keys()) {
                if (!name.startsWith("progress_")) continue
                ctx.getSharedPreferences(name, Context.MODE_PRIVATE).edit().apply { clear(); load(this, pr.getJSONObject(name)) }.commit()
            }
        }
    }

    /** Dingus starts again with what came across (everything in memory is from before). */
    fun restart(ctx: Context) {
        val i = android.content.Intent(ctx, HomeActivity::class.java).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK or android.content.Intent.FLAG_ACTIVITY_CLEAR_TASK)
        val pi = android.app.PendingIntent.getActivity(ctx, 7, i, android.app.PendingIntent.FLAG_IMMUTABLE or android.app.PendingIntent.FLAG_CANCEL_CURRENT)
        (ctx.getSystemService(Context.ALARM_SERVICE) as android.app.AlarmManager).set(android.app.AlarmManager.RTC, System.currentTimeMillis() + 700, pi)
        android.os.Process.killProcess(android.os.Process.myPid())
    }

    // ------------------------------------------------------------------ the old Fire TV: sending

    class Sender(private val ctx: Context, private val onSent: (String) -> Unit) {
        val code = (1000 + Random.nextInt(9000)).toString()
        private var server: ServerSocket? = null
        private var nsd: NsdManager? = null
        private var reg: NsdManager.RegistrationListener? = null
        @Volatile private var stopped = false
        private val main = Handler(Looper.getMainLooper())
        var port = 0; private set

        fun start() {
            val ss = ServerSocket(0); server = ss; port = ss.localPort
            Thread({
                while (!stopped) {
                    val s = try { ss.accept() } catch (e: Exception) { break }
                    Thread({ handle(s) }, "dingus-move").apply { isDaemon = true; start() }
                }
            }, "dingus-move-server").apply { isDaemon = true; start() }
            // Announced on the network, so the other Fire TV finds it by itself.
            try {
                val m = ctx.getSystemService(Context.NSD_SERVICE) as NsdManager
                val l = object : NsdManager.RegistrationListener {
                    override fun onServiceRegistered(i: NsdServiceInfo) { Ui.test("move-announced ${i.serviceName}") }
                    override fun onRegistrationFailed(i: NsdServiceInfo, e: Int) { Ui.test("move-announce-failed $e") }
                    override fun onServiceUnregistered(i: NsdServiceInfo) {}
                    override fun onUnregistrationFailed(i: NsdServiceInfo, e: Int) {}
                }
                m.registerService(NsdServiceInfo().apply { serviceName = deviceName(ctx).take(60); serviceType = TYPE; port = this@Sender.port }, NsdManager.PROTOCOL_DNS_SD, l)
                nsd = m; reg = l
            } catch (e: Exception) { android.util.Log.w("Dingus", "move announce", e) }
            Ui.test("move-send code $code port $port")
        }

        fun stop() {
            stopped = true
            try { server?.close() } catch (_: Exception) {}
            try { reg?.let { nsd?.unregisterService(it) } } catch (_: Exception) {}
        }

        private fun handle(s: Socket) {
            try {
                s.soTimeout = 15_000
                val input = s.getInputStream().buffered()
                val head = StringBuilder()
                while (!head.endsWith("\r\n\r\n")) { val c = input.read(); if (c < 0) return; head.append(c.toChar()); if (head.length > 8_192) return }
                val path = head.lines().first().split(' ').getOrNull(1) ?: "/"
                fun q(k: String) = Regex("""[?&]$k=([^&]*)""").find(path)?.groupValues?.get(1)
                val (status, body) = when {
                    path.startsWith("/info") -> 200 to info(ctx).toString()
                    path.startsWith("/move") && q("code") == code -> {
                        val data = export(ctx, progress = q("progress") != "0", reminders = q("reminders") != "0").toString()
                        main.post { onSent(q("name") ?: "the other Fire TV") }
                        200 to data
                    }
                    path.startsWith("/move") -> 403 to "{\"error\":\"code\"}"
                    else -> 404 to "{}"
                }
                val bytes = body.toByteArray()
                s.getOutputStream().apply {
                    write("HTTP/1.1 $status OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: ${bytes.size}\r\nConnection: close\r\n\r\n".toByteArray())
                    write(bytes); flush()
                }
            } catch (_: Exception) {
            } finally { try { s.close() } catch (_: Exception) {} }
        }
    }

    // ------------------------------------------------------------------ the new Fire TV: finding and copying

    class Found(val name: String, val host: String, val port: Int, val version: String, val playlists: Int, val reminders: Int)

    /** Looks for Fire TVs showing their code on this Wi-Fi; [onFound] for each (on the main thread). */
    class Finder(private val ctx: Context, private val onFound: (Found) -> Unit) {
        private val m = ctx.getSystemService(Context.NSD_SERVICE) as NsdManager
        private val main = Handler(Looper.getMainLooper())
        private var listener: NsdManager.DiscoveryListener? = null
        private val seen = HashSet<String>()

        fun start() {
            val l = object : NsdManager.DiscoveryListener {
                override fun onDiscoveryStarted(t: String) { Ui.test("move-looking") }
                override fun onDiscoveryStopped(t: String) {}
                override fun onStartDiscoveryFailed(t: String, e: Int) { Ui.test("move-look-failed $e") }
                override fun onStopDiscoveryFailed(t: String, e: Int) {}
                override fun onServiceLost(i: NsdServiceInfo) {}
                override fun onServiceFound(i: NsdServiceInfo) {
                    // (One at a time: resolving several at once fails on older Android.)
                    synchronized(this@Finder) {
                        try {
                            m.resolveService(i, object : NsdManager.ResolveListener {
                                override fun onResolveFailed(i: NsdServiceInfo, e: Int) {}
                                override fun onServiceResolved(r: NsdServiceInfo) {
                                    val host = r.host?.hostAddress ?: return
                                    Thread { ask(host, r.port) }.start()
                                }
                            })
                        } catch (_: Exception) {}
                    }
                }
            }
            listener = l
            try { m.discoverServices(TYPE, NsdManager.PROTOCOL_DNS_SD, l) } catch (e: Exception) { android.util.Log.w("Dingus", "move find", e) }
        }

        /** Asks a Fire TV what it has (also used directly, by address, by the self-test). */
        fun ask(host: String, port: Int) {
            if (!synchronized(seen) { seen.add("$host:$port") }) return
            try {
                val o = JSONObject(get("http://$host:$port/info"))
                val f = Found(o.optString("name", "Fire TV"), host, port, o.optString("version"), o.optInt("playlists"), o.optInt("reminders"))
                main.post { onFound(f) }
            } catch (_: Exception) { synchronized(seen) { seen.remove("$host:$port") } }
        }

        fun stop() { try { listener?.let { m.stopServiceDiscovery(it) } } catch (_: Exception) {} }
    }

    class WrongCode : Exception("That code doesn't match the one on the other Fire TV")

    /** Asks [from] for everything, with the code it's showing. Call off the main thread. */
    fun fetch(ctx: Context, from: Found, code: String, progress: Boolean, reminders: Boolean): JSONObject {
        val name = java.net.URLEncoder.encode(deviceName(ctx), "UTF-8")
        val c = URL("http://${from.host}:${from.port}/move?code=$code&progress=${if (progress) 1 else 0}&reminders=${if (reminders) 1 else 0}&name=$name").openConnection() as HttpURLConnection
        c.connectTimeout = 8_000; c.readTimeout = 20_000
        try {
            if (c.responseCode == 403) throw WrongCode()
            return JSONObject(c.inputStream.use { it.readBytes().toString(Charsets.UTF_8) })
        } finally { c.disconnect() }
    }

    private fun get(url: String): String {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 5_000; c.readTimeout = 8_000
        try { return c.inputStream.use { it.readBytes().toString(Charsets.UTF_8) } } finally { c.disconnect() }
    }
}
