package app.dingus.tv

import android.app.Application

class DingusApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Store.init(this)
        Repo.init(this)
        Logos.init(this)
    }
}
