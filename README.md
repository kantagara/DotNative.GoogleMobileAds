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
