package app.dingus.tv

import android.os.Handler
import android.os.Looper
import java.net.Inet4Address
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import kotlin.random.Random

/**
 * Typing keys with a remote is painful, so Dingus shows a web address and a code: open it on a phone on the same
 * Wi-Fi and paste your VPN key, address or WireGuard config files there. Only runs while that screen is open,
 * and only accepts the code shown on the TV.
 */
class PhoneSetup(private val onSaved: (String) -> Unit) {
    val code = (1000 + Random.nextInt(9000)).toString()
    private var server: ServerSocket? = null
    private val main = Handler(Looper.getMainLooper())
    @Volatile private var stopped = false

    /** "http://192.168.1.50:8765", or null when the stick has no network address. */
    fun start(): String? {
        val ip = lanAddress() ?: return null
        val ss = try { ServerSocket(PORT) } catch (e: Exception) { ServerSocket(0) }
        server = ss
        Thread({
            while (!stopped) {
                val s = try { ss.accept() } catch (e: Exception) { break }
                Thread({ handle(s) }, "dingus-phone").apply { isDaemon = true; start() }
            }
        }, "dingus-phone-setup").apply { isDaemon = true; start() }
        Ui.test("phone-setup code $code port ${ss.localPort}")
        return "http://$ip:${ss.localPort}"
    }

    fun stop() { stopped = true; try { server?.close() } catch (_: Exception) {} }

    private fun handle(s: Socket) {
        try {
            s.soTimeout = 20_000
            val input = s.getInputStream().buffered()
            val head = StringBuilder()
            while (!head.endsWith("\r\n\r\n")) { val c = input.read(); if (c < 0) return; head.append(c.toChar()); if (head.length > 16_384) return }
            val lines = head.lines()
            val (method, path) = lines.first().split(' ').let { (it.getOrNull(0) ?: "") to (it.getOrNull(1) ?: "/") }
            val length = lines.firstOrNull { it.startsWith("Content-Length:", true) }?.substringAfter(':')?.trim()?.toIntOrNull() ?: 0
            val body = if (method == "POST" && length in 1..200_000) ByteArray(length).also { b ->
                var got = 0; while (got < length) { val n = input.read(b, got, length - got); if (n < 0) break; got += n }
            }.toString(Charsets.UTF_8) else ""
            val reply = if (method == "POST" && path.startsWith("/save")) save(form(body)) else page(null)
            val bytes = reply.toByteArray()
            s.getOutputStream().apply {
                write("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: ${bytes.size}\r\nConnection: close\r\n\r\n".toByteArray())
                write(bytes); flush()
            }
        } catch (_: Exception) {
        } finally { try { s.close() } catch (_: Exception) {} }
    }

    private fun form(body: String): Map<String, String> = body.split('&').filter { it.contains('=') }.associate {
        URLDecoder.decode(it.substringBefore('='), "UTF-8") to URLDecoder.decode(it.substringAfter('='), "UTF-8")
    }

    private fun save(f: Map<String, String>): String {
        if (f["code"]?.trim() != code) return page("That code doesn't match the one on the TV.")
        val done = ArrayList<String>()
        try {
            f["key"]?.trim()?.takeIf { it.isNotEmpty() }?.let { Vpn.privateKey = it; done.add("private key") }
            f["address"]?.trim()?.takeIf { it.isNotEmpty() }?.let { Vpn.address = it; done.add("tunnel address") }
            f["conf"]?.trim()?.takeIf { it.isNotEmpty() }?.let { text ->
                // Several files can be pasted one after another: each starts with [Interface].
                val parts = text.split(Regex("(?=\\[Interface])", RegexOption.IGNORE_CASE)).map { it.trim() }.filter { it.isNotEmpty() }
                val base = f["name"]?.trim().orEmpty()
                parts.forEachIndexed { i, c ->
                    val n = if (parts.size == 1) base else (if (base.isEmpty()) "Location" else base) + " ${i + 1}"
                    Vpn.addConfig(n.ifEmpty { guessName(c) }, c)
                }
                done.add(if (parts.size == 1) "1 config file" else "${parts.size} config files")
                if (f["key"].isNullOrBlank()) Vpn.provider = Vpn.CONFIGS
            }
        } catch (e: Exception) {
            return page("That didn't look like a WireGuard config: ${e.message}")
        }
        if (done.isEmpty()) return page("Nothing to save – fill in at least one box.")
        val msg = "Saved: " + done.joinToString(", ")
        main.post { onSaved(msg) }
        return page("✓ $msg. You can close this page.", ok = true)
    }

    /** A name from the file's server address ("uk-lon…" → "uk-lon"). */
    private fun guessName(c: String) = Regex("""Endpoint\s*=\s*([^:\s]+)""", RegexOption.IGNORE_CASE).find(c)?.groupValues?.get(1)
        ?.substringBefore('.')?.ifEmpty { null } ?: "Location ${Vpn.configs.size + 1}"

    private fun page(note: String?, ok: Boolean = false) = """<!doctype html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Dingus VPN setup</title>
<style>body{font-family:system-ui,sans-serif;background:#0b0e14;color:#e8ecf3;margin:0;padding:20px;max-width:640px}
h1{font-size:22px}label{display:block;margin:16px 0 6px;color:#8b94a7;font-size:14px}
input,textarea{width:100%;box-sizing:border-box;background:#141923;color:#e8ecf3;border:1px solid #262e3d;border-radius:8px;padding:12px;font-size:16px}
textarea{height:180px;font-family:monospace;font-size:13px}button{margin-top:20px;background:#3b82f6;color:#fff;border:0;border-radius:8px;padding:14px 22px;font-size:16px}
.note{padding:12px;border-radius:8px;background:${if (ok) "#14532d" else "#7f1d1d"};margin:12px 0}small{color:#8b94a7}</style></head><body>
<h1>Dingus VPN setup</h1>${if (note != null) "<div class=note>${esc(note)}</div>" else ""}
<form method=post action=/save>
<label>Code shown on the TV</label><input name=code inputmode=numeric autocomplete=off required>
<h2 style="font-size:17px;margin-top:28px">Surfshark</h2>
<small>From Surfshark's website: VPN → Manual setup → Desktop or mobile → WireGuard → "I don't have a key pair" → generate, then copy both.</small>
<label>WireGuard private key</label><input name=key autocomplete=off placeholder="e.g. aBcD…=">
<label>Tunnel address</label><input name=address autocomplete=off placeholder="e.g. 10.14.0.2/16">
<h2 style="font-size:17px;margin-top:28px">Or WireGuard config files (ProtonVPN and others)</h2>
<small>Paste the contents of one or more .conf files. Each one becomes a location.</small>
<label>Name (optional)</label><input name=name placeholder="e.g. UK London">
<label>Config file(s)</label><textarea name=conf placeholder="[Interface]&#10;PrivateKey = …&#10;Address = …&#10;&#10;[Peer]&#10;PublicKey = …&#10;Endpoint = …"></textarea>
<button type=submit>Send to the TV</button></form></body></html>"""

    private fun esc(s: String) = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

    companion object {
        const val PORT = 8765

        fun lanAddress(): String? = try {
            NetworkInterface.getNetworkInterfaces().toList().filter { it.isUp && !it.isLoopback && !it.name.startsWith("tun") && !it.name.startsWith("wg") }
                .flatMap { it.inetAddresses.toList() }.filterIsInstance<Inet4Address>()
                .map { it.hostAddress }.firstOrNull { it != null && !it.startsWith("10.0.2.") }
                ?: NetworkInterface.getNetworkInterfaces().toList().flatMap { it.inetAddresses.toList() }
                    .filterIsInstance<Inet4Address>().firstOrNull { !it.isLoopbackAddress }?.hostAddress
        } catch (e: Exception) { null }
    }
}
