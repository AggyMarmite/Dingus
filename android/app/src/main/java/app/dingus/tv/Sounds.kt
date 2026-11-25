package app.dingus.tv

import android.content.Context
import android.media.AudioAttributes
import android.media.SoundPool
import android.view.KeyEvent
import java.io.File
import java.io.FileOutputStream
import kotlin.math.exp
import kotlin.math.ln

/**
 * Soft sounds (Settings → Look → Sounds): "Felt" – a muffled, rounded tap as you move, a soft two-note press for OK,
 * the same falling for BACK. Three strengths (soft, softer, faintest), or off (the default). Made here (not
 * recordings): a triangle wave, muffled, with a quick fade.
 */
object Sounds {
    /** Off, then the three strengths: volume, how muffled (low-pass Hz), and length. */
    val names = listOf("Off", "Felt · soft", "Felt · softer", "Felt · faintest")
    private val levels = listOf(Triple(0.022f, 700f, 0.75f), Triple(0.013f, 560f, 0.65f), Triple(0.007f, 450f, 0.55f))

    private var pool: SoundPool? = null
    private var loadedFor = -1
    private var move = 0; private var ok = 0; private var back = 0

    private const val RATE = 44_100

    /** Gets the sounds ready for the strength chosen (once; again if it's changed). */
    fun prepare(ctx: Context) {
        val level = Store.sounds
        if (level <= 0 || level == loadedFor) return
        val (v, lp, len) = levels[(level - 1).coerceIn(0, 2)]
        val p = pool ?: SoundPool.Builder().setMaxStreams(3)
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ASSISTANCE_SONIFICATION)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build()).build().also { pool = it }
        fun make(name: String, notes: List<Pair<Float, Float>>): Int {
            val f = File(ctx.cacheDir, "sound-$level-$name.wav")
            if (!f.exists()) f.writeBytes(wav(notes, v, lp, len))
            return p.load(f.path, 1)
        }
        move = make("move", listOf(300f to 0f))
        ok = make("ok", listOf(260f to 0f, 390f to 0.04f))
        back = make("back", listOf(390f to 0f, 260f to 0.04f))
        loadedFor = level
    }

    /** A key on any screen (called once per key press, from the screen's key handling). */
    fun key(ctx: Context, e: KeyEvent) {
        if (Store.sounds <= 0 || e.action != KeyEvent.ACTION_DOWN || e.repeatCount != 0) return
        val id = when (e.keyCode) {
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT -> move
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> ok
            KeyEvent.KEYCODE_BACK -> back
            else -> return
        }
        if (loadedFor != Store.sounds) { prepare(ctx); return }
        if (id != 0) pool?.play(id, 1f, 1f, 1, 0, 1f)
    }

    /** One of them, to hear it (Settings, when you pick a strength). */
    fun sample(ctx: Context) { prepare(ctx); android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({ pool?.play(ok, 1f, 1f, 1, 0, 1f) }, 250) }

    /** Notes (pitch, start in seconds) as a 16-bit mono WAV: triangle waves, muffled, each fading out quickly. */
    private fun wav(notes: List<Pair<Float, Float>>, v: Float, lp: Float, len: Float): ByteArray {
        val d = 0.05f * len * 1.6f // each note's length (seconds)
        val total = ((notes.maxOf { it.second } + d + 0.02f) * RATE).toInt()
        val mix = FloatArray(total)
        val k = exp(-2.0 * Math.PI * lp / RATE).toFloat() // one-pole low-pass
        for ((f, t0) in notes) {
            val start = (t0 * RATE).toInt(); val n = (d * RATE).toInt(); val attack = (0.006f * RATE).toInt()
            var y = 0f
            val fall = ln(0.0001f.toDouble()) / (n - attack).coerceAtLeast(1)
            for (i in 0 until n) {
                val ph = (i * f / RATE) % 1f
                val tri = 4f * kotlin.math.abs(ph - 0.5f) - 1f
                y = (1 - k) * tri + k * y
                val env = if (i < attack) i / attack.toFloat() else exp(fall * (i - attack)).toFloat()
                if (start + i < total) mix[start + i] += y * env * v * 6f // (the low-pass takes the level down; this brings it back)
            }
        }
        val out = java.io.ByteArrayOutputStream()
        fun int(x: Int) { out.write(x and 0xFF); out.write(x shr 8 and 0xFF); out.write(x shr 16 and 0xFF); out.write(x shr 24 and 0xFF) }
        fun short(x: Int) { out.write(x and 0xFF); out.write(x shr 8 and 0xFF) }
        out.write("RIFF".toByteArray()); int(36 + total * 2); out.write("WAVEfmt ".toByteArray())
        int(16); short(1); short(1); int(RATE); int(RATE * 2); short(2); short(16)
        out.write("data".toByteArray()); int(total * 2)
        for (s in mix) short((s.coerceIn(-1f, 1f) * 32767).toInt())
        return out.toByteArray()
    }
}
