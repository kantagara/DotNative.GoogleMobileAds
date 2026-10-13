using DotNative;
using DotNative.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNative.GoogleMobileAds;

// ─────────────────────────────────────────────────────────────
// Configuration
// ─────────────────────────────────────────────────────────────

/// <summary>The app ID must also be embedded in the native manifest/Info.plist.</summary>
public sealed record AdMobAdsOptions(string ApplicationId);

// ─────────────────────────────────────────────────────────────
// Public API
// ─────────────────────────────────────────────────────────────

public interface IMobileAds
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task LoadAppOpenAsync(string adUnitId, CancellationToken cancellationToken = default);

    Task ShowAppOpenAsync(CancellationToken cancellationToken = default);

    Task ShowBannerAsync(string adUnitId, CancellationToken cancellationToken = default);

    Task HideBannerAsync(CancellationToken cancellationToken = default);
}

public interface IAdMobAds : IMobileAds { }

// ─────────────────────────────────────────────────────────────
// Dependency Injection
// ─────────────────────────────────────────────────────────────

public static class MobileAdsServices
{
    public static IServiceCollection AddAdMobAds(
        this IServiceCollection services,
        AdMobAdsOptions options
    )
    {
        ValidateOptions(options);
        return services.AddAdMobAds(_ => options);
    }

    public static IServiceCollection AddAdMobAds(
        this IServiceCollection services,
        Func<PresentationTarget, AdMobAdsOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IAdMobAds>(sp =>
        {
            var target = sp.GetRequiredService<PresentationTarget>();

            return new MobileAdsService(
                sp.GetRequiredService<IPlatformChannels>(),
                target,
                () => configure(target)
            );
        });
        services.TryAddSingleton<IMobileAds>(sp => sp.GetRequiredService<IAdMobAds>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationInitializer, AdMobInitializer>()
        );

        return services;
    }

    internal static void ValidateOptions(AdMobAdsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (
            !System.Text.RegularExpressions.Regex.IsMatch(
                options.ApplicationId ?? "",
                @"^ca-app-pub-[0-9]{16}~[0-9]{10}$"
            )
        )
            throw new ArgumentException(
                "An AdMob application ID is required (ca-app-pub-…~…). An ad-unit ID cannot be used here.",
                nameof(options)
            );
    }

    // Compatibility for applications configured exclusively through native packaging.
    public static IServiceCollection AddMobileAds(this IServiceCollection services)
    {
        services.TryAddSingleton<IMobileAds, MobileAdsService>();
        return services;
    }
}

// ─────────────────────────────────────────────────────────────
// Implementation
// ─────────────────────────────────────────────────────────────

internal sealed class MobileAdsService(
    IPlatformChannels channels,
    PresentationTarget target,
    Func<AdMobAdsOptions>? configuration = null
) : IAdMobAds
{
    private const string ChannelName = "dotnative.ads";

    private bool IsMobile => target.Platform is NativePlatform.IOS or NativePlatform.Android;

    private Task<object?> InvokeAsync(
        string method,
        Dictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default
    )
    {
        return channels.Get(ChannelName).InvokeAsync(method, arguments, cancellationToken);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsMobile)
            return Task.CompletedTask;

        var options = configuration?.Invoke();
        if (configuration is not null)
            MobileAdsServices.ValidateOptions(options!);

        return InvokeAsync(
            "initialize",
            options?.ApplicationId is null
                ? null
                : new() { ["applicationId"] = options.ApplicationId },
            cancellationToken
        );
    }

    public Task LoadAppOpenAsync(string adUnitId, CancellationToken cancellationToken = default)
    {
        ValidateUnit(adUnitId);

        return IsMobile
            ? InvokeAsync("loadAppOpen", new() { ["adUnitId"] = adUnitId }, cancellationToken)
            : Task.CompletedTask;
    }

    public Task ShowAppOpenAsync(CancellationToken cancellationToken = default)
    {
        return IsMobile
            ? InvokeAsync("showAppOpen", cancellationToken: cancellationToken)
            : Task.CompletedTask;
    }

    public Task ShowBannerAsync(string adUnitId, CancellationToken cancellationToken = default)
    {
        ValidateUnit(adUnitId);

        return IsMobile
            ? InvokeAsync("showBanner", new() { ["adUnitId"] = adUnitId }, cancellationToken)
            : Task.CompletedTask;
    }

    public Task HideBannerAsync(CancellationToken cancellationToken = default)
    {
        return IsMobile
            ? InvokeAsync("hideBanner", cancellationToken: cancellationToken)
            : Task.CompletedTask;
    }

    private static void ValidateUnit(string adUnitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adUnitId);

        if (adUnitId.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(adUnitId));
    }
}

// ─────────────────────────────────────────────────────────────
// Application Lifecycle
// ─────────────────────────────────────────────────────────────

internal sealed class AdMobInitializer(IAdMobAds ads) : IApplicationInitializer
{
    public Task InitializeAsync(CancellationToken stopping) => ads.InitializeAsync(stopping);
}
