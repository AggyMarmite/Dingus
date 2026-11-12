package app.dingus.tv

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.widget.LinearLayout
import android.widget.TextView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** Settings: playlist, refreshing, the guide preview and updates. */
class SettingsActivity : Activity() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var list: LinearLayout
    private var refreshing = false

    private lateinit var playlistRow: View
    private lateinit var refreshRow: View
    private lateinit var previewRow: View
    private lateinit var updateRow: View
    private lateinit var hiddenRow: View

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        list = findViewById(R.id.list)

        playlistRow = row("Playlist", "") { startActivity(Intent(this, AddPlaylistActivity::class.java)) }
        refreshRow = row("Update channels and TV guide", "") { refresh() }
        previewRow = row("Preview in the guide", "") {
            Store.preview = !Store.preview
            update()
        }
        hiddenRow = row("Hidden categories", "") { unhide() }
        updateRow = row("Check for updates", "") {
            sub(updateRow, "Checking…")
            Updater.check(this, manual = true) { msg -> sub(updateRow, msg) }
        }
        row("About Dingus", "Version ${BuildConfig.VERSION_NAME}  ·  Live TV with a TV guide, made for the remote") {}
        playlistRow.requestFocus()
    }

    override fun onResume() { super.onResume(); update(); Ui.test("settings-shown") }

    private fun update() {
        val p = Store.playlist
        sub(playlistRow, p?.let { "${it.name}  ·  ${if (it.type == "xtream") "Xtream Codes login" else "M3U link"}" } ?: "Add your playlist")
        if (!refreshing) {
            val at = Store.guideLoadedAt
            sub(refreshRow, "${Repo.channels.size} channels" +
                (if (at > 0) "  ·  guide updated ${Ui.day(at).trim().ifEmpty { "today" }} at ${Ui.time(at)}" else ""))
        }
        sub(previewRow, if (Store.preview) "On  ·  the highlighted channel plays in the small window" else "Off")
        sub(updateRow, "You have version ${BuildConfig.VERSION_NAME}")
        val n = Store.hidden.size
        sub(hiddenRow, if (n == 0) "None  ·  press ≡ on a category to hide it" else "$n hidden  ·  choose to show them again")
    }

    private fun refresh() {
        val p = Store.playlist ?: return
        if (refreshing) return
        refreshing = true
        scope.launch {
            try {
                sub(refreshRow, "Updating channels…")
                Repo.refreshChannels(p)
                sub(refreshRow, "Updating the TV guide…")
                Repo.refreshGuide()
                refreshing = false
                update()
            } catch (e: Exception) {
                refreshing = false
                sub(refreshRow, Net.friendly(e))
            }
        }
    }

    /** Pick hidden categories to show again. */
    private fun unhide() {
        val keys = Store.hidden.toList()
        if (keys.isEmpty()) return
        val names = keys.map { k ->
            val kind = k.substringBefore(':'); val id = k.substringAfter(':')
            val name = when (kind) {
                "live" -> Repo.categories.firstOrNull { it.id == id }?.name
                else -> Vod.cached(kind)?.categories?.firstOrNull { it.id == id }?.name
            } ?: "Category $id"
            name + when (kind) { "movies" -> "  (Films)"; "series" -> "  (TV Shows)"; else -> "  (TV guide)" }
        }
        val show = BooleanArray(keys.size)
        Dialogs.builder(this).setTitle("Show again")
            .setMultiChoiceItems(names.toTypedArray(), show) { _, i, on -> show[i] = on }
            .setPositiveButton("Done") { _, _ ->
                keys.forEachIndexed { i, k -> if (show[i]) Store.setHidden(k, false) }
                update()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun row(title: String, subtitle: String, click: () -> Unit): View {
        val v = LayoutInflater.from(this).inflate(R.layout.item_setting, list, false)
        v.findViewById<TextView>(R.id.title).text = title
        v.findViewById<TextView>(R.id.subtitle).text = subtitle
        v.setOnClickListener { click() }
        list.addView(v)
        return v
    }

    private fun sub(v: View, s: String) { v.findViewById<TextView>(R.id.subtitle).text = s }

    override fun onDestroy() { scope.cancel(); super.onDestroy() }
}
