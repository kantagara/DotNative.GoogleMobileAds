import GoogleMobileAds
import UIKit

@MainActor final class MobileAdsPlugin {
    private static var instance: MobileAdsPlugin?
    private var appOpen: AppOpenAd?
    private var banner: BannerView?
    private var fullScreen: AdsFullScreen?
    private var bannerDelegate: AdsBanner?

    static func register() {
        if instance == nil { instance = MobileAdsPlugin() }
    }

    private init() {
        let channel = NativeChannels.channel("dotnative.ads")
        channel.handle("initialize") { _, reply in
            MobileAds.shared.start { _ in Task { @MainActor in reply.success() } }
        }
        channel.handle("loadAppOpen") { [weak self] arguments, reply in
            guard let adUnitId = arguments.fields["adUnitId"]?.string, !adUnitId.isEmpty else {
                reply.failure("invalid_argument", "An ad unit id is required")
                return
            }
            AppOpenAd.load(with: adUnitId, request: Request()) { ad, error in
                Task { @MainActor in
                    if let ad {
                        self?.appOpen = ad
                        reply.success()
                    } else {
                        reply.failure("ad_failed", error?.localizedDescription ?? "The ad did not load")
                    }
                }
            }
        }
        channel.handle("showAppOpen") { [weak self] _, reply in
            guard let self, let ad = self.appOpen,
                let controller = NativeChannels.presenter?.view.window?.rootViewController
            else {
                reply.failure("unavailable", "No app-open ad is loaded")
                return
            }
            self.appOpen = nil
            let screen = AdsFullScreen(reply)
            self.fullScreen = screen
            ad.fullScreenContentDelegate = screen
            ad.present(from: controller)
        }
        channel.handle("showBanner") { [weak self] arguments, reply in
            guard let self, let adUnitId = arguments.fields["adUnitId"]?.string, !adUnitId.isEmpty,
                let window = NativeChannels.presenter?.view.window,
                let controller = window.rootViewController
            else {
                reply.failure("invalid_argument", "An ad unit id and a window are required")
                return
            }
            self.removeBanner()
            let view = BannerView(adSize: AdSizeLargeBanner)
            view.adUnitID = adUnitId
            view.rootViewController = controller
            let bannerDelegate = AdsBanner(reply) { [weak self] in self?.removeBanner() }
            self.bannerDelegate = bannerDelegate
            view.delegate = bannerDelegate
            view.translatesAutoresizingMaskIntoConstraints = false
            window.addSubview(view)
            NSLayoutConstraint.activate([
                view.centerXAnchor.constraint(equalTo: window.centerXAnchor),
                view.bottomAnchor.constraint(equalTo: window.safeAreaLayoutGuide.bottomAnchor),
            ])
            self.banner = view
            view.load(Request())
        }
        channel.handle("hideBanner") { [weak self] _, reply in
            self?.removeBanner()
            reply.success()
        }
    }

    private func removeBanner() {
        banner?.removeFromSuperview()
        banner = nil
    }
}

private final class AdsFullScreen: NSObject, FullScreenContentDelegate {
    private let reply: PluginReply
    init(_ reply: PluginReply) { self.reply = reply }

    func adDidDismissFullScreenContent(_ ad: FullScreenPresentingAd) {
        Task { @MainActor in reply.success() }
    }

    func ad(
        _ ad: FullScreenPresentingAd, didFailToPresentFullScreenContentWithError error: Error
    ) {
        let message = error.localizedDescription
        Task { @MainActor in reply.failure("ad_failed", message) }
    }
}

private final class AdsBanner: NSObject, BannerViewDelegate {
    private let reply: PluginReply
    private let failed: () -> Void
    init(_ reply: PluginReply, failed: @escaping () -> Void) {
        self.reply = reply
        self.failed = failed
    }

    func bannerViewDidReceiveAd(_ bannerView: BannerView) {
        Task { @MainActor in reply.success() }
    }

    func bannerView(_ bannerView: BannerView, didFailToReceiveAdWithError error: Error) {
        let message = error.localizedDescription
        Task { @MainActor in
            failed()
            reply.failure("ad_failed", message)
        }
    }
}
