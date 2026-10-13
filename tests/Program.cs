using System.Buffers.Binary;
using DotNative;
using DotNative.GoogleMobileAds;
using DotNative.Plugins;

var transport = new Transport();
using var channels = new PlatformChannels(new Dispatcher(), transport);
transport.Channels = channels;
var ads = new PlacementMobileAds(channels, new(NativePlatform.Android));
await ads.ConfigureAsync(new(["TEST-DEVICE"]));
Check(transport.Calls.Single().Method == "configure", "configuration absent");
var status = await ads.GatherAsync();
Check(!status.CanRequestAds && status.PrivacyOptionsRequired, "consent contract");
transport.CanRequest = true;
Check((await ads.GetStatusAsync()).CanRequestAds, "consent state ignored");
await ads.ShowPrivacyOptionsAsync();
await ads.InitializeAsync();
Check(transport.Calls.Last().Method == "initialize", "initialization absent");
const string unit = "ca-app-pub-3940256099942544/6300978111";
await Throws<ArgumentException>(() => ads.LoadBannerAsync("bad"));
await Throws<ArgumentException>(() => ads.ConfigureAsync(new([""])));
var banner = await ads.LoadBannerAsync(unit);
Check(banner.View is PlatformView, "not a real platform view");
await banner.DisposeAsync();
await banner.DisposeAsync();
Check(transport.Calls.Count(c => c.Method == "dispose") == 1, "duplicate disposal");
await Throws<ObjectDisposedException>(() =>
{
    _ = banner.View;
    return Task.CompletedTask;
});
transport.Slot = long.MaxValue;
await Throws<InvalidDataException>(() => ads.LoadBannerAsync(unit));
Check(transport.Calls.Count(c => c.Method == "dispose") == 2, "invalid slot leaked");
transport.Slot = 0;
transport.Hold = "loadBanner";
using var cts = new CancellationTokenSource();
var pending = ads.LoadBannerAsync(unit, cts.Token);
cts.Cancel();
await Throws<OperationCanceledException>(() => pending);
Check(transport.Cancels == 1, "load cancellation not sent to native");
transport.Hold = null;
var appOpen = await ads.LoadAppOpenAdAsync("ca-app-pub-3940256099942544/9257395921");
Check(appOpen.LoadedAt != default, "load time absent");
await appOpen.ShowAsync();
await appOpen.DisposeAsync();
Check(transport.Calls.Count(c => c.Method == "showAppOpen") == 1, "fullscreen show mismatch");
await Throws<ObjectDisposedException>(() => appOpen.ShowAsync());
transport.FailDispose = true;
var retry = await ads.LoadBannerAsync(unit);
await Throws<PluginException>(() => retry.DisposeAsync().AsTask());
transport.FailDispose = false;
await retry.DisposeAsync();
await Throws<PlatformNotSupportedException>(() =>
    new PlacementMobileAds(channels, new(NativePlatform.Linux)).GatherAsync()
);
Console.WriteLine(
    "PASS Ads: configure, consent/privacy contract, ad-unit validation, native view, idempotent disposal, malformed-result cleanup, cancellation, app-open single use, disposal retry, unsupported platform. No live requests."
);
static void Check(bool ok, string message)
{
    if (!ok)
        throw new Exception(message);
}
static async Task Throws<T>(Func<Task> run)
    where T : Exception
{
    try
    {
        await run();
    }
    catch (T)
    {
        return;
    }
    throw new Exception("Expected " + typeof(T).Name);
}

sealed class Dispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();
}

sealed class Transport : IPluginTransport
{
    public PlatformChannels Channels = null!;
    public long Slot;
    public bool CanRequest;
    public string? Hold;
    public int Cancels;
    public bool FailDispose;
    public List<(string Method, Dictionary<string, object?>? Args)> Calls = new();

    public void SendPlugin(ReadOnlySpan<byte> bytes)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) == 2)
        {
            Cancels++;
            return;
        }
        var call = (Dictionary<string, object?>)PluginCodec.Decode(bytes[16..])!;
        var method = (string)call["method"]!;
        Calls.Add((method, call["arguments"] as Dictionary<string, object?>));
        if (method == Hold)
            return;
        object? result = method switch
        {
            "loadBanner" => Slot,
            "consentStatus" or "privacyOptions" or "gatherConsent" => new Dictionary<
                string,
                object?
            >
            {
                ["canRequestAds"] = CanRequest,
                ["privacyOptionsRequired"] = true,
            },
            _ => null,
        };
        var failure = method == "dispose" && FailDispose;
        if (failure)
            result = new Dictionary<string, object?>
            {
                ["code"] = "error",
                ["message"] = "dispose failed",
            };
        var payload = PluginCodec.Encode(result);
        var reply = new byte[12 + payload.Length];
        bytes[..8].CopyTo(reply);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), failure ? 1u : 0u);
        payload.CopyTo(reply, 12);
        Channels.AcceptReply(reply);
    }
}
