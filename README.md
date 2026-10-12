# DotNative.GoogleMobileAds

Shows AdMob ads through the Google Mobile Ads SDK.

```csharp
builder.Services.AddMobileAds();
await services.MobileAds.InitializeAsync();
await services.MobileAds.LoadAppOpenAsync(adUnitId);
await services.MobileAds.ShowAppOpenAsync();
await services.MobileAds.ShowBannerAsync(adUnitId);
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

`services.MobileAds` is the C# 14 property on `net10.0`.
`services.MobileAds()` is the method on `net9.0`.
