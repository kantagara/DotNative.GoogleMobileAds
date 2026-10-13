import GoogleMobileAds
import UIKit
import UserMessagingPlatform

@_silgen_name("dotnative_register_platform_view")
private func registerAdsView(
    _ name: UnsafePointer<CChar>, _ length: Int,
    _ create: @convention(c) (UInt32, UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer?,
    _ detach: (
        @convention(c) (UInt32, UnsafeMutableRawPointer?, UnsafeMutableRawPointer?) -> Void
    )?,
    _ rebind: (
        @convention(c) (UInt32, UInt32, UnsafeMutableRawPointer?, UnsafeMutableRawPointer?) -> Void
    )?,
    _ context: UnsafeMutableRawPointer?
) -> Int32

@MainActor final class PlacementAdsPlugin {
    fileprivate static var instance: PlacementAdsPlugin?
    @MainActor private final class Entry {
        let id: String
        let slot: Int
        var banner: BannerView?
        var appOpen: AppOpenAd?
        var bannerDelegate: PlacementBannerDelegate?
        var fullscreenDelegate: PlacementFullScreenDelegate?
        var showing = false
        var loadReply: PluginReply?
        var showReply: PluginReply?
        var disposals: [PluginReply] = []
        init(_ id: String, slot: Int = -1) {
            self.id = id
            self.slot = slot
        }
    }
    private var entries: [String: Entry] = [:]
    private var slots: [Entry?] = Array(repeating: nil, count: 16)
    private var configured = false
    private var underAge = false
    private var consentBusy = false
    private var consentUpdated = false
    private var initialized = false
    private var registered = true
    private var backgroundObserver: NSObjectProtocol?
    static func register() { if instance == nil { instance = PlacementAdsPlugin() } }
    private init() {
        for slot in 0..<16 {
            let name = "dotnative.ads.banner.\(slot)"
            let status = name.withCString {
                registerAdsView(
                    $0, name.utf8.count,
                    { _, context in
                        let address: UInt = MainActor.assumeIsolated {
                            let slot = Int(bitPattern: context) - 1
                            let holder = UIView()
                            if let instance = PlacementAdsPlugin.instance, slot >= 0, slot < 16,
                                let banner = instance.slots[slot]?.banner
                            {
                                banner.removeFromSuperview()
                                banner.frame = holder.bounds
                                banner.autoresizingMask = [.flexibleWidth, .flexibleHeight]
                                holder.addSubview(banner)
                            }
                            return UInt(bitPattern: Unmanaged.passRetained(holder).toOpaque())
                        }
                        return UnsafeMutableRawPointer(bitPattern: address)
                    },
                    { _, view, _ in
                        MainActor.assumeIsolated {
                            if let view {
                                Unmanaged<UIView>.fromOpaque(view).takeUnretainedValue().subviews
                                    .forEach { $0.removeFromSuperview() }
                            }
                        }
                    }, nil, UnsafeMutableRawPointer(bitPattern: slot + 1))
            }
            if status != 0 { registered = false }
        }
        let channel = NativeChannels.channel("dotnative.ads.placements")
        channel.handle("configure") { [weak self] args, reply in
            guard let self, self.registered else {
                reply.failure("unavailable", "Banner native slots unavailable")
                return
            }
            guard self.entries.isEmpty, !self.consentBusy else {
                reply.failure("busy", "Configure before loading ads")
                return
            }
            var devices: [String] = []
            if case .list(let values) = args.fields["testDevices"] {
                devices = values.compactMap { $0.string }
            }
            if case .bool(let underAge) = args.fields["underAge"] { self.underAge = underAge }
            MobileAds.shared.requestConfiguration.testDeviceIdentifiers = devices
            MobileAds.shared.requestConfiguration.tagForUnderAgeOfConsent =
                self.underAge ? true : nil
            self.configured = true
            reply.success()
        }
        channel.handle("consentStatus") { [weak self] _, reply in self?.status(reply) }
        channel.handle("gatherConsent") { [weak self] _, reply in
            guard let self, self.configured, !self.consentBusy,
                let presenter = NativeChannels.presenter
            else {
                reply.failure("busy", "Configure first; consent requires an exclusive presenter")
                return
            }
            self.consentBusy = true
            var cancelled = false
            reply.onCancel = { cancelled = true }
            let parameters = RequestParameters()
            parameters.isTaggedForUnderAgeOfConsent = self.underAge
            ConsentInformation.shared.requestConsentInfoUpdate(with: parameters) {
                [weak self] error in
                Task { @MainActor in
                    guard let self else { return }
                    if let error {
                        self.consentBusy = false
                        if !cancelled {
                            reply.failure("consent_failed", error.localizedDescription)
                        }
                        return
                    }
                    self.consentUpdated = true
                    if cancelled {
                        self.consentBusy = false
                        return
                    }
                    ConsentForm.loadAndPresentIfRequired(from: presenter) { error in
                        Task { @MainActor in
                            self.consentBusy = false
                            if !cancelled {
                                if let error {
                                    reply.failure("consent_failed", error.localizedDescription)
                                } else {
                                    self.status(reply)
                                }
                            }
                        }
                    }
                }
            }
        }
        channel.handle("privacyOptions") { [weak self] _, reply in
            guard let self, self.consentUpdated, !self.consentBusy,
                let presenter = NativeChannels.presenter
            else {
                reply.failure("busy", "Gather consent first")
                return
            }
            guard ConsentInformation.shared.privacyOptionsRequirementStatus == .required else {
                self.status(reply)
                return
            }
            self.consentBusy = true
            var cancelled = false
            reply.onCancel = { cancelled = true }
            ConsentForm.presentPrivacyOptionsForm(from: presenter) { error in
                Task { @MainActor in
                    self.consentBusy = false
                    if !cancelled {
                        if let error {
                            reply.failure("consent_failed", error.localizedDescription)
                        } else {
                            self.status(reply)
                        }
                    }
                }
            }
        }
        channel.handle("initialize") { [weak self] _, reply in
            guard let self, self.allowed(reply) else { return }
            MobileAds.shared.start { _ in
                Task { @MainActor in
                    self.initialized = true
                    reply.success()
                }
            }
        }
        channel.handle("loadBanner") { [weak self] args, reply in
            guard let self, self.ready(reply), let (id, unit) = self.identifiers(args, reply)
            else { return }
            guard let slot = self.slots.firstIndex(where: { $0 == nil }),
                let presenter = NativeChannels.presenter
            else {
                reply.failure("capacity", "At most 16 live banners; a presenter is required")
                return
            }
            let entry = Entry(id, slot: slot)
            self.entries[id] = entry
            self.slots[slot] = entry
            entry.loadReply = reply
            let banner = BannerView(adSize: AdSizeLargeBanner)
            entry.banner = banner
            banner.adUnitID = unit
            banner.rootViewController = presenter
            let listener = PlacementBannerDelegate { [weak self, weak entry] error in
                guard let self, let entry, self.entries[id] === entry, let load = entry.loadReply
                else { return }
                entry.loadReply = nil
                if let error {
                    self.remove(entry)
                    load.failure("ad_failed", error.localizedDescription)
                } else {
                    load.success(.integer(Int64(slot)))
                }
            }
            entry.bannerDelegate = listener
            banner.delegate = listener
            reply.onCancel = { [weak self, weak entry] in if let entry { self?.remove(entry) } }
            banner.load(Request())
        }
        channel.handle("loadAppOpen") { [weak self] args, reply in
            guard let self, self.ready(reply), let (id, unit) = self.identifiers(args, reply)
            else { return }
            guard self.entries.values.filter({ $0.slot < 0 }).count < 4 else {
                reply.failure("capacity", "At most four cached app-open ads")
                return
            }
            let entry = Entry(id)
            self.entries[id] = entry
            entry.loadReply = reply
            reply.onCancel = { [weak self, weak entry] in if let entry { self?.remove(entry) } }
            AppOpenAd.load(with: unit, request: Request()) { [weak self, weak entry] ad, error in
                Task { @MainActor in
                    guard let self, let entry, self.entries[id] === entry else { return }
                    entry.loadReply = nil
                    if let ad {
                        entry.appOpen = ad
                        reply.success()
                    } else {
                        self.remove(entry)
                        reply.failure("ad_failed", error?.localizedDescription ?? "Ad load failed")
                    }
                }
            }
        }
        channel.handle("showAppOpen") { [weak self] args, reply in
            guard let self, self.ready(reply) else { return }
            guard let id = args.fields["id"]?.string, let entry = self.entries[id],
                let ad = entry.appOpen,
                !self.entries.values.contains(where: { $0.showing }),
                let presenter = NativeChannels.presenter
            else {
                reply.failure("unavailable", "No eligible app-open ad")
                return
            }
            entry.showing = true
            entry.showReply = reply
            reply.onCancel = { [weak entry] in entry?.showReply = nil }
            let listener = PlacementFullScreenDelegate { [weak self, weak entry] error in
                guard let self, let entry else { return }
                entry.showing = false
                if let error {
                    entry.showReply?.failure("ad_failed", error.localizedDescription)
                } else {
                    entry.showReply?.success()
                }
                self.remove(entry)
            }
            entry.fullscreenDelegate = listener
            ad.fullScreenContentDelegate = listener
            ad.present(from: presenter)
        }
        channel.handle("dispose") { [weak self] args, reply in
            guard let self, let id = args.fields["id"]?.string, let entry = self.entries[id] else {
                reply.success()
                return
            }
            if entry.showing {
                entry.disposals.append(reply)
            } else {
                self.remove(entry)
                reply.success()
            }
        }
        backgroundObserver = NotificationCenter.default.addObserver(
            forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                Array(self.entries.values).filter { !$0.showing }.forEach { self.remove($0) }
            }
        }
    }
    private func status(_ reply: PluginReply) {
        reply.success(
            .map([
                "canRequestAds": .bool(consentUpdated && ConsentInformation.shared.canRequestAds),
                "privacyOptionsRequired": .bool(
                    ConsentInformation.shared.privacyOptionsRequirementStatus == .required),
            ]))
    }
    private func allowed(_ reply: PluginReply) -> Bool {
        guard configured, consentUpdated, !consentBusy, ConsentInformation.shared.canRequestAds
        else {
            reply.failure("consent_required", "Gather consent before requesting ads")
            return false
        }
        return true
    }
    private func identifiers(_ args: PluginValue, _ reply: PluginReply) -> (String, String)? {
        guard let id = args.fields["id"]?.string, !id.isEmpty, id.count <= 64, entries[id] == nil,
            let unit = args.fields["adUnitId"]?.string, !unit.isEmpty
        else {
            reply.failure("invalid_argument", "Unique id and ad unit required")
            return nil
        }
        return (id, unit)
    }
    private func ready(_ reply: PluginReply) -> Bool {
        guard allowed(reply) else { return false }
        guard initialized else {
            reply.failure("not_initialized", "Initialize after gathering consent")
            return false
        }
        return true
    }
    private func remove(_ entry: Entry) {
        guard entries[entry.id] === entry else { return }
        entries.removeValue(forKey: entry.id)
        entry.loadReply?.failure("cancelled", "Ad load disposed")
        entry.loadReply = nil
        entry.banner?.delegate = nil
        entry.banner?.removeFromSuperview()
        entry.banner = nil
        entry.bannerDelegate = nil
        entry.appOpen = nil
        entry.fullscreenDelegate = nil
        entry.showReply = nil
        if entry.slot >= 0 { slots[entry.slot] = nil }
        entry.disposals.forEach { $0.success() }
        entry.disposals.removeAll()
    }
}
@MainActor private final class PlacementBannerDelegate: NSObject, BannerViewDelegate {
    let complete: (Error?) -> Void
    init(_ complete: @escaping (Error?) -> Void) { self.complete = complete }
    func bannerViewDidReceiveAd(_ bannerView: BannerView) { complete(nil) }
    func bannerView(_ bannerView: BannerView, didFailToReceiveAdWithError error: Error) {
        complete(error)
    }
}
@MainActor private final class PlacementFullScreenDelegate: NSObject, FullScreenContentDelegate {
    let complete: (Error?) -> Void
    init(_ complete: @escaping (Error?) -> Void) { self.complete = complete }
    func adDidDismissFullScreenContent(_ ad: FullScreenPresentingAd) { complete(nil) }
    func ad(_ ad: FullScreenPresentingAd, didFailToPresentFullScreenContentWithError error: Error) {
        complete(error)
    }
}
