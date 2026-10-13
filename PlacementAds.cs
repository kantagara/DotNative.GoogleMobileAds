using DotNative.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNative.GoogleMobileAds;

public sealed record AdConsentStatus(bool CanRequestAds, bool PrivacyOptionsRequired);

public sealed record AdsRequestConfiguration(
    IReadOnlyList<string>? TestDeviceIds = null,
    bool TagForUnderAgeOfConsent = false
);

public interface IAdConsent
{
    Task<AdConsentStatus> GatherAsync(CancellationToken cancellationToken = default);
    Task<AdConsentStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AdConsentStatus> ShowPrivacyOptionsAsync(CancellationToken cancellationToken = default);
}

public interface IPlacementMobileAds
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ConfigureAsync(
        AdsRequestConfiguration configuration,
        CancellationToken cancellationToken = default
    );
    Task<IBannerAd> LoadBannerAsync(string adUnitId, CancellationToken cancellationToken = default);
    Task<IAppOpenAd> LoadAppOpenAdAsync(
        string adUnitId,
        CancellationToken cancellationToken = default
    );
}

public interface IBannerAd : IAsyncDisposable
{
    Element View { get; }
}

public interface IAppOpenAd : IAsyncDisposable
{
    DateTimeOffset LoadedAt { get; }

    /// <summary>Completes on dismissal. The SDK owns fullscreen dismissal; DisposeAsync waits for it.
    /// Cancellation cannot forcibly close a Google fullscreen ad.</summary>
    Task ShowAsync(CancellationToken cancellationToken = default);
}

public static class PlacementAdsServices
{
    public static IServiceCollection AddPlacementMobileAds(this IServiceCollection services)
    {
        services.TryAddSingleton<PlacementMobileAds>();
        services.TryAddSingleton<IPlacementMobileAds>(sp =>
            sp.GetRequiredService<PlacementMobileAds>()
        );
        services.TryAddSingleton<IAdConsent>(sp => sp.GetRequiredService<PlacementMobileAds>());
        return services;
    }
}

public sealed class PlacementMobileAds(IPlatformChannels channels, PresentationTarget target)
    : IPlacementMobileAds,
        IAdConsent
{
    private MethodChannel Channel => channels.Get("dotnative.ads.placements");

    private Task<object?> Call(
        string method,
        Dictionary<string, object?>? args,
        CancellationToken token
    )
    {
        if (target.Platform is not (NativePlatform.Android or NativePlatform.IOS))
            throw new PlatformNotSupportedException(
                "Google Mobile Ads placements require Android or iOS."
            );
        return Channel.InvokeAsync(method, args, token);
    }

    public Task ConfigureAsync(
        AdsRequestConfiguration configuration,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var ids = configuration.TestDeviceIds ?? [];
        if (
            ids.Count > 100
            || ids.Any(id =>
                string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)
            )
        )
            throw new ArgumentException("Invalid test device identifiers.");
        return Call(
            "configure",
            new()
            {
                ["testDevices"] = ids.Cast<object?>().ToArray(),
                ["underAge"] = configuration.TagForUnderAgeOfConsent,
            },
            cancellationToken
        );
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Call("initialize", null, cancellationToken);

    public Task<AdConsentStatus> GatherAsync(CancellationToken cancellationToken = default) =>
        Status("gatherConsent", cancellationToken);

    public Task<AdConsentStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Status("consentStatus", cancellationToken);

    public Task<AdConsentStatus> ShowPrivacyOptionsAsync(
        CancellationToken cancellationToken = default
    ) => Status("privacyOptions", cancellationToken);

    private async Task<AdConsentStatus> Status(string method, CancellationToken token)
    {
        var value = await Call(method, null, token);
        if (
            value is not IReadOnlyDictionary<string, object?> fields
            || fields.GetValueOrDefault("canRequestAds") is not bool can
            || fields.GetValueOrDefault("privacyOptionsRequired") is not bool privacy
        )
            throw new InvalidDataException("Invalid consent response.");
        return new(can, privacy);
    }

    public async Task<IBannerAd> LoadBannerAsync(
        string adUnitId,
        CancellationToken cancellationToken = default
    )
    {
        ValidateUnit(adUnitId);
        var id = Guid.NewGuid().ToString("N");
        var value = await Call(
            "loadBanner",
            new() { ["id"] = id, ["adUnitId"] = adUnitId },
            cancellationToken
        );
        if (value is not long and not int)
        {
            await Call("dispose", new() { ["id"] = id }, default);
            throw new InvalidDataException("Invalid banner slot.");
        }
        var rawSlot = Convert.ToInt64(value);
        if (rawSlot is < 0 or >= 16)
        {
            await Call("dispose", new() { ["id"] = id }, default);
            throw new InvalidDataException("Invalid banner slot.");
        }
        var banner = new Banner(Channel, id, (int)rawSlot);
        if (cancellationToken.IsCancellationRequested)
        {
            await banner.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return banner;
    }

    public async Task<IAppOpenAd> LoadAppOpenAdAsync(
        string adUnitId,
        CancellationToken cancellationToken = default
    )
    {
        ValidateUnit(adUnitId);
        var id = Guid.NewGuid().ToString("N");
        await Call(
            "loadAppOpen",
            new() { ["id"] = id, ["adUnitId"] = adUnitId },
            cancellationToken
        );
        var ad = new AppOpen(Channel, id);
        if (cancellationToken.IsCancellationRequested)
        {
            await ad.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return ad;
    }

    public static void ValidateUnit(string id)
    {
        if (
            string.IsNullOrWhiteSpace(id)
            || !System.Text.RegularExpressions.Regex.IsMatch(
                id,
                @"^ca-app-pub-[0-9]{16}/[0-9]{10}$"
            )
        )
            throw new ArgumentException("A valid AdMob ad unit ID is required.");
    }

    private abstract class Lease(MethodChannel channel, string id) : IAsyncDisposable
    {
        protected bool Disposed { get; private set; }
        private Task? disposal;

        protected Task<object?> Call(string method, CancellationToken token) =>
            channel.InvokeAsync(method, new Dictionary<string, object?> { ["id"] = id }, token);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (disposal is null || disposal.IsFaulted || disposal.IsCanceled)
                disposal = Call("dispose", default);
            return new(disposal);
        }
    }

    private sealed class Banner(MethodChannel channel, string id, int slot)
        : Lease(channel, id),
            IBannerAd
    {
        public Element View
        {
            get
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                return new PlatformView("dotnative.ads.banner." + slot).Width(320).Height(100);
            }
        }
    }

    private sealed class AppOpen(MethodChannel channel, string id) : Lease(channel, id), IAppOpenAd
    {
        private int shown;
        public DateTimeOffset LoadedAt { get; } = DateTimeOffset.UtcNow;

        public async Task ShowAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow - LoadedAt >= TimeSpan.FromHours(4))
            {
                await DisposeAsync();
                throw new InvalidOperationException("App-open ad expired.");
            }
            if (Interlocked.Exchange(ref shown, 1) != 0)
                throw new InvalidOperationException("An app-open ad can only be shown once.");
            try
            {
                await Call("showAppOpen", cancellationToken);
            }
            finally
            {
                await DisposeAsync();
            }
        }
    }
}
