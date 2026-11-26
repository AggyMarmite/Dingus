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
            // (A cap in memory too: 30 seconds of a 4K stream is well over 100 MB, which on the Fire Stick could
            // freeze the whole device.)
            .setTargetBufferBytes(if (small) 16 * 1024 * 1024 else 48 * 1024 * 1024)
            .setPrioritizeTimeOverSizeThresholds(false)
            .build()
        // If the first-choice video decoder can't start for a stream (some 4K / Dolby Vision files), try the next one
        // instead of playing the sound over a black picture.
        // Sound the Fire Stick can't decode itself (DTS, TrueHD, and AC-3/E-AC-3 on TVs without it) and pictures its own
        // decoders turn down are decoded in software by FFmpeg instead; the built-in decoders still come first.
        // (Software decoding is for sound only: decoding a picture in software – a 4K one the built-in decoder turned
        // down – locked the Fire Stick up.)
        val renderers = object : io.github.anilbeesetti.nextlib.media3ext.ffdecoder.NextRenderersFactory(ctx) {
            override fun buildVideoRenderers(context: Context, extensionRendererMode: Int, mediaCodecSelector: androidx.media3.exoplayer.mediacodec.MediaCodecSelector,
                                             enableDecoderFallback: Boolean, eventHandler: android.os.Handler,
                                             eventListener: androidx.media3.exoplayer.video.VideoRendererEventListener,
                                             allowedVideoJoiningTimeMs: Long, out: java.util.ArrayList<androidx.media3.exoplayer.Renderer>) {
                super.buildVideoRenderers(context, DefaultRenderersFactory.EXTENSION_RENDERER_MODE_OFF, mediaCodecSelector, enableDecoderFallback,
                    eventHandler, eventListener, allowedVideoJoiningTimeMs, out)
            }
        }
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

    /** What went wrong, in plain words (the error's code goes in the log). */
    fun plain(e: androidx.media3.common.PlaybackException): String {
        val http = (e.cause as? androidx.media3.datasource.HttpDataSource.InvalidResponseCodeException)?.responseCode
        return when (e.errorCode) {
            androidx.media3.common.PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS -> when (http) {
                404, 410 -> "The provider doesn't have this at the moment."
                401, 403 -> "The provider turned it down: all your connections may be in use, or it isn't in your package."
                else -> "The provider isn't sending it right now."
            }
            androidx.media3.common.PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
            androidx.media3.common.PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT -> "Couldn't reach the provider. Check the internet connection."
            androidx.media3.common.PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED,
            androidx.media3.common.PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
            androidx.media3.common.PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
            androidx.media3.common.PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES -> "Its format isn't supported on this Fire TV."
            else -> "Something went wrong with the stream."
        }
    }

    /** The player couldn't tell what the data is (worth trying it as HLS or DASH). */
    fun unrecognised(e: androidx.media3.common.PlaybackException) =
        e.errorCode == androidx.media3.common.PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED ||
        e.errorCode == androidx.media3.common.PlaybackException.ERROR_CODE_PARSING_CONTAINER_MALFORMED

    /** Whether a stream failed because the server can't be reached (a backup server may be worth trying). */
    /** The server refused the stream (401/403): through a VPN, what a provider answers a location it blocks. */
    fun refused(e: androidx.media3.common.PlaybackException): Boolean {
        val code = (e.cause as? androidx.media3.datasource.HttpDataSource.InvalidResponseCodeException)?.responseCode ?: return false
        return code == 401 || code == 403
    }

    /** Any network failure fetching the stream (couldn't connect, cut off, a bad answer…). */
    fun network(e: androidx.media3.common.PlaybackException): Boolean = e.errorCode in 2000..2999

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
