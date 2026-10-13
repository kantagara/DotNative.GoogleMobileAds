# DotNative.GoogleMobileAds

Shows AdMob ads through the Google Mobile Ads SDK.

```csharp
builder.Services.AddAdMobAds(new AdMobAdsOptions(
    ApplicationId: "ca-app-pub-0000000000000000~1111111111"));
builder.Build().Run<App>(); // AdMob initializes automatically.
```

The banner is attached to the bottom of the native window. App-open ads use
the SDK full-screen presentation.

Android resolves `com.google.android.gms:play-services-ads:25.1.0`. The app
manifest must contain `com.google.android.gms.ads.APPLICATION_ID`.

iOS publish downloads Google Mobile Ads 13.2.0 and the User Messaging Platform,
then links those frameworks. The cache lives in `.ads-ios`. The iOS app also
needs `GADApplicationIdentifier` in its Info.plist.

Windows, Linux and macOS skip the SDK.

## Service access

`services.AdMobAds` is the C# 14 property on `net10.0`.
`services.AdMobAds()` is the method on `net9.0`.

`AddAdMobAds` accepts either `AdMobAdsOptions` or a factory taking the presentation
target for different Android/iOS app IDs. `IAdMobAds` is the explicitly named
service; `IMobileAds` aliases the same instance for compatibility. Ad-unit IDs
remain per-placement arguments to load/show methods.

The application ID is an explicit declaration of the packaged AdMob app identity.
It must also be present in AndroidManifest.xml / Info.plist, as required by the
native SDK before managed startup. The native plugin checks equality during
initialization and returns `configuration_mismatch` if they differ; runtime
registration does not rewrite package metadata. The plugin reads no environment
variables. `AddMobileAds()` remains only for older callers using native configuration.

`AddAdMobAds` also registers its application initializer. Build resolves it, and
Run starts it once after mounting the UI. The framework owns cancellation and
async shutdown; startup errors are logged and retained on
`DotNativeApplication.Initialization`. No manual GetRequiredService/InitializeAsync
block is required. Legacy `AddMobileAds()` retains manual initialization semantics.

Before loading an ad immediately after startup, await `application.Initialization`.
Then use `IAdMobAds.LoadAppOpenAsync(adUnitId)` / `ShowAppOpenAsync()` or
`ShowBannerAsync(adUnitId)` from the feature that owns the placement.

## Owned placements and UMP (preview.2)

Use the new placement API for feed/details banners and app-open ads. It replaces the legacy window-overlay usage above. Register **only** `AddPlacementMobileAds()` for this flow: do not also register the legacy automatic `AddAdMobAds` initializer, because initialization must follow consent and request configuration.

```csharp
services.AddPlacementMobileAds();
var ads = services.GetRequiredService<IPlacementMobileAds>();
var consent = services.GetRequiredService<IAdConsent>();
await ads.ConfigureAsync(new AdsRequestConfiguration(testDeviceIds), token);
var state = await consent.GatherAsync(token); // update UMP each app launch, conditional form
if (state.CanRequestAds)
{
    await ads.InitializeAsync(token); // Google SDK, after consent
    await using var banner = await ads.LoadBannerAsync(demoBannerUnitId, token);
    // Mount banner.View (real PlatformView, 320 x 100 DIP) in your placement.
    // Keep this async scope alive until it is unmounted.
}
// Expose a privacy entry when state.PrivacyOptionsRequired:
await consent.ShowPrivacyOptionsAsync(token);
```

`GetStatusAsync` rechecks UMP status. Native load/show methods reject requests without configuration, updated UMP consent, `CanRequestAds` and explicit initialization. Consent errors and unavailability propagate; no implicit approval is returned. `services.PlacementAds` / `services.AdConsent` are net10 accessors, with methods on net9.

`LoadAppOpenAdAsync` returns `IAppOpenAd` with LoadedAt, a single-use ShowAsync and DisposeAsync. Show completes on SDK dismissal/failure, expires after four hours and disposes the handle. Banner disposal destroys/detaches its native view; cancelled loads discard late callbacks. Multiple banners own independent slots (maximum 16); app-open cache has a four-handle bound. Load errors are retryable by making a new request. The application owns timeouts, visible loading/error/retry UI and placement lifetime.

Suspend/unmount placements before logout, route exit and background. Native background cleanup removes nonshowing ads; reload on resume. Google owns fullscreen and consent form UI: cancellation suppresses late completion but **cannot forcibly dismiss an SDK ad/form**. App-open DisposeAsync waits until dismissal or Android Activity destruction. Do not block your UI thread waiting for cleanup; do not show app-open over sensitive account transitions or another native modal.

Android also resolves UMP 3.2.0. iOS links GoogleMobileAds 13.2.0 and UserMessagingPlatform 3.1.0. Package the AdMob application ID in Android `com.google.android.gms.ads.APPLICATION_ID` / iOS `GADApplicationIdentifier`; `ConfigureAsync` supplies test-device IDs and under-age tagging, not app identity. Configure your published UMP message in AdMob and expose the required privacy options entry. iOS ATT remains a separate application policy; the plugin does not request ATT or assume permission. Keep current Google SKAdNetwork entries in the host Info.plist per the [SDK setup guide](https://developers.google.com/admob/ios/quick-start).

Only Google's [demo units](https://developers.google.com/admob/android/test-ads) or configured test devices may be used for verification. The included tests never request ads or generate clicks/impressions.

### Host compatibility and validation

This preview requires the DotNative host's `PlatformView`, native view registry and `IApplicationInitializer` APIs. They exist in the FaliNamIgrac source host used for validation. The older standalone DotNative 0.1.0-preview.10 NuGet package lacks these APIs: a package-only build with that host is unsupported. Consume this release through DotNative's tagged source dependency mechanism with a compatible host until the host NuGet is updated. The GitHub nupkg asset is not a NuGet.org publication.

```sh
dotnet run --project tests -f net10.0 --artifacts-path /tmp/ads-tests
DOTNET_ROLL_FORWARD=Major dotnet run --project tests -f net9.0 --artifacts-path /tmp/ads-tests
```

Tests cover configuration/consent, validation, native view identity, cancellation packets, late-result cleanup, app-open single use and disposal retry. Android sources compile against the actual Google SDK; Swift sources typecheck against the actual iOS frameworks. Native test harness details are in `tests/native/README.md`. Live UMP messages/ad fill and physical-device presentation need a configured test application; compilation and boundary tests are not claims of live-ad validation.
