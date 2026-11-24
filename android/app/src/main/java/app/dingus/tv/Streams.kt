package app.dingus.tv

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.os.Handler
import android.os.Looper
import android.util.Log

/**
 * Closing streams cleanly before the connection under them changes. A provider counts a stream as "in use" until it
 * sees it close: one that's simply cut off (the VPN switching server, the Wi-Fi changing) can keep a 1-connection
 * plan "full" for several minutes. So whatever's playing closes its stream properly first, over the old connection,
 * and starts again once the new one is up.
 *
 * Leaving Dingus (the Home button, or closing it) already closes the stream: the screens let go of their players
 * when they stop.
 */
object Streams {
    /** Something playing: [close] lets go of the stream (keeping its place); [reopen] starts it again. */
    class Playing(val close: () -> Unit, val reopen: () -> Unit)

    private val playing = LinkedHashSet<Playing>()
    private val main = Handler(Looper.getMainLooper())
    private var closed = false

    fun add(p: Playing) { synchronized(playing) { playing.add(p) } }
    fun remove(p: Playing) { synchronized(playing) { playing.remove(p) } }

    private fun all() = synchronized(playing) { playing.toList() }

    /** Closes every stream now (on the main thread; waits for it when called from elsewhere, up to a second). */
    fun closeAll(why: String) {
        val list = all()
        if (list.isEmpty()) return
        Log.i("Dingus", "Closing streams: $why")
        Ui.test("streams-closed $why")
        closed = true
        if (Looper.myLooper() == Looper.getMainLooper()) { list.forEach { runCatching { it.close() } }; return }
        val done = java.util.concurrent.CountDownLatch(1)
        main.post { list.forEach { runCatching { it.close() } }; done.countDown() }
        done.await(1, java.util.concurrent.TimeUnit.SECONDS)
        Thread.sleep(250) // a moment for the close to reach the provider before the connection changes
    }

    /** Starts them again on the new connection (after a moment for it to settle). */
    fun reopenAll(delayMs: Long = 1_200) {
        if (!closed) return
        closed = false
        main.postDelayed({ all().forEach { runCatching { it.reopen() } }; Ui.test("streams-reopened") }, delayMs)
    }

    /** Watches the Fire TV's own network (the Wi-Fi or cable changing, or dropping): close, then reopen when it's back. */
    fun watchNetwork(ctx: Context) {
        val cm = ctx.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return
        if (android.os.Build.VERSION.SDK_INT < 24) return
        var current: Network? = null
        try {
            cm.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) {
                    val before = current
                    current = network
                    if (before != null && before != network) { closeAll("network changed"); reopenAll() } else reopenAll()
                }
                override fun onLost(network: Network) {
                    if (network == current) { current = null; closeAll("network lost") }
                }
            })
        } catch (e: Exception) { Log.w("Dingus", "network watch", e) }
    }
}
