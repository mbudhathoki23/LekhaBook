using System.Net;
using LekhaBook.Offline;
using Microsoft.Extensions.Logging.Abstractions;

namespace LekhaBook.Sync.Tests;

/// <summary>"Online" must mean the server answers, not that the phone has some network.</summary>
public sealed class ServerConnectivityTests
{
    private readonly Server _server = new();
    private readonly Network _network = new();
    private readonly List<bool> _changes = [];

    private ServerConnectivity New()
    {
        var connectivity = new ServerConnectivity(_network, _server, TimeProvider.System,
            NullLogger<ServerConnectivity>.Instance, watch: false);
        connectivity.OnlineChanged += (_, online) => _changes.Add(online);
        return connectivity;
    }

    [Fact]
    public async Task A_network_without_a_server_is_offline_until_the_server_answers()
    {
        using var connectivity = New();

        Assert.False(await connectivity.CheckAsync(force: true));
        _server.Up = true;
        Assert.True(await connectivity.CheckAsync(force: true));
        _server.Up = false;
        Assert.False(await connectivity.CheckAsync(force: true));

        Assert.Equal([true, false], _changes);
    }

    [Fact]
    public async Task No_network_is_offline_without_asking_the_server()
    {
        _server.Up = true;
        _network.HasNetwork = false;
        using var connectivity = New();

        Assert.False(await connectivity.CheckAsync(force: true));
        Assert.Equal(0, _server.Asked);
    }

    [Fact]
    public async Task Calls_fail_at_once_while_the_server_is_away_and_go_through_when_it_is_back()
    {
        using var connectivity = New();
        var calls = new Counting();
        using var client = new HttpClient(new OfflineGate(connectivity) { InnerHandler = calls })
        {
            BaseAddress = new Uri("http://books.example.test/"),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/me"));
        Assert.Equal(0, calls.Count);

        _server.Up = true;
        await connectivity.CheckAsync(force: true);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/me")).StatusCode);
        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public async Task A_call_that_cannot_connect_turns_the_app_offline()
    {
        _server.Up = true;
        using var connectivity = New();
        await connectivity.CheckAsync(force: true);
        using var client = new HttpClient(new OfflineGate(connectivity) { InnerHandler = new Broken() })
        {
            BaseAddress = new Uri("http://books.example.test/"),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/me"));

        Assert.False(connectivity.IsOnline);
    }

    private sealed class Network : INetworkMonitor
    {
        public bool HasNetwork { get; set; } = true;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class Server : HttpMessageHandler, IHttpClientFactory
    {
        public bool Up { get; set; }

        public int Asked { get; private set; }

        public HttpClient CreateClient(string name) =>
            new(this, disposeHandler: false) { BaseAddress = new Uri("http://books.example.test/") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked++;
            return Up ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)) : throw new HttpRequestException("down");
        }
    }

    private sealed class Counting : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class Broken : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("cut");
    }
}
