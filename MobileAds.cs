using DotNative;
using DotNative.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNative.GoogleMobileAds;

public interface IMobileAds
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task LoadAppOpenAsync(string adUnitId, CancellationToken cancellationToken = default);

    Task ShowAppOpenAsync(CancellationToken cancellationToken = default);

    Task ShowBannerAsync(string adUnitId, CancellationToken cancellationToken = default);

    Task HideBannerAsync(CancellationToken cancellationToken = default);
}

public static class MobileAdsServices
{
    public static IServiceCollection AddMobileAds(this IServiceCollection services)
    {
        services.TryAddSingleton<IMobileAds, RoutingMobileAds>();
        return services;
    }
}

internal sealed class RoutingMobileAds(IPlatformChannels channels, PresentationTarget target)
    : IMobileAds
{
    private bool Mobile => target.Platform is NativePlatform.IOS or NativePlatform.Android;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Mobile
            ? new ChannelMobileAds(channels).InitializeAsync(cancellationToken)
            : Task.CompletedTask;

    public Task LoadAppOpenAsync(string adUnitId, CancellationToken cancellationToken = default)
    {
        ValidateUnit(adUnitId);
        return Mobile
            ? new ChannelMobileAds(channels).LoadAppOpenAsync(adUnitId, cancellationToken)
            : Task.CompletedTask;
    }

    public Task ShowAppOpenAsync(CancellationToken cancellationToken = default) =>
        Mobile
            ? new ChannelMobileAds(channels).ShowAppOpenAsync(cancellationToken)
            : Task.CompletedTask;

    public Task ShowBannerAsync(string adUnitId, CancellationToken cancellationToken = default)
    {
        ValidateUnit(adUnitId);
        return Mobile
            ? new ChannelMobileAds(channels).ShowBannerAsync(adUnitId, cancellationToken)
            : Task.CompletedTask;
    }

    public Task HideBannerAsync(CancellationToken cancellationToken = default) =>
        Mobile
            ? new ChannelMobileAds(channels).HideBannerAsync(cancellationToken)
            : Task.CompletedTask;

    private static void ValidateUnit(string adUnitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adUnitId);
        if (adUnitId.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(adUnitId));
    }
}

internal sealed class ChannelMobileAds(IPlatformChannels channels)
{
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Call("initialize", null, cancellationToken);

    public Task LoadAppOpenAsync(string adUnitId, CancellationToken cancellationToken) =>
        Call(
            "loadAppOpen",
            new Dictionary<string, object?> { ["adUnitId"] = adUnitId },
            cancellationToken
        );

    public Task ShowAppOpenAsync(CancellationToken cancellationToken) =>
        Call("showAppOpen", null, cancellationToken);

    public Task ShowBannerAsync(string adUnitId, CancellationToken cancellationToken) =>
        Call(
            "showBanner",
            new Dictionary<string, object?> { ["adUnitId"] = adUnitId },
            cancellationToken
        );

    public Task HideBannerAsync(CancellationToken cancellationToken) =>
        Call("hideBanner", null, cancellationToken);

    private Task<object?> Call(
        string method,
        Dictionary<string, object?>? arguments,
        CancellationToken cancellationToken
    ) =>
        channels.Get("dotnative.ads").InvokeAsync(method, arguments, cancellationToken);
}
