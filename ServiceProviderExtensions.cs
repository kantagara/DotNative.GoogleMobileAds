using System;
using Microsoft.Extensions.DependencyInjection;

namespace DotNative.GoogleMobileAds;

public static class MobileAdsServiceProviderExtensions
{
#if NET10_0_OR_GREATER
    extension(IServiceProvider services)
    {
        /// <summary>Resolves the registered plugin using the provider's DI lifetime.</summary>
        public IAdMobAds AdMobAds => services.GetRequiredService<IAdMobAds>();

        public IPlacementMobileAds PlacementAds =>
            services.GetRequiredService<IPlacementMobileAds>();

        public IAdConsent AdConsent => services.GetRequiredService<IAdConsent>();

        public IMobileAds MobileAds => services.GetRequiredService<IMobileAds>();
    }
#else
    /// <summary>Resolves the registered plugin using the provider's DI lifetime.</summary>
    public static IAdMobAds AdMobAds(this IServiceProvider services) =>
        services.GetRequiredService<IAdMobAds>();

    public static IPlacementMobileAds PlacementAds(this IServiceProvider services) =>
        services.GetRequiredService<IPlacementMobileAds>();

    public static IAdConsent AdConsent(this IServiceProvider services) =>
        services.GetRequiredService<IAdConsent>();

    public static IMobileAds MobileAds(this IServiceProvider services) =>
        services.GetRequiredService<IMobileAds>();
#endif
}
