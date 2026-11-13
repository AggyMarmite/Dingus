package app.dingus.tv

import android.app.Activity
import android.os.Bundle
import android.view.View
import android.widget.EditText
import android.widget.TextView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Add (or change) the playlist: an Xtream Codes login or an M3U link. It's checked before it's saved. */
class AddPlaylistActivity : BaseActivity() {
    companion object {
        /** Add another playlist (otherwise: edit the one in use, or add the first). */
        const val NEW = "new"
        /** Edit this playlist. */
        const val ID = "id"
    }

    private var editing: Playlist? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var type = "xtream"
    private var busy = false

    private val name by lazy { findViewById<EditText>(R.id.name) }
    private val server by lazy { findViewById<EditText>(R.id.server) }
    private val user by lazy { findViewById<EditText>(R.id.user) }
    private val pass by lazy { findViewById<EditText>(R.id.pass) }
    private val m3uUrl by lazy { findViewById<EditText>(R.id.m3uUrl) }
    private val epg by lazy { findViewById<EditText>(R.id.epg) }
    private val error by lazy { findViewById<TextView>(R.id.error) }
    private val save by lazy { findViewById<TextView>(R.id.save) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_add_playlist)

        findViewById<View>(R.id.typeXtream).setOnClickListener { setType("xtream") }
        findViewById<View>(R.id.typeM3u).setOnClickListener { setType("m3u") }
        save.setOnClickListener { trySave() }

        val existing = when {
            intent.getBooleanExtra(NEW, false) -> null
            intent.getStringExtra(ID) != null -> Store.playlists.firstOrNull { it.id == intent.getStringExtra(ID) }
            else -> Store.playlist
        }
        editing = existing
        if (existing != null) {
            findViewById<TextView>(R.id.heading).text = existing.name
            name.setText(existing.name)
            server.setText(existing.server); user.setText(existing.user); pass.setText(existing.pass)
            m3uUrl.setText(existing.m3uUrl); epg.setText(existing.epgUrls.firstOrNull() ?: "")
            setType(existing.type)
            findViewById<View>(R.id.remove).apply {
                visibility = View.VISIBLE
                setOnClickListener {
                    Playlists.remove(this@AddPlaylistActivity, existing.id)
                    finish()
                }
            }
        } else {
            if (Store.playlists.isNotEmpty()) findViewById<TextView>(R.id.heading).text = "Add another playlist"
            setType("xtream")
        }
        findViewById<View>(if (type == "xtream") R.id.typeXtream else R.id.typeM3u).requestFocus()
    }

    private fun setType(t: String) {
        type = if (t == "m3u") "m3u" else "xtream"
        findViewById<View>(R.id.xtream).visibility = if (type == "xtream") View.VISIBLE else View.GONE
        findViewById<View>(R.id.m3u).visibility = if (type == "m3u") View.VISIBLE else View.GONE
        findViewById<View>(R.id.typeXtream).isActivated = type == "xtream"
        findViewById<View>(R.id.typeM3u).isActivated = type == "m3u"
    }

    private fun showError(s: String?) {
        error.text = s ?: ""
        error.visibility = if (s.isNullOrEmpty()) View.GONE else View.VISIBLE
    }

    private fun trySave() {
        if (busy) return
        val p = Playlist(
            name = name.text.toString().trim().ifEmpty { "My playlist" },
            type = type,
            server = server.text.toString().trim(),
            user = user.text.toString().trim(),
            pass = pass.text.toString(),
            m3uUrl = m3uUrl.text.toString().trim(),
            // The box holds your first guide address; any others (added in Settings) are kept.
            epgUrl = (listOf(epg.text.toString().trim()) + (editing?.epgUrls?.drop(1) ?: emptyList())).filter { it.isNotEmpty() }.joinToString("\n"),
            id = editing?.id ?: Store.newId(),
            useProviderGuide = editing?.useProviderGuide ?: true,
        )
        if (type == "xtream" && (p.server.isEmpty() || p.user.isEmpty())) return showError("Enter the server address, username and password.")
        if (type == "m3u" && p.m3uUrl.isEmpty()) return showError("Enter the M3U address.")
        showError(null)
        busy = true
        save.text = "Checking…"
        scope.launch {
            try {
                val r = withContext(Dispatchers.IO) { Sources.load(p) }
                if (r.channels.isEmpty()) { showError("That worked, but there are no live channels in it."); return@launch }
                val switching = p.id != Store.activeId
                Store.playlist = p // saves it and makes it the one in use
                if (switching) { Repo.reset(); Vod.reset() }
                Repo.clear()
                Vod.clear()
                Repo.refreshChannels(p, r)
                Store.guideLoadedAt = 0
                Ui.test("playlist-added ${p.name}")
                finish()
            } catch (e: Exception) {
                showError(Net.friendly(e))
            } finally {
                busy = false
                save.text = "Save"
            }
        }
    }

    // ------------------------------------------------------------------ from your phone

    private var phone: PhoneSetup? = null

    override fun onResume() {
        super.onResume()
        val ps = PhoneSetup(onSaved = {}, onPlaylist = { f -> fromPhone(f) })
        val url = ps.start()
        phone = ps
        val box = findViewById<View>(R.id.phoneBox)
        if (url == null) { box.visibility = View.GONE; return }
        // A QR code to scan with the phone's camera: opens the form with the code already filled in.
        findViewById<android.widget.ImageView>(R.id.phoneQr).setImageBitmap(Qr.bitmap(ps.qrAddress(url), Ui.dp(this, 150)))
        findViewById<TextView>(R.id.phoneHint).text =
            "Easier on your phone: scan this with its camera (on the same Wi-Fi) and fill in your playlist there.\n\nOr open  $url  and enter the code  ${ps.code}"
        box.visibility = View.VISIBLE
    }

    override fun onPause() { super.onPause(); phone?.stop(); phone = null }

    /** The form filled in on a phone: put it in the boxes here and check it, as if you'd typed it. */
    private fun fromPhone(f: Map<String, String>) {
        setType(f["type"] ?: "xtream")
        f["name"]?.trim()?.takeIf { it.isNotEmpty() }?.let { name.setText(it) }
        server.setText(f["server"]?.trim() ?: ""); user.setText(f["user"]?.trim() ?: ""); pass.setText(f["pass"] ?: "")
        m3uUrl.setText(f["m3u"]?.trim() ?: "")
        f["epg"]?.trim()?.takeIf { it.isNotEmpty() }?.let { epg.setText(it) }
        Ui.test("playlist-from-phone ${f["type"]}")
        trySave()
    }

    override fun onDestroy() { scope.cancel(); super.onDestroy() }
}
