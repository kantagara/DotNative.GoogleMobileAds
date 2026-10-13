package com.dotnative.plugins

import android.app.Activity
import android.content.pm.PackageManager
import android.view.Gravity
import android.view.ViewGroup
import android.widget.FrameLayout
import com.google.android.gms.ads.AdError
import com.google.android.gms.ads.AdListener
import com.google.android.gms.ads.AdRequest
import com.google.android.gms.ads.AdSize
import com.google.android.gms.ads.AdView
import com.google.android.gms.ads.FullScreenContentCallback
import com.google.android.gms.ads.LoadAdError
import com.google.android.gms.ads.MobileAds
import com.google.android.gms.ads.appopen.AppOpenAd

class MobileAdsPlugin(private val activity: Activity) {
    private var appOpen: AppOpenAd? = null
    private var banner: AdView? = null

    init {
        val channel = NativeChannels.channel("dotnative.ads")
        channel.handle("initialize") { args, reply ->
            val expected = (args as? Map<*, *>)?.get("applicationId") as? String
            if (expected != null) {
                @Suppress("DEPRECATION")
                val actual = activity.packageManager.getApplicationInfo(
                    activity.packageName, PackageManager.GET_META_DATA
                ).metaData?.getString("com.google.android.gms.ads.APPLICATION_ID")
                if (actual != expected) {
                    reply.failure("configuration_mismatch", "AddAdMobAds application ID does not match AndroidManifest.xml")
                    return@handle
                }
            }
            MobileAds.initialize(activity) { activity.runOnUiThread { reply.success() } }
        }
        channel.handle("loadAppOpen") { args, reply ->
            val adUnitId = (args as? Map<*, *>)?.get("adUnitId") as? String
            if (adUnitId.isNullOrBlank()) {
                reply.failure("invalid_argument", "An ad unit id is required")
                return@handle
            }
            AppOpenAd.load(
                activity,
                adUnitId,
                AdRequest.Builder().build(),
                object : AppOpenAd.AppOpenAdLoadCallback() {
                    override fun onAdLoaded(ad: AppOpenAd) {
                        appOpen = ad
                        activity.runOnUiThread { reply.success() }
                    }

                    override fun onAdFailedToLoad(error: LoadAdError) {
                        activity.runOnUiThread { reply.failure("ad_failed", error.message) }
                    }
                },
            )
        }
        channel.handle("showAppOpen") { _, reply ->
            val ad = appOpen
            if (ad == null) {
                reply.failure("unavailable", "No app-open ad is loaded")
                return@handle
            }
            appOpen = null
            ad.fullScreenContentCallback =
                object : FullScreenContentCallback() {
                    override fun onAdDismissedFullScreenContent() {
                        reply.success()
                    }

                    override fun onAdFailedToShowFullScreenContent(error: AdError) {
                        reply.failure("ad_failed", error.message)
                    }
                }
            ad.show(activity)
        }
        channel.handle("showBanner") { args, reply ->
            val adUnitId = (args as? Map<*, *>)?.get("adUnitId") as? String
            if (adUnitId.isNullOrBlank()) {
                reply.failure("invalid_argument", "An ad unit id is required")
                return@handle
            }
            activity.runOnUiThread {
                removeBanner()
                val view = AdView(activity)
                view.adUnitId = adUnitId
                view.setAdSize(AdSize.LARGE_BANNER)
                val content = activity.window.decorView.findViewById<ViewGroup>(android.R.id.content)
                content.addView(
                    view,
                    FrameLayout.LayoutParams(
                        FrameLayout.LayoutParams.WRAP_CONTENT,
                        FrameLayout.LayoutParams.WRAP_CONTENT,
                        Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL,
                    ),
                )
                view.adListener =
                    object : AdListener() {
                        override fun onAdLoaded() {
                            reply.success()
                        }

                        override fun onAdFailedToLoad(error: LoadAdError) {
                            removeBanner()
                            reply.failure("ad_failed", error.message)
                        }
                    }
                banner = view
                view.loadAd(AdRequest.Builder().build())
            }
        }
        channel.handle("hideBanner") { _, reply ->
            activity.runOnUiThread {
                removeBanner()
                reply.success()
            }
        }
    }

    private fun removeBanner() {
        val view = banner ?: return
        (view.parent as? ViewGroup)?.removeView(view)
        view.destroy()
        banner = null
    }
}
