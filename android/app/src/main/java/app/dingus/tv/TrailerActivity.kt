package app.dingus.tv

import android.annotation.SuppressLint
import android.content.Intent
import android.graphics.Color
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.webkit.JavascriptInterface
import android.webkit.WebChromeClient
import android.webkit.WebView
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView

/**
 * A trailer, full screen inside Dingus: YouTube's own player in a web view, driven by the remote (OK / ▶❚❚ pause,
 * ◀ ▶ 10 seconds, BACK closes). When it ends you're back on the film's page. A trailer YouTube won't play outside
 * YouTube offers to open there instead.
 */
class TrailerActivity : BaseActivity() {
    companion object {
        const val KEY = "key"
        const val TITLE = "title"

        fun open(a: android.app.Activity, key: String, title: String) =
            a.startActivity(Intent(a, TrailerActivity::class.java).putExtra(KEY, key).putExtra(TITLE, title))
    }

    private lateinit var web: WebView
    private lateinit var bar: ProgressBar
    private lateinit var overlay: View
    private val handler = Handler(Looper.getMainLooper())
    private var key = ""
    private var failed = false

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        key = intent.getStringExtra(KEY) ?: run { finish(); return }
        val d = resources.displayMetrics.density
        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }

        web = WebView(this).apply {
            setBackgroundColor(Color.BLACK)
            settings.javaScriptEnabled = true
            settings.mediaPlaybackRequiresUserGesture = false
            settings.domStorageEnabled = true
            webChromeClient = WebChromeClient()
            isFocusable = false // (the remote's keys come to this screen, which tells the player)
            addJavascriptInterface(Bridge(), "Dingus")
        }
        root.addView(web, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        // Title at the top, the progress and the buttons at the bottom; they fade a few seconds after a key press.
        val box = FrameLayout(this)
        box.addView(TextView(this).apply {
            text = "Trailer   " + (intent.getStringExtra(TITLE) ?: "")
            textSize = 18f; setTextColor(Color.WHITE); setTypeface(null, android.graphics.Typeface.BOLD)
            setPadding((40 * d).toInt(), (24 * d).toInt(), (40 * d).toInt(), (40 * d).toInt())
            background = android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(0xCC000000.toInt(), 0))
        }, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP))
        val bottom = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding((40 * d).toInt(), (40 * d).toInt(), (40 * d).toInt(), (24 * d).toInt())
            background = android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.BOTTOM_TOP, intArrayOf(0xCC000000.toInt(), 0))
        }
        bar = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 1000; progressDrawable = getDrawable(R.drawable.progress_white)
        }
        bottom.addView(bar, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, (4 * d).toInt()))
        bottom.addView(TextView(this).apply {
            text = "OK  Pause        ◀ ▶  10 seconds        BACK  Back to the film"
            textSize = 14f; setTextColor(0xFFE2E8F0.toInt())
            setPadding(0, (10 * d).toInt(), 0, 0)
        })
        box.addView(bottom, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM))
        overlay = box
        root.addView(box, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        setContentView(root)

        // YouTube's player wants to know which site it's on, so the page is given one.
        web.loadDataWithBaseURL("https://dingus.app/", page(key), "text/html", "utf-8", null)
        showOverlay()
        handler.post(tick)
        Ui.test("trailer-open $key")
    }

    private fun page(k: String) = """
        <!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1">
        <style>html,body{margin:0;height:100%;background:#000;overflow:hidden}#p{position:absolute;inset:0;width:100%;height:100%}</style></head>
        <body><div id="p"></div>
        <script src="https://www.youtube.com/iframe_api"></script>
        <script>
        var player;
        function onYouTubeIframeAPIReady(){
          player = new YT.Player('p', {videoId:'$k', host:'https://www.youtube-nocookie.com',
            playerVars:{autoplay:1, controls:0, rel:0, modestbranding:1, playsinline:1, iv_load_policy:3, fs:0, disablekb:1, origin:'https://dingus.app'},
            events:{
              onReady:function(e){ e.target.playVideo(); Dingus.state(-2); },
              onStateChange:function(e){ Dingus.state(e.data); },
              onError:function(e){ Dingus.error(e.data); }
            }});
        }
        function pos(){ try{ return player.getCurrentTime() + '/' + player.getDuration(); }catch(x){ return ''; } }
        function toggle(){ try{ if(player.getPlayerState()==1) player.pauseVideo(); else player.playVideo(); }catch(x){} }
        function skip(s){ try{ player.seekTo(Math.max(0, player.getCurrentTime()+s), true); }catch(x){} }
        </script></body></html>
    """.trimIndent()

    private inner class Bridge {
        @JavascriptInterface fun state(s: Int) {
            Ui.test("trailer-state $s")
            if (s == 0) runOnUiThread { finish() } // ended
        }
        @JavascriptInterface fun error(code: Int) {
            Ui.test("trailer-error $code")
            if (failed) return
            failed = true
            runOnUiThread { notHere() }
        }
    }

    /** YouTube won't play this one outside YouTube: offer it there (Silk or the YouTube app), as before. */
    private fun notHere() {
        if (isFinishing) return
        Dialogs.builder(this).setTitle("Trailer")
            .setMessage("This trailer can only be watched on YouTube itself.")
            .setPositiveButton("Open on YouTube") { _, _ -> if (!Tmdb.playTrailer(this, key)) Notice.done("No app to play the trailer", 3_000); finish() }
            .setNegativeButton("Back") { _, _ -> finish() }
            .setOnCancelListener { finish() }
            .show()
    }

    private val tick = object : Runnable {
        override fun run() {
            web.evaluateJavascript("pos()") { r ->
                val parts = r.trim('"').split('/')
                val now = parts.getOrNull(0)?.toDoubleOrNull(); val len = parts.getOrNull(1)?.toDoubleOrNull()
                if (now != null && len != null && len > 0) bar.progress = (now / len * 1000).toInt()
            }
            handler.postDelayed(this, 1_000)
        }
    }

    private val hide = Runnable { overlay.animate().alpha(0f).setDuration(400).start() }

    private fun showOverlay() {
        overlay.animate().cancel(); overlay.alpha = 1f
        handler.removeCallbacks(hide); handler.postDelayed(hide, 4_000)
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (backHold(event)) return true
        if (event.action != KeyEvent.ACTION_DOWN) return super.dispatchKeyEvent(event)
        when (event.keyCode) {
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE,
            KeyEvent.KEYCODE_MEDIA_PLAY, KeyEvent.KEYCODE_MEDIA_PAUSE -> { web.evaluateJavascript("toggle()", null); showOverlay(); return true }
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_MEDIA_REWIND -> { web.evaluateJavascript("skip(-10)", null); showOverlay(); return true }
            KeyEvent.KEYCODE_DPAD_RIGHT, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> { web.evaluateJavascript("skip(10)", null); showOverlay(); return true }
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN -> { showOverlay(); return true }
        }
        return super.dispatchKeyEvent(event)
    }

    override fun onPause() { super.onPause(); web.evaluateJavascript("try{player.pauseVideo()}catch(x){}", null) }

    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        if (::web.isInitialized) { web.loadUrl("about:blank"); web.destroy() }
        super.onDestroy()
    }
}
