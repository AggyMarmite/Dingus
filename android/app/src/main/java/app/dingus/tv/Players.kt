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
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory

/** Counts the bytes the player downloads, for the bitrate in the stream stats. */
@OptIn(UnstableApi::class)
object StreamMeter : androidx.media3.datasource.TransferListener {
    @Volatile var bytes = 0L; private set
    override fun onTransferInitializing(source: androidx.media3.datasource.DataSource, dataSpec: androidx.media3.datasource.DataSpec, isNetwork: Boolean) {}
    override fun onTransferStart(source: androidx.media3.datasource.DataSource, dataSpec: androidx.media3.datasource.DataSpec, isNetwork: Boolean) {}
    override fun onBytesTransferred(source: androidx.media3.datasource.DataSource, dataSpec: androidx.media3.datasource.DataSpec, isNetwork: Boolean, bytesTransferred: Int) { bytes += bytesTransferred }
    override fun onTransferEnd(source: androidx.media3.datasource.DataSource, dataSpec: androidx.media3.datasource.DataSpec, isNetwork: Boolean) {}
}

/** One place that makes the video player, for full screen and the guide's preview. */
@OptIn(UnstableApi::class)
object Players {
    /** The video decoder the player last started (for the "no picture" note). */
    @Volatile var lastDecoder = ""

    fun create(ctx: Context, small: Boolean = false): ExoPlayer {
        val http = DefaultHttpDataSource.Factory()
            .setUserAgent(Net.USER_AGENT)
            .setAllowCrossProtocolRedirects(true)
            .setConnectTimeoutMs(12_000)
            .setReadTimeoutMs(15_000)
            .setTransferListener(StreamMeter)
        // Modest buffers: quick to start, and kind to the Fire Stick's memory.
        val load = DefaultLoadControl.Builder()
            .setBufferDurationsMs(if (small) 4_000 else 8_000, if (small) 12_000 else 30_000, 1_000, 2_000)
            .setPrioritizeTimeOverSizeThresholds(true)
            .build()
        // If the first-choice video decoder can't start for a stream (some 4K / Dolby Vision files), try the next one
        // instead of playing the sound over a black picture.
        // Sound the Fire Stick can't decode itself (DTS, TrueHD, and AC-3/E-AC-3 on TVs without it) and pictures its own
        // decoders turn down are decoded in software by FFmpeg instead; the built-in decoders still come first.
        val renderers = io.github.anilbeesetti.nextlib.media3ext.ffdecoder.NextRenderersFactory(ctx)
            .setExtensionRendererMode(DefaultRenderersFactory.EXTENSION_RENDERER_MODE_ON)
            .setEnableDecoderFallback(true)
        val p = ExoPlayer.Builder(ctx, renderers)
            .setMediaSourceFactory(DefaultMediaSourceFactory(http))
            .setLoadControl(load)
            .build()
        p.videoScalingMode = C.VIDEO_SCALING_MODE_SCALE_TO_FIT
        if (!small && Store.matchFrameRate) noAutoFrameRate(p)
        subtitles(p)
        p.addAnalyticsListener(object : androidx.media3.exoplayer.analytics.AnalyticsListener {
            override fun onVideoDecoderInitialized(eventTime: androidx.media3.exoplayer.analytics.AnalyticsListener.EventTime,
                                                   decoderName: String, initializedTimestampMs: Long, initializationDurationMs: Long) {
                lastDecoder = decoderName
                android.util.Log.i("Dingus", "Video decoder: $decoderName")
                Ui.test("video-decoder $decoderName")
            }
            override fun onVideoCodecError(eventTime: androidx.media3.exoplayer.analytics.AnalyticsListener.EventTime, videoCodecError: Exception) {
                android.util.Log.w("Dingus", "Video codec error", videoCodecError)
                Ui.test("video-codec-error ${videoCodecError.message}")
            }
        })
        return p
    }

    /**
     * Subtitles for films, episodes and live TV: in your language when that's switched on (and there are some
     * in it), otherwise off to start with. You can still pick others while watching (≡ → Audio & subtitles).
     */
    fun subtitles(p: ExoPlayer) {
        val b = p.trackSelectionParameters.buildUpon()
        if (Store.autoSubtitles) {
            b.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
                .setPreferredTextLanguage(Store.subtitleLanguage)
                .setSelectUndeterminedTextLanguage(false)
                .setIgnoredTextSelectionFlags(C.SELECTION_FLAG_DEFAULT) // only your language, not whatever the file marks as default
        } else b.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
        p.trackSelectionParameters = b.build()
    }

    fun item(c: Channel): MediaItem = item(liveUrl(c))

    /**
     * What the address is, going by its ending: HLS, DASH or Smooth Streaming (null = a plain file or stream, which
     * the player works out from the data itself: MP4, MKV, WebM, TS, AVI, FLV, MP3, FLAC, Ogg …).
     */
    fun typeOf(url: String): String? {
        val path = url.substringBefore('?').substringBefore('#').lowercase()
        return when {
            path.endsWith(".m3u8") || url.contains("type=m3u8", true) || url.contains("output=m3u8", true) || url.contains("output=hls", true) -> MimeTypes.APPLICATION_M3U8
            path.endsWith(".mpd") -> MimeTypes.APPLICATION_MPD
            path.endsWith(".ism") || path.endsWith(".isml") || path.endsWith("/manifest") -> MimeTypes.APPLICATION_SS
            else -> null
        }
    }

    /** [guess] after the player didn't recognise the data: 1 = treat it as HLS, 2 = as DASH. */
    fun item(url: String, guess: Int = 0): MediaItem {
        val b = MediaItem.Builder().setUri(Uri.parse(url))
        when (guess) {
            1 -> MimeTypes.APPLICATION_M3U8
            2 -> MimeTypes.APPLICATION_MPD
            else -> typeOf(url)
        }?.let { b.setMimeType(it) }
        return b.build()
    }

    /** The player couldn't tell what the data is (worth trying it as HLS or DASH). */
    fun unrecognised(e: androidx.media3.common.PlaybackException) =
        e.errorCode == androidx.media3.common.PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED ||
        e.errorCode == androidx.media3.common.PlaybackException.ERROR_CODE_PARSING_CONTAINER_MALFORMED

    /** Whether a stream failed because the server can't be reached (a backup server may be worth trying). */
    fun serverDown(e: androidx.media3.common.PlaybackException): Boolean {
        if (e.errorCode == androidx.media3.common.PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED ||
            e.errorCode == androidx.media3.common.PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT) return true
        val code = (e.cause as? androidx.media3.datasource.HttpDataSource.InvalidResponseCodeException)?.responseCode ?: return false
        return code >= 500 || code == 404
    }

    /** The channel's address in the chosen stream format (Xtream logins offer MPEG-TS and HLS). */
    fun liveUrl(c: Channel): String = Failover.rewrite(liveUrlRaw(c))

    private fun liveUrlRaw(c: Channel): String =
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
