package com.dealgosa.app

import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import android.os.Bundle
import io.flutter.embedding.android.FlutterActivity

class MainActivity : FlutterActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        createNotificationChannel()
    }

    /**
     * The channel push notifications arrive on. Android 8 and newer show nothing on a channel that
     * does not exist, and its name is what people see in the phone's notification settings.
     * Creating it again is harmless: Android keeps the existing one and the person's choices.
     */
    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return

        val channel = NotificationChannel(
            "dealgo_alerts",
            "تنبيهات ديلقو",
            NotificationManager.IMPORTANCE_HIGH,
        ).apply {
            description = "المزادات والجوائز والطلبات والمحفظة"
        }

        getSystemService(NotificationManager::class.java)?.createNotificationChannel(channel)
    }
}
