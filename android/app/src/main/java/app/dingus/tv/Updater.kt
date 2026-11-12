package app.dingus.tv

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.view.LayoutInflater
import android.widget.ProgressBar
import android.widget.TextView
import androidx.core.content.FileProvider
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/** Checks GitHub for a newer release and installs its APK, all from inside the app. */
object Updater {
    var api = "https://api.github.com/repos/AggyMarmite/Dingus/releases/latest"

    class Release(val version: String, val apk: String, val notes: String)

    private val scope = CoroutineScope(Dispatchers.Main)
    private var busy = false

    /** Is [a] newer than [b]? ("0.15.0" vs "0.14.2") */
    fun newer(a: String, b: String): Boolean {
        val x = a.trimStart('v', 'V').split('.', '-').map { it.toIntOrNull() ?: 0 }
        val y = b.trimStart('v', 'V').split('.', '-').map { it.toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(x.size, y.size)) {
            val p = x.getOrElse(i) { 0 }; val q = y.getOrElse(i) { 0 }
            if (p != q) return p > q
        }
        return false
    }

    private fun latest(): Release? {
        val c = open(api)
        val o = c.inputStream.use { JSONObject(it.readBytes().toString(Charsets.UTF_8)) }
        val tag = o.optString("tag_name").ifEmpty { return null }
        val assets = o.optJSONArray("assets") ?: return null
        for (i in 0 until assets.length()) {
            val a = assets.getJSONObject(i)
            if (a.optString("name").endsWith(".apk", true)) {
                return Release(tag.trimStart('v', 'V'), a.optString("browser_download_url"), o.optString("body"))
            }
        }
        return null
    }

    private fun open(url: String): HttpURLConnection {
        var u = URL(url)
        repeat(6) {
            val c = (u.openConnection() as HttpURLConnection).apply {
                instanceFollowRedirects = false
                connectTimeout = 15_000; readTimeout = 30_000
                setRequestProperty("User-Agent", "Dingus-Android/${BuildConfig.VERSION_NAME}")
                setRequestProperty("Accept", if (url.contains("api.github.com")) "application/vnd.github+json" else "*/*")
            }
            when (c.responseCode) {
                in 300..399 -> { u = URL(u, c.getHeaderField("Location") ?: throw IOException("Bad redirect")); c.disconnect() }
                200 -> return c
                else -> throw IOException("The update server answered ${c.responseCode}")
            }
        }
        throw IOException("Too many redirects")
    }

    /**
     * Looks for an update. [manual] shows a message either way; otherwise only when there is one.
     * [auto] (self-test only) skips the question and downloads straight away.
     */
    fun check(act: Activity, manual: Boolean, auto: Boolean = false, done: ((String) -> Unit)? = null) {
        if (busy) return
        busy = true
        scope.launch {
            val r = try { withContext(Dispatchers.IO) { latest() } } catch (e: Exception) {
                busy = false
                Ui.test("update-check-failed ${e.message}")
                if (manual) done?.invoke("Couldn't check for updates: ${Net.friendly(e)}")
                return@launch
            }
            busy = false
            Store.updateCheckedAt = System.currentTimeMillis()
            if (r == null || !newer(r.version, BuildConfig.VERSION_NAME)) {
                Ui.test("update-none")
                if (manual) done?.invoke("You're up to date (version ${BuildConfig.VERSION_NAME}).")
                return@launch
            }
            Ui.test("update-found ${r.version}")
            done?.invoke("Version ${r.version} is available.")
            if (act.isFinishing) return@launch
            if (auto) { download(act, r); return@launch }
            val notes = r.notes.lines().map { it.trim() }.filter { it.startsWith("-") || it.startsWith("*") }
                .joinToString("\n") { "•  " + it.trimStart('-', '*', ' ') }.take(600)
            AlertDialog.Builder(act, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("Dingus ${r.version} is available")
                .setMessage((if (notes.isNotEmpty()) "$notes\n\n" else "") + "You have version ${BuildConfig.VERSION_NAME}. Your playlist and favourites are kept.")
                .setPositiveButton("Update now") { _, _ -> download(act, r) }
                .setNegativeButton("Later", null)
                .show()
                .getButton(AlertDialog.BUTTON_POSITIVE)?.requestFocus()
        }
    }

    private fun download(act: Activity, r: Release) {
        val view = LayoutInflater.from(act).inflate(R.layout.dialog_progress, null)
        val bar = view.findViewById<ProgressBar>(R.id.bar)
        val text = view.findViewById<TextView>(R.id.text)
        var job: Job? = null
        val dlg = AlertDialog.Builder(act, android.R.style.Theme_DeviceDefault_Dialog_Alert)
            .setTitle("Downloading Dingus ${r.version}")
            .setView(view)
            .setCancelable(false)
            .setNegativeButton("Cancel") { _, _ -> job?.cancel() }
            .show()
        job = scope.launch {
            try {
                val file = withContext(Dispatchers.IO) {
                    val dir = File(act.externalCacheDir ?: act.cacheDir, "updates").apply { mkdirs() }
                    dir.listFiles()?.forEach { it.delete() }
                    val f = File(dir, "Dingus-${r.version}.apk")
                    val c = open(r.apk)
                    val total = c.contentLength.toLong()
                    c.inputStream.use { inp ->
                        FileOutputStream(f).use { out ->
                            val buf = ByteArray(64 * 1024)
                            var got = 0L; var last = 0L
                            while (isActive) {
                                val n = inp.read(buf); if (n < 0) break
                                out.write(buf, 0, n); got += n
                                if (got - last > 128 * 1024) {
                                    last = got
                                    val g = got
                                    launch(Dispatchers.Main) {
                                        if (total > 0) { bar.isIndeterminate = false; bar.progress = (g * 1000 / total).toInt() }
                                        text.text = "${g / 1024 / 1024} of ${if (total > 0) "${total / 1024 / 1024} MB" else "…"}"
                                    }
                                }
                            }
                            if (!isActive) throw IOException("Cancelled")
                        }
                    }
                    if (total > 0 && f.length() != total) throw IOException("The download was incomplete")
                    f
                }
                dlg.dismiss()
                Ui.test("update-downloaded ${file.length()}")
                install(act, file)
            } catch (e: Exception) {
                dlg.dismiss()
                if (e.message == "Cancelled" || e is kotlinx.coroutines.CancellationException) return@launch
                Ui.test("update-failed ${e.message}")
                AlertDialog.Builder(act, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                    .setTitle("The update didn't download")
                    .setMessage(Net.friendly(e))
                    .setPositiveButton("OK", null)
                    .show()
            }
        }
    }

    private fun install(act: Activity, f: File) {
        val uri: Uri = if (Build.VERSION.SDK_INT >= 24) FileProvider.getUriForFile(act, "${act.packageName}.files", f)
        else { f.setReadable(true, false); Uri.fromFile(f) }
        val i = Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 26 && !act.packageManager.canRequestPackageInstalls()) {
            // Android asks once for permission to install updates; Fire TV keeps this under Developer options.
            AlertDialog.Builder(act, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("One-time permission")
                .setMessage("To install updates, allow Dingus to install apps.\n\nOn a Fire TV: Settings → My Fire TV → Developer options → Install unknown apps → Dingus → On. Then choose Check for updates again.")
                .setPositiveButton("Open settings") { _, _ ->
                    try {
                        act.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${act.packageName}")))
                    } catch (e: Exception) {
                        try { act.startActivity(i) } catch (_: Exception) {}
                    }
                }
                .setNegativeButton("Try anyway") { _, _ -> try { act.startActivity(i) } catch (_: Exception) {} }
                .show()
            Ui.test("update-needs-permission")
            return
        }
        try {
            act.startActivity(i)
            Ui.test("update-installer-opened")
        } catch (e: Exception) {
            Ui.test("update-installer-missing ${e.message}")
        }
    }
}
