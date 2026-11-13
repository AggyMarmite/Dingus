package app.dingus.tv

import android.app.Application

class DingusApp : Application() {
    override fun onTrimMemory(level: Int) {
        super.onTrimMemory(level)
        if (level >= TRIM_MEMORY_RUNNING_LOW) { Posters.trim(); Logos.trim() }
    }

    override fun onCreate() {
        super.onCreate()
        Store.init(this)
        Repo.init(this)
        Logos.init(this)
        Posters.init(this)
        Vod.init(this)
        Vpn.init(this)
        // Pause/rewind files left over from last time (e.g. if the app was closed while watching).
        Thread { java.io.File(cacheDir, "timeshift").deleteRecursively() }.start()
    }
}
