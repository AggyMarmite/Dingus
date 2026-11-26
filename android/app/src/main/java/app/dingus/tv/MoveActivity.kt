package app.dingus.tv

import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Move to another Fire TV (sending: the code, waiting) and Copy from another Fire TV (receiving: the Fire TVs found,
 * the code, what to bring, then Dingus starts again with it). One frosted panel in the middle of the screen.
 */
class MoveActivity : BaseActivity() {
    companion object {
        /** True: this Fire TV sends. False: it copies from another. */
        const val SEND = "send"
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var panel: LinearLayout
    private var sender: Move.Sender? = null
    private var finder: Move.Finder? = null
    private val found = ArrayList<Move.Found>()
    private var chosen: Move.Found? = null
    private var code = ""
    private var bringProgress = true
    private var bringReminders = true
    private var step = ""
    private val d by lazy { resources.displayMetrics.density }
    private fun dp(v: Int) = (v * d).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val root = FrameLayout(this).apply { setBackgroundColor(Themes.color(this@MoveActivity, R.attr.dBg)) }
        val accent = Themes.color(this, R.attr.dAccent)
        root.addView(View(this).apply {
            background = GradientDrawable().apply {
                gradientType = GradientDrawable.RADIAL_GRADIENT
                colors = intArrayOf((accent and 0x00FFFFFF) or (0x30 shl 24), (accent and 0x00FFFFFF) or (0x0C shl 24), 0)
                gradientRadius = resources.displayMetrics.widthPixels * 0.5f
                setGradientCenter(0.85f, -0.05f)
            }
        }, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundResource(R.drawable.search_panel)
            setPadding(dp(40), dp(34), dp(40), dp(34))
            clipChildren = false; clipToPadding = false
        }
        root.addView(panel, FrameLayout.LayoutParams(dp(620), ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER))
        root.clipChildren = false
        setContentView(root)
        if (intent.getBooleanExtra(SEND, true)) startSending() else startFinding()
        // (Self-test: copy from the address given, as if it had been found on the network.)
        if (BuildConfig.DEBUG) intent.getStringExtra("dingus_test_from")?.let { a -> Thread { finder?.ask(a.substringBefore(':'), a.substringAfter(':').toInt()) }.start() }
    }

    override fun onDestroy() { sender?.stop(); finder?.stop(); scope.cancel(); super.onDestroy() }

    // ------------------------------------------------------------------ pieces

    private fun clear() { panel.removeAllViews() }

    private fun title(t: String) = panel.addView(TextView(this).apply {
        text = t; textSize = 26f; setTypeface(typeface, android.graphics.Typeface.BOLD); setTextColor(0xFFFFFFFF.toInt())
    })

    private fun para(t: CharSequence, top: Int = 8) = TextView(this).apply {
        text = t; textSize = 15f; setTextColor(0xFFB8C2D3.toInt()); setLineSpacing(dp(2).toFloat(), 1f)
    }.also { panel.addView(it, lp(top)) }

    private fun lp(top: Int) = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(top) }

    private fun codeBoxes(shown: String, typing: Boolean) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        for (i in 0 until 4) addView(TextView(this@MoveActivity).apply {
            text = shown.getOrNull(i)?.toString() ?: "·"
            gravity = Gravity.CENTER; textSize = 30f; setTypeface(typeface, android.graphics.Typeface.BOLD)
            setTextColor(if (i < shown.length) 0xFFFFFFFF.toInt() else 0x55FFFFFF)
            background = GradientDrawable().apply {
                cornerRadius = dp(12).toFloat(); setColor(0x22FFFFFF)
                if (typing && i == shown.length) setStroke(dp(2), 0xFFFFFFFF.toInt())
            }
        }, LinearLayout.LayoutParams(dp(58), dp(70)).apply { marginEnd = dp(12) })
    }.also { panel.addView(it, lp(14)) }

    private fun button(t: String, primary: Boolean = false, click: () -> Unit) = TextView(this).apply {
        text = t; textSize = 15f; setTypeface(typeface, android.graphics.Typeface.BOLD)
        setBackgroundResource(R.drawable.pill_btn_bg); isFocusable = true; isClickable = true
        setPadding(dp(26), dp(11), dp(26), dp(11)); setTextColor(resources.getColorStateList(R.color.pill_text, theme))
        setOnClickListener { click() }
        if (primary) post { requestFocus() }
    }

    private fun buttons(vararg b: TextView) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL; clipChildren = false; clipToPadding = false
        b.forEachIndexed { i, v -> addView(v, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { if (i > 0) marginStart = dp(12) }) }
    }.also { panel.addView(it, lp(24)) }

    private fun waiting(t: String) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
        addView(ProgressBar(this@MoveActivity).apply { isIndeterminate = true }, LinearLayout.LayoutParams(dp(22), dp(22)))
        addView(TextView(this@MoveActivity).apply { text = t; textSize = 14f; setTextColor(0xFFCBD5E1.toInt()) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginStart = dp(12) })
    }.also { panel.addView(it, lp(16)) }

    /** A row to choose (a Fire TV, or something to bring): white when highlighted, like the mini menu. */
    private fun choice(main: String, sub: String?, tick: Boolean?, click: () -> Unit) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
        setBackgroundResource(R.drawable.mini_row_bg); isFocusable = true; isClickable = true
        setPadding(dp(16), dp(10), dp(16), dp(10))
        val texts = LinearLayout(this@MoveActivity).apply { orientation = LinearLayout.VERTICAL }
        val a = TextView(this@MoveActivity).apply { text = main; textSize = 16f; setTypeface(typeface, android.graphics.Typeface.BOLD) }
        val b = sub?.let { TextView(this@MoveActivity).apply { text = it; textSize = 13f } }
        texts.addView(a); b?.let { texts.addView(it) }
        addView(texts, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        val t = tick?.let { on -> TextView(this@MoveActivity).apply {
            text = if (on) "✓" else ""; gravity = Gravity.CENTER; textSize = 15f; setTypeface(typeface, android.graphics.Typeface.BOLD)
            setTextColor(0xFF04120A.toInt())
            background = GradientDrawable().apply { cornerRadius = dp(6).toFloat(); setColor(if (on) 0xFF22C55E.toInt() else 0x33FFFFFF) }
        } }
        t?.let { addView(it, LinearLayout.LayoutParams(dp(26), dp(26))) }
        fun colours(f: Boolean) { a.setTextColor(if (f) 0xFF0B0E14.toInt() else 0xFFFFFFFF.toInt()); b?.setTextColor(if (f) 0xFF4B5565.toInt() else 0xFF8B95A7.toInt()) }
        colours(false)
        setOnFocusChangeListener { _, f -> colours(f) }
        setOnClickListener { click() }
    }.also { panel.addView(it, lp(4)) }

    // ------------------------------------------------------------------ sending

    private fun startSending() {
        step = "send"
        val s = Move.Sender(this) { name -> sent(java.net.URLDecoder.decode(name, "UTF-8")) }
        try { s.start() } catch (e: Exception) { showProblem("Couldn't start: ${Net.friendly(e)}"); return }
        sender = s
        clear()
        title("Move to another Fire TV")
        para(android.text.SpannableStringBuilder("On the other Fire TV, open Dingus and choose ").apply {
            val st = length; append("Copy from another Fire TV"); setSpan(android.text.style.StyleSpan(android.graphics.Typeface.BOLD), st, length, 0)
            append(" (Settings → About). When it asks, type this code:")
        })
        codeBoxes(s.code, typing = false)
        waiting("Waiting for the other Fire TV…  (both need to be on the same Wi-Fi)")
        buttons(button("Cancel", primary = true) { finish() })
        // (Self-test: copy from this one, on the same Fire TV, as if from another.)
        if (BuildConfig.DEBUG && intent.getBooleanExtra("dingus_test_loop", false)) panel.postDelayed({
            startActivity(android.content.Intent(this, MoveActivity::class.java).putExtra(SEND, false)
                .putExtra("dingus_test_from", "127.0.0.1:${s.port}").putExtra("dingus_test_no_restart", true))
        }, 1_500)
    }

    private fun sent(to: String) {
        Ui.test("move-sent $to")
        clear()
        title("✓  Sent to $to")
        para("It has your playlists, categories and settings now – it starts again with them in a moment. Everything here stays as it is.")
        buttons(button("Done", primary = true) { finish() })
    }

    // ------------------------------------------------------------------ copying

    private fun startFinding() {
        step = "find"
        finder = Move.Finder(this) { f -> if (found.none { (it.host == f.host || it.name == f.name) && it.port == f.port }) { found.add(f); Ui.test("move-found ${f.name}"); if (step == "find") showFound() } }.also { it.start() }
        showFound()
    }

    private fun showFound() {
        step = "find"
        clear()
        title("Copy from another Fire TV")
        if (found.isEmpty()) {
            para("On the Fire TV that already has Dingus, choose Settings → About → Move to another Fire TV. It'll show up here.")
            waiting("Looking on your Wi-Fi…")
            buttons(button("Cancel", primary = true) { finish() })
            return
        }
        para("Found on your Wi-Fi:")
        var first: View? = null
        for (f in found) {
            val row = choice(f.name, listOfNotNull("Dingus ${f.version}".takeIf { f.version.isNotEmpty() },
                "${f.playlists} playlist${if (f.playlists == 1) "" else "s"}").joinToString("  ·  "), null) { chosen = f; code = ""; askCode(null) }
            if (first == null) first = row
        }
        val f0 = first
        f0?.post { f0.requestFocus() }
    }

    private fun askCode(problem: String?) {
        step = "code"
        val f = chosen ?: return showFound()
        clear()
        title("Copy from ${f.name}")
        para(problem ?: "Type the code it's showing (the number buttons, or the ones below):")
        codeBoxes(code, typing = true)
        // On-screen number keys, for remotes without them.
        val keys = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; clipChildren = false; clipToPadding = false }
        for (n in listOf("1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "⌫")) keys.addView(TextView(this).apply {
            text = n; gravity = Gravity.CENTER; textSize = 16f; setTypeface(typeface, android.graphics.Typeface.BOLD)
            setBackgroundResource(R.drawable.search_key); isFocusable = true; isClickable = true
            setTextColor(resources.getColorStateList(R.color.pill_text, theme))
            setOnClickListener { if (n == "⌫") back() else digit(n[0]) }
        }, LinearLayout.LayoutParams(dp(40), dp(40)).apply { marginEnd = dp(6) })
        panel.addView(keys, lp(18))
        val focusAt: View? = keys.getChildAt(code.lastOrNull()?.let { if (it == '0') 9 else it - '1' } ?: 0)
        focusAt?.post { focusAt.requestFocus() }
    }

    private fun digit(c: Char) {
        if (step != "code" || code.length >= 4) return
        code += c
        if (code.length == 4) chooseWhat() else askCode(null)
    }

    private fun back() { if (step == "code" && code.isNotEmpty()) { code = code.dropLast(1); askCode(null) } }

    private fun chooseWhat() {
        step = "what"
        val f = chosen ?: return showFound()
        clear()
        title("Copy from ${f.name}")
        choice("Playlists and logins", "With their backup servers", true) {}
        choice("Categories and channels", "Hidden, reordered and renamed; favourites", true) {}
        choice("Settings", "Guide, playback, look, VPN set-up", true) {}
        choice("Watch progress and My list", "Continue watching picks up where you were", bringProgress) { bringProgress = !bringProgress; chooseWhat() }
        choice("Reminders", if (f.reminders > 0) "${f.reminders} set" else "None set", bringReminders) { bringReminders = !bringReminders; chooseWhat() }
        para("Anything already on this Fire TV is replaced.", top = 14)
        buttons(button("Copy", primary = true) { copy() }, button("Cancel") { finish() })
    }

    private fun copy() {
        val f = chosen ?: return
        step = "copying"
        clear()
        title("Copying from ${f.name}…")
        waiting("A moment…")
        scope.launch {
            try {
                val data = withContext(Dispatchers.IO) { Move.fetch(this@MoveActivity, f, code, bringProgress, bringReminders) }
                withContext(Dispatchers.IO) { Move.import(this@MoveActivity, data) }
                Ui.test("move-copied ${data.optJSONObject("main")?.length()}")
                getSharedPreferences("dingus", MODE_PRIVATE).edit().putString("movedFrom", f.name).commit() // (for the note when it starts again)
                if (BuildConfig.DEBUG && intent.getBooleanExtra("dingus_test_no_restart", false)) { finish(); return@launch }
                Move.restart(this@MoveActivity)
            } catch (e: Move.WrongCode) {
                code = ""; askCode("That code doesn't match – check the one on ${f.name} and type it again:")
            } catch (e: Exception) {
                showProblem("Couldn't copy: ${Net.friendly(e)}")
            }
        }
    }

    private fun showProblem(t: String) {
        step = "problem"
        clear()
        title("Something went wrong")
        para(t)
        buttons(button("Try again", primary = true) { if (intent.getBooleanExtra(SEND, true)) startSending() else showFound() }, button("Close") { finish() })
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (backHold(event)) return true
        if (step == "code" && event.action == KeyEvent.ACTION_DOWN && event.keyCode in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9) {
            digit('0' + (event.keyCode - KeyEvent.KEYCODE_0)); return true
        }
        if (step == "code" && event.action == KeyEvent.ACTION_DOWN && event.keyCode == KeyEvent.KEYCODE_DEL) { back(); return true }
        // BACK while typing the code takes off a digit; with none (or choosing what to bring), back to the Fire TVs found.
        if (event.keyCode == KeyEvent.KEYCODE_BACK && step in setOf("code", "what")) {
            if (event.action == KeyEvent.ACTION_UP) { if (step == "code" && code.isNotEmpty()) back() else showFound() }
            return true
        }
        return super.dispatchKeyEvent(event)
    }
}
