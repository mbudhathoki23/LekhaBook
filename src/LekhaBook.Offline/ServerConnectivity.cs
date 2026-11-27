using LekhaBook.UI.Services;
using Microsoft.Extensions.Logging;

namespace LekhaBook.Offline;

/// <summary>Whether the device has any network. The host supplies it.</summary>
public interface INetworkMonitor
{
    bool HasNetwork { get; }

    event EventHandler? Changed;
}

/// <summary>
/// "Online" means the LekhaBook server answers, not merely that the device has internet. It asks the server
/// at start, when the network changes, often while offline and now and then while online.
/// </summary>
public sealed class ServerConnectivity : IConnectivityService, IDisposable
{
    public const string ProbeClient = "lekhabook-probe";

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);

    private readonly INetworkMonitor _network;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _clock;
    private readonly ILogger<ServerConnectivity> _logger;
    private readonly SemaphoreSlim _probing = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private bool _reachable;
    private bool _reported;
    private DateTimeOffset? _checkedAt;

    public ServerConnectivity(INetworkMonitor network, IHttpClientFactory http, TimeProvider clock,
        ILogger<ServerConnectivity> logger, bool watch = true)
    {
        _network = network;
        _http = http;
        _clock = clock;
        _logger = logger;
        _network.Changed += OnNetworkChanged;
        if (watch)
        {
            _ = WatchAsync(_stop.Token);
        }
    }

    public bool IsOnline => _network.HasNetwork && _reachable;

    public event EventHandler<bool>? OnlineChanged;

    /// <summary>Asks the server now, unless it was asked a moment ago.</summary>
    public async Task<bool> CheckAsync(bool force = false)
    {
        if (!force && _checkedAt is { } at && _clock.GetUtcNow() - at < Fresh)
        {
            return IsOnline;
        }

        await _probing.WaitAsync();
        try
        {
            if (!force && _checkedAt is { } again && _clock.GetUtcNow() - again < Fresh)
            {
                return IsOnline; // another caller just asked
            }

            var reachable = false;
            if (_network.HasNetwork)
            {
                try
                {
                    using var client = _http.CreateClient(ProbeClient);
                    using var response = await client.GetAsync("health", _stop.Token);
                    reachable = response.IsSuccessStatusCode;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    _logger.LogDebug(ex, "The server did not answer");
                }
            }

            _checkedAt = _clock.GetUtcNow();
            Set(reachable);
        }
        finally
        {
            _probing.Release();
        }

        return IsOnline;
    }

    /// <summary>A real request just failed to reach the server.</summary>
    public void MarkUnreachable()
    {
        _checkedAt = _clock.GetUtcNow();
        Set(false);
    }

    private void Set(bool reachable)
    {
        _reachable = reachable;
        var online = IsOnline;
        if (online != _reported)
        {
            _reported = online;
            OnlineChanged?.Invoke(this, online);
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        _checkedAt = null;
        if (!_network.HasNetwork)
        {
            Set(false);
        }
        else
        {
            _ = CheckAsync(force: true);
        }
    }

    private async Task WatchAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await CheckAsync(force: true);
                await Task.Delay(IsOnline ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(10), stop);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
    }

    public void Dispose()
    {
        _network.Changed -= OnNetworkChanged;
        _stop.Cancel();
    }
}

/// <summary>
/// Sits on the API client: while the server is not answering, calls fail at once instead of leaving a screen
/// waiting for a timeout, and a call that cannot connect turns the app to offline.
/// </summary>
public sealed class OfflineGate(ServerConnectivity connectivity) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!connectivity.IsOnline && !await connectivity.CheckAsync())
        {
            throw new HttpRequestException("The server is not reachable.");
        }

        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            connectivity.MarkUnreachable();
            throw;
        }
    }
}
