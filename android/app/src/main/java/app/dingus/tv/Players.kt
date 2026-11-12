package app.dingus.tv

import android.content.Context
import android.net.Uri
import androidx.annotation.OptIn
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory

/** One place that makes the video player, for full screen and the guide's preview. */
@OptIn(UnstableApi::class)
object Players {
    fun create(ctx: Context, small: Boolean = false): ExoPlayer {
        val http = DefaultHttpDataSource.Factory()
            .setUserAgent(Net.USER_AGENT)
            .setAllowCrossProtocolRedirects(true)
            .setConnectTimeoutMs(12_000)
            .setReadTimeoutMs(15_000)
        // Modest buffers: quick to start, and kind to the Fire Stick's memory.
        val load = DefaultLoadControl.Builder()
            .setBufferDurationsMs(if (small) 4_000 else 8_000, if (small) 12_000 else 30_000, 1_000, 2_000)
            .setPrioritizeTimeOverSizeThresholds(true)
            .build()
        val p = ExoPlayer.Builder(ctx)
            .setMediaSourceFactory(DefaultMediaSourceFactory(http))
            .setLoadControl(load)
            .build()
        p.videoScalingMode = C.VIDEO_SCALING_MODE_SCALE_TO_FIT
        if (!small && Store.matchFrameRate) noAutoFrameRate(p)
        return p
    }

    fun item(c: Channel): MediaItem = item(liveUrl(c))

    fun item(url: String): MediaItem {
        val b = MediaItem.Builder().setUri(Uri.parse(url))
        if (url.contains(".m3u8", ignoreCase = true)) b.setMimeType(MimeTypes.APPLICATION_M3U8)
        return b.build()
    }

    /** The channel's address in the chosen stream format (Xtream logins offer MPEG-TS and HLS). */
    fun liveUrl(c: Channel): String =
        if (Store.streamFormat == "hls" && Store.playlist?.type == "xtream" && c.url.contains("/live/") && c.url.endsWith(".ts"))
            c.url.removeSuffix(".ts") + ".m3u8"
        else c.url

    /** Calls [FrameRate.match] once the video's frame rate is known. */
    fun watchFrameRate(a: android.app.Activity, p: ExoPlayer) {
        p.addListener(object : androidx.media3.common.Player.Listener {
            override fun onTracksChanged(tracks: androidx.media3.common.Tracks) {
                p.videoFormat?.frameRate?.let { FrameRate.match(a, it) }
            }
        })
    }

    /** Hand frame-rate matching to [FrameRate] (it uses the TV's own display modes). */
    fun noAutoFrameRate(p: ExoPlayer) { p.videoChangeFrameRateStrategy = C.VIDEO_CHANGE_FRAME_RATE_STRATEGY_OFF }
}
