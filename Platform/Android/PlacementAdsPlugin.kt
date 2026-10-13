package com.dotnative.plugins

import android.app.Activity
import android.app.Application
import android.os.Bundle
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import com.dotnative.runtime.NativeHost
import com.google.android.gms.ads.*
import com.google.android.gms.ads.appopen.AppOpenAd
import com.google.android.ump.*

class PlacementAdsPlugin(private val activity: Activity) {
    private class Entry(val id: String, val slot: Int = -1) {
        var banner: AdView? = null
        var appOpen: AppOpenAd? = null
        var showing = false
        var showReply: PluginReply? = null
        var loadReply: PluginReply? = null
        val disposals = mutableListOf<PluginReply>()
    }

    private val entries = mutableMapOf<String, Entry>()
    private val slots = arrayOfNulls<Entry>(16)
    private val consent = UserMessagingPlatform.getConsentInformation(activity)
    private var underAge = false
    private var configured = false
    private var consentBusy = false
    private var consentUpdated = false
    private var initialized = false
    private var destroyed = false

    init {
        for (slot in slots.indices) NativeHost.registerPlatformView(
            "dotnative.ads.banner.$slot",
            object : NativeHost.PlatformViewFactory {
                override fun create(nodeId: Int): View {
                    val container = FrameLayout(activity)
                    slots[slot]?.banner?.let { banner ->
                        (banner.parent as? ViewGroup)?.removeView(banner)
                        container.addView(banner, FrameLayout.LayoutParams(-1, -1))
                        banner.resume()
                    }
                    return container
                }

                override fun detach(child: View, nodeId: Int) {
                    (child as? ViewGroup)?.let { group ->
                        (group.getChildAt(0) as? AdView)?.pause()
                        group.removeAllViews()
                    }
                }
            },
        )
        val channel = NativeChannels.channel("dotnative.ads.placements")
        channel.handle("configure") { args, reply ->
            if (entries.isNotEmpty() || consentBusy) {
                reply.failure("busy", "Configure before consent and ad loading")
                return@handle
            }
            val fields = args as? Map<*, *>
            val devices =
                (fields?.get("testDevices") as? List<*>)?.filterIsInstance<String>() ?: emptyList()
            underAge = fields?.get("underAge") as? Boolean ?: false
            MobileAds.setRequestConfiguration(
                RequestConfiguration.Builder()
                    .setTestDeviceIds(devices)
                    .setTagForUnderAgeOfConsent(
                        if (underAge) RequestConfiguration.TAG_FOR_UNDER_AGE_OF_CONSENT_TRUE
                        else RequestConfiguration.TAG_FOR_UNDER_AGE_OF_CONSENT_UNSPECIFIED
                    )
                    .build()
            )
            configured = true
            reply.success()
        }
        channel.handle("consentStatus") { _, reply -> status(reply) }
        channel.handle("gatherConsent") { _, reply ->
            if (!configured || consentBusy) {
                reply.failure("busy", "Configure first; consent flow must be exclusive")
                return@handle
            }
            consentBusy = true
            var cancelled = false
            reply.onCancel = { cancelled = true }
            consent.requestConsentInfoUpdate(
                activity,
                ConsentRequestParameters.Builder().setTagForUnderAgeOfConsent(underAge).build(),
                {
                    if (destroyed) {
                        consentBusy = false
                        reply.failure("unavailable", "Activity destroyed")
                        return@requestConsentInfoUpdate
                    }
                    consentUpdated = true
                    if (cancelled) {
                        consentBusy = false
                        return@requestConsentInfoUpdate
                    }
                    UserMessagingPlatform.loadAndShowConsentFormIfRequired(activity) { error ->
                        consentBusy = false
                        if (!cancelled) {
                            if (error != null) reply.failure("consent_failed", error.message)
                            else status(reply)
                        }
                    }
                },
                { error ->
                    consentBusy = false
                    if (!cancelled) reply.failure("consent_failed", error.message)
                },
            )
        }
        channel.handle("privacyOptions") { _, reply ->
            if (!consentUpdated || consentBusy) {
                reply.failure("busy", "Gather consent first")
                return@handle
            }
            if (
                consent.privacyOptionsRequirementStatus !=
                    ConsentInformation.PrivacyOptionsRequirementStatus.REQUIRED
            ) {
                status(reply)
                return@handle
            }
            consentBusy = true
            var cancelled = false
            reply.onCancel = { cancelled = true }
            UserMessagingPlatform.showPrivacyOptionsForm(activity) { error ->
                consentBusy = false
                if (!cancelled) {
                    if (error != null) reply.failure("consent_failed", error.message)
                    else status(reply)
                }
            }
        }
        channel.handle("initialize") { _, reply ->
            if (!allowed(reply)) return@handle
            MobileAds.initialize(activity) {
                activity.runOnUiThread {
                    if (destroyed) reply.failure("unavailable", "Activity destroyed")
                    else {
                        initialized = true
                        reply.success()
                    }
                }
            }
        }
        channel.handle("loadBanner") { args, reply ->
            val fields = args as? Map<*, *>
            val id = fields?.get("id") as? String
            val unit = fields?.get("adUnitId") as? String
            if (!ready(reply) || !valid(id, unit, reply)) return@handle
            val slot = slots.indexOfFirst { it == null }
            if (slot < 0) {
                reply.failure("capacity", "At most 16 live banner placements are supported")
                return@handle
            }
            val entry = Entry(id!!, slot)
            entry.loadReply = reply
            entries[id] = entry
            slots[slot] = entry
            val banner = AdView(activity)
            entry.banner = banner
            banner.adUnitId = unit!!
            banner.setAdSize(AdSize.LARGE_BANNER)
            reply.onCancel = { remove(entry) }
            banner.adListener =
                object : AdListener() {
                    private var loaded = false

                    override fun onAdLoaded() {
                        if (entries[id] === entry && !loaded) {
                            loaded = true
                            entry.loadReply = null
                            reply.success(slot)
                        }
                    }

                    override fun onAdFailedToLoad(error: LoadAdError) {
                        if (entries[id] === entry && !loaded) {
                            entry.loadReply = null
                            remove(entry)
                            reply.failure("ad_failed", error.message)
                        }
                    }
                }
            banner.loadAd(AdRequest.Builder().build())
        }
        channel.handle("loadAppOpen") { args, reply ->
            val fields = args as? Map<*, *>
            val id = fields?.get("id") as? String
            val unit = fields?.get("adUnitId") as? String
            if (!ready(reply) || !valid(id, unit, reply)) return@handle
            if (entries.values.count { it.slot < 0 } >= 4) {
                reply.failure("capacity", "At most four cached app-open ads are supported")
                return@handle
            }
            val entry = Entry(id!!)
            entry.loadReply = reply
            entries[id] = entry
            reply.onCancel = { remove(entry) }
            AppOpenAd.load(
                activity,
                unit!!,
                AdRequest.Builder().build(),
                object : AppOpenAd.AppOpenAdLoadCallback() {
                    override fun onAdLoaded(ad: AppOpenAd) {
                        if (entries[id] === entry) {
                            entry.appOpen = ad
                            entry.loadReply = null
                            reply.success()
                        }
                    }

                    override fun onAdFailedToLoad(error: LoadAdError) {
                        if (entries[id] === entry) {
                            entry.loadReply = null
                            remove(entry)
                            reply.failure("ad_failed", error.message)
                        }
                    }
                },
            )
        }
        channel.handle("showAppOpen") { args, reply ->
            if (!ready(reply)) return@handle
            val entry = entries[(args as? Map<*, *>)?.get("id") as? String]
            val ad = entry?.appOpen
            if (entry == null || ad == null || entries.values.any { it.showing }) {
                reply.failure("unavailable", "No eligible app-open ad")
                return@handle
            }
            entry.showing = true
            entry.showReply = reply
            // Google owns fullscreen UI: cancellation suppresses completion; disposal waits for
            // dismissal.
            reply.onCancel = { entry.showReply = null }
            ad.fullScreenContentCallback =
                object : FullScreenContentCallback() {
                    override fun onAdDismissedFullScreenContent() {
                        entry.showing = false
                        entry.showReply?.success()
                        remove(entry)
                    }

                    override fun onAdFailedToShowFullScreenContent(error: AdError) {
                        entry.showing = false
                        entry.showReply?.failure("ad_failed", error.message)
                        remove(entry)
                    }
                }
            ad.show(activity)
        }
        channel.handle("dispose") { args, reply ->
            val entry = entries[(args as? Map<*, *>)?.get("id") as? String]
            if (entry == null) reply.success()
            else if (entry.showing) entry.disposals.add(reply)
            else {
                remove(entry)
                reply.success()
            }
        }
        activity.application.registerActivityLifecycleCallbacks(
            object : Application.ActivityLifecycleCallbacks {
                override fun onActivityStopped(a: Activity) {
                    if (a === activity)
                        entries.values.filter { !it.showing }.toList().forEach(::remove)
                }

                override fun onActivityDestroyed(a: Activity) {
                    if (a === activity) {
                        destroyed = true
                        entries.values.toList().forEach {
                            it.showing = false
                            it.showReply?.failure("unavailable", "Activity destroyed")
                            remove(it)
                        }
                        activity.application.unregisterActivityLifecycleCallbacks(this)
                    }
                }

                override fun onActivityCreated(a: Activity, state: Bundle?) {}

                override fun onActivityStarted(a: Activity) {}

                override fun onActivityResumed(a: Activity) {}

                override fun onActivityPaused(a: Activity) {}

                override fun onActivitySaveInstanceState(a: Activity, state: Bundle) {}
            }
        )
    }

    private fun valid(id: String?, unit: String?, reply: PluginReply): Boolean {
        if (
            id.isNullOrBlank() || id.length > 64 || unit.isNullOrBlank() || entries.containsKey(id)
        ) {
            reply.failure("invalid_argument", "Unique id and ad unit required")
            return false
        }
        return true
    }

    private fun status(reply: PluginReply) =
        reply.success(
            mapOf(
                "canRequestAds" to (consentUpdated && consent.canRequestAds()),
                "privacyOptionsRequired" to
                    (consent.privacyOptionsRequirementStatus ==
                        ConsentInformation.PrivacyOptionsRequirementStatus.REQUIRED),
            )
        )

    private fun allowed(reply: PluginReply): Boolean {
        if (
            destroyed || !configured || !consentUpdated || consentBusy || !consent.canRequestAds()
        ) {
            reply.failure("consent_required", "Gather consent before requesting ads")
            return false
        }
        return true
    }

    private fun ready(reply: PluginReply): Boolean {
        if (!allowed(reply)) return false
        if (!initialized) {
            reply.failure("not_initialized", "Initialize after gathering consent")
            return false
        }
        return true
    }

    private fun remove(entry: Entry) {
        if (entries[entry.id] !== entry) return
        entries.remove(entry.id)
        entry.loadReply?.failure("cancelled", "Ad load disposed")
        entry.loadReply = null
        entry.banner?.let {
            (it.parent as? ViewGroup)?.removeView(it)
            it.adListener = object : AdListener() {}
            it.destroy()
        }
        entry.banner = null
        entry.appOpen = null
        if (entry.slot >= 0) slots[entry.slot] = null
        entry.disposals.forEach { it.success() }
        entry.disposals.clear()
        entry.showReply = null
    }
}
