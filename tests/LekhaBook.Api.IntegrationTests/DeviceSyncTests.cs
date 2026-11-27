using System.Net;
using System.Net.Http.Json;
using LekhaBook.Client;
using LekhaBook.Contracts;
using LekhaBook.Data.Sqlite;
using LekhaBook.Domain;
using LekhaBook.Offline;
using LekhaBook.Testing;
using LekhaBook.UI.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>
/// A phone (the real device store, data service and sync engine) against the real API: what is entered with no
/// connection reaches the server once and unchanged, and nothing the user did is silently lost.
/// </summary>
public sealed class DeviceSyncTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6];

    private static DateOnly Today => CompanyClock.Today(CompanyClock.DefaultTimeZoneId, TimeProvider.System);

    [PostgresFact]
    public async Task Offline_entries_show_at_once_and_upload_once_with_matching_balances()
    {
        await using var phone = await Phone.ForNewCompanyAsync(api);
        var (party, cash) = (phone.Party, phone.Cash);

        phone.GoOffline();
        var received = await phone.AddAsync("PaymentIn", 500m, party, cash);
        var mistake = await phone.AddAsync("PaymentIn", 75m, party, cash);
        Assert.True((await phone.Data.UpdateTransactionAsync(phone.CompanyId, received.Id, Change(650m, party, cash))).Ok);
        Assert.True((await phone.Data.DeleteTransactionAsync(phone.CompanyId, mistake.Id)).Ok);
        Assert.Equal(650m, await phone.CashBalanceAsync());
        Assert.Empty(await phone.ServerEntriesAsync());

        await phone.SyncAsync();
        await phone.SyncAsync(); // nothing is sent twice

        Assert.Empty(await phone.Engine.GetIssuesAsync());
        var entry = Assert.Single(await phone.ServerEntriesAsync());
        Assert.Equal((received.Id, 650m), (entry.Id, entry.Amount));
        Assert.Equal(650m, (await phone.ServerGetAsync<MoneyAccountDto>($"accounts/{cash}")).Balance);
        Assert.Equal(650m, await phone.CashBalanceAsync());
    }

    [PostgresFact]
    public async Task An_edit_made_while_the_entry_is_uploading_is_not_lost()
    {
        await using var phone = await Phone.ForNewCompanyAsync(api);
        phone.GoOffline();
        var entry = await phone.AddAsync("PaymentIn", 100m, phone.Party, phone.Cash);

        await phone.WhilePushingAsync(async () =>
            Assert.True((await phone.Data.UpdateTransactionAsync(phone.CompanyId, entry.Id, Change(222m, phone.Party, phone.Cash))).Ok));
        await phone.SyncAsync();

        Assert.Equal(222m, Assert.Single(await phone.ServerEntriesAsync()).Amount);
        Assert.Equal(222m, await phone.CashBalanceAsync());
        Assert.Empty(await phone.Engine.GetIssuesAsync());
    }

    [PostgresFact]
    public async Task A_void_made_while_the_entry_is_uploading_voids_it_on_the_server()
    {
        await using var phone = await Phone.ForNewCompanyAsync(api);
        phone.GoOffline();
        var entry = await phone.AddAsync("PaymentIn", 100m, phone.Party, phone.Cash);

        await phone.WhilePushingAsync(async () =>
            Assert.True((await phone.Data.DeleteTransactionAsync(phone.CompanyId, entry.Id)).Ok));
        await phone.SyncAsync();

        Assert.Empty(await phone.ServerEntriesAsync());
        Assert.Equal(0m, await phone.CashBalanceAsync());
        Assert.Empty(await phone.Engine.GetIssuesAsync());
    }

    [PostgresFact]
    public async Task A_conflicting_change_stays_visible_until_discarded_then_the_server_copy_returns()
    {
        await using var phone = await Phone.ForNewCompanyAsync(api);
        phone.GoOffline();
        var entry = await phone.AddAsync("PaymentIn", 100m, phone.Party, phone.Cash);
        await phone.SyncAsync();

        phone.GoOffline();
        Assert.True((await phone.Data.UpdateTransactionAsync(phone.CompanyId, entry.Id, Change(200m, phone.Party, phone.Cash))).Ok);
        (await phone.Server.PutAsJsonAsync($"/api/companies/{phone.CompanyId}/transactions/{entry.Id}",
            Change(300m, phone.Party, phone.Cash))).EnsureSuccessStatusCode();
        await phone.SyncAsync();

        var issue = Assert.Single(await phone.Engine.GetIssuesAsync());
        Assert.StartsWith("Conflict", issue.Error);
        Assert.Equal(200m, await phone.CashBalanceAsync()); // what the user entered, not silently replaced
        Assert.Equal(300m, Assert.Single(await phone.ServerEntriesAsync()).Amount);

        await phone.Engine.DiscardIssueAsync(issue.Id);

        Assert.Empty(await phone.Engine.GetIssuesAsync());
        Assert.Equal(300m, await phone.CashBalanceAsync());
    }

    [PostgresFact]
    public async Task Discarding_a_refused_new_entry_removes_it_from_the_phone()
    {
        await using var phone = await Phone.ForNewCompanyAsync(api);
        phone.GoOffline();
        await phone.AddAsync("PaymentIn", 90m, phone.Party, phone.Cash);
        // The party is deleted on the server before the phone gets back.
        (await phone.Server.DeleteAsync($"/api/companies/{phone.CompanyId}/parties/{phone.Party}")).EnsureSuccessStatusCode();
        await phone.SyncAsync();
        var issue = Assert.Single(await phone.Engine.GetIssuesAsync());

        await phone.Engine.DiscardIssueAsync(issue.Id);

        Assert.Empty(await phone.Engine.GetIssuesAsync());
        Assert.Empty((await phone.Data.GetTransactionsAsync(phone.CompanyId)).Value!);
        Assert.Equal(0m, await phone.CashBalanceAsync());
    }

    [PostgresFact]
    public async Task Offline_the_phone_applies_the_users_permissions()
    {
        await using var owner = await Phone.ForNewCompanyAsync(api);
        owner.GoOffline();
        var theirs = await owner.AddAsync("PaymentIn", 40m, owner.Party, owner.Cash);
        await owner.SyncAsync();

        var staff = await api.NewUserAsync("staff" + Guid.NewGuid().ToString("N")[..6]);
        await ApiFixture.AddMemberAsync(owner.User, owner.CompanyId, staff, "EntryPerson",
            [Permission.CustomersView, Permission.PaymentInView, Permission.PaymentInCreate]);
        await using var phone = await Phone.SignedInAsync(api, staff, owner.CompanyId);
        phone.GoOffline();

        var edit = await phone.Data.UpdateTransactionAsync(owner.CompanyId, theirs.Id, Change(1m, owner.Party, owner.Cash));
        Assert.Equal(403, edit.StatusCode);
        Assert.Equal(403, (await phone.Data.DeleteTransactionAsync(owner.CompanyId, theirs.Id)).StatusCode);
        Assert.Equal(403, (await phone.Data.GetMoneyAccountsAsync(owner.CompanyId)).StatusCode);
        Assert.Equal(403, (await phone.Data.DeletePartyAsync(owner.CompanyId, owner.Party)).StatusCode);
        using (var photo = new MemoryStream(Jpeg))
        {
            var attach = await phone.Files.UploadAttachmentAsync(owner.CompanyId, photo, "bill.jpg", "image/jpeg", theirs.Id);
            Assert.Equal(403, attach.StatusCode);
        }

        await phone.Engine.SyncNowAsync();
        Assert.Equal(0, phone.Engine.PendingCount);
    }

    [PostgresFact]
    public async Task A_photo_the_server_already_has_is_not_uploaded_again()
    {
        await using var phone = await Phone.ForNewCompanyAsync(api);
        phone.GoOffline();
        var entry = await phone.AddAsync("PaymentIn", 60m, phone.Party, phone.Cash);
        using (var photo = new MemoryStream(Jpeg))
        {
            Assert.True((await phone.Files.UploadAttachmentAsync(phone.CompanyId, photo, "bill.jpg", "image/jpeg", entry.Id)).Ok);
        }

        // The entry goes up, then the photo reaches the server but the phone never records the answer.
        phone.GoOnline();
        phone.Cable.BlockUploads = true;
        await phone.Engine.SyncNowAsync();
        phone.Cable.BlockUploads = false;
        using (var content = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(Jpeg);
            file.Headers.ContentType = new("image/jpeg");
            content.Add(file, "file", "bill.jpg");
            content.Add(new StringContent(entry.Id.ToString()), "transactionId");
            (await phone.Server.PostAsync($"/api/companies/{phone.CompanyId}/attachments", content)).EnsureSuccessStatusCode();
        }

        await phone.Engine.SyncNowAsync();

        Assert.Single(await phone.ServerGetAsync<List<AttachmentDto>>($"attachments?transactionId={entry.Id}"));
        Assert.Single((await phone.Files.GetAttachmentsAsync(phone.CompanyId, entry.Id)).Value!);
    }

    [PostgresFact]
    public async Task Retrying_an_online_upgrade_brings_over_what_was_added_since()
    {
        var owner = await api.NewUserAsync("upgrader" + Guid.NewGuid().ToString("N")[..6]);
        var companyId = Guid.CreateVersion7();
        var (cash, party, adjust) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        MigrateAccountDto[] accounts =
        [
            new(cash, "Cash", "Cash", true, true, 0m, "Debit", Today, null, null, null, null, null, null, null, null, false),
            new(party, "Ram", "Party", false, true, 0m, "Debit", Today, "Customer", null, null, null, null, null, null, null, false),
            new(adjust, "Adjustment", "System", true, true, 0m, "Debit", Today, null, null, null, null, null, null, null, null, false),
        ];
        MigrateTransactionDto Entry(Guid id, decimal amount) =>
            new(id, "PaymentIn", id.ToString(), false, true, Today, amount, party, cash, null, null, null, "Posted", false);
        async Task<MigrateOfflineCompanyResponse> MigrateAsync(params MigrateTransactionDto[] entries)
        {
            var response = await owner.Http.PostAsJsonAsync("/api/companies/migrate", new MigrateOfflineCompanyRequest(
                companyId, "Upgrade Pasal", null, null, null, "BS", 4, "NPR", "Asia/Kathmandu", [], accounts, entries));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<MigrateOfflineCompanyResponse>())!;
        }

        await MigrateAsync(Entry(first, 10m));
        var again = await MigrateAsync(Entry(first, 15m), Entry(second, 20m));
        async Task<decimal> AmountAsync(Guid id) =>
            (await owner.Http.GetFromJsonAsync<List<TransactionDto>>($"/api/companies/{companyId}/transactions"))!
                .Single(t => t.Id == id).Amount;

        Assert.True(again.AlreadyExisted);
        Assert.Equal(2, again.TransactionsMigrated);
        Assert.Equal(15m, await AmountAsync(first));
        Assert.Equal(20m, await AmountAsync(second));

        // Once somebody has changed an entry online, an older device copy no longer replaces it.
        (await owner.Http.PutAsJsonAsync($"/api/companies/{companyId}/transactions/{first}",
            Change(99m, party, cash))).EnsureSuccessStatusCode();
        var third = await MigrateAsync(Entry(first, 15m), Entry(second, 20m));

        Assert.Equal(2, third.TransactionsMigrated);
        Assert.Equal(99m, await AmountAsync(first));
    }

    [PostgresFact]
    public async Task An_upgrade_whose_photos_fail_keeps_the_offline_books_and_can_be_retried()
    {
        var user = await api.NewUserAsync("upgrader" + Guid.NewGuid().ToString("N")[..6]);
        var folder = Directory.CreateTempSubdirectory("lekhabook-upgrade-").FullName;
        var files = Path.Combine(folder, "files");
        var databases = new UserDatabaseFactory(folder);
        var store = new LocalBooksStore(databases, files);
        var cable = new Cable();
        var tokens = new Tokens(null);
        var client = new LekhaBookApi(api.Factory.CreateDefaultClient(cable), tokens, TimeProvider.System);
        var session = new SessionService(client, tokens, new Settings(), [new Opener(databases)],
            localProvider: new DeviceLocalSessionProvider(store));
        var connectivity = new Switch();
        var remote = new ApiAttachmentService(client);
        var companyApi = new CompanyApi(client);
        using var engine = new DeviceSyncEngine(databases, store, session, connectivity, client, companyApi,
            remote, NullLogger<DeviceSyncEngine>.Instance);

        // Books kept with no account: one entry with its photo.
        await session.InitializeAsync();
        Assert.Null((await session.StartLocalModeAsync("Offline Pasal")).Error);
        var companyId = session.CurrentCompany!.CompanyId;
        var data = new SqliteDataService(store, session, engine);
        var party = await data.AddPartyAsync(companyId, new SavePartyRequest("Ram Store", "Customer", Id: Guid.CreateVersion7()));
        Assert.True(party.Ok, party.Error);
        var cash = (await data.GetMoneyAccountsAsync(companyId)).Value!.First(a => a.Type == "Cash").Id;
        var entry = await data.AddTransactionAsync(companyId, new CreateTransactionRequest(
            "PaymentIn", Today, 60m, party.Value!.Id, cash, Id: Guid.CreateVersion7(), CheckForDuplicates: false));
        Assert.True(entry.Ok, entry.Error);
        var attachments = new SqliteAttachmentService(store, remote, session, connectivity, engine);
        using (var photo = new MemoryStream(Jpeg))
        {
            var added = await attachments.UploadAttachmentAsync(companyId, photo, "bill.jpg", "image/jpeg", entry.Value!.Id);
            Assert.True(added.Ok, added.Error);
        }

        var upgrade = new OfflineUpgradeService(databases, files, session, companyApi, remote, engine);

        cable.BlockUploads = true;
        var failed = await upgrade.UpgradeWithExistingAccountAsync(user.Email, "Passw0rd-123");

        Assert.False(failed.Ok);
        Assert.True(databases.DatabaseExists(UserDatabaseFactory.LocalOfflineUserId));
        // Back on the books kept on this device, so the user can see them and try again.
        Assert.True(session.IsLocalMode);
        Assert.Null(await tokens.LoadAsync());
        Assert.Equal(entry.Value!.Id, Assert.Single((await data.GetTransactionsAsync(companyId)).Value!).Id);

        cable.BlockUploads = false;
        var retried = await upgrade.UpgradeWithExistingAccountAsync(user.Email, "Passw0rd-123");

        Assert.True(retried.Ok, retried.Error);
        var onServer = await user.Http.GetFromJsonAsync<List<AttachmentDto>>(
            $"/api/companies/{companyId}/attachments?transactionId={entry.Value!.Id}");
        Assert.Single(onServer!);

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // Left for the system to clean.
        }
    }

    private static UpdateTransactionRequest Change(decimal amount, Guid from, Guid to) =>
        new(Today, amount, from, to, null, null, null, CheckForDuplicates: false);

    /// <summary>One installed app, over a cable that can be pulled.</summary>
    private sealed class Phone : IAsyncDisposable
    {
        private readonly Switch _connectivity = new();
        private readonly string _folder = Directory.CreateTempSubdirectory("lekhabook-phone-").FullName;

        public Cable Cable { get; } = new();

        public ApiFixture.UserClient User { get; private set; } = null!;

        /// <summary>The same user on another device: always online.</summary>
        public HttpClient Server => User.Http;

        public Guid CompanyId { get; private set; }

        public Guid Party { get; private set; }

        public Guid Cash { get; private set; }

        public DeviceSyncEngine Engine { get; private set; } = null!;

        public SqliteDataService Data { get; private set; } = null!;

        public SqliteAttachmentService Files { get; private set; } = null!;

        public static async Task<Phone> ForNewCompanyAsync(ApiFixture api)
        {
            var (owner, companyId) = await api.NewCompanyAsync();
            var party = Guid.CreateVersion7();
            (await owner.Http.PostAsJsonAsync($"/api/companies/{companyId}/parties",
                new SavePartyRequest("Ram Store", "Customer", Id: party))).EnsureSuccessStatusCode();
            var phone = await SignedInAsync(api, owner, companyId);
            phone.Party = party;
            phone.Cash = (await phone.ServerGetAsync<List<MoneyAccountDto>>("accounts")).First(a => a.Type == "Cash").Id;
            return phone;
        }

        public static async Task<Phone> SignedInAsync(ApiFixture api, ApiFixture.UserClient user, Guid companyId)
        {
            var phone = new Phone { User = user, CompanyId = companyId };
            var databases = new UserDatabaseFactory(phone._folder);
            var store = new LocalBooksStore(databases, Path.Combine(phone._folder, "files"));
            var tokens = new Tokens(new ApiTokens(user.Token.AccessToken, user.Token.RefreshToken, DateTime.UtcNow.AddMinutes(50)));
            var client = new LekhaBookApi(api.Factory.CreateDefaultClient(phone.Cable), tokens, TimeProvider.System);
            var session = new SessionService(client, tokens, new Settings(), [new Opener(databases)]);
            await session.InitializeAsync();
            await session.SelectCompanyAsync(companyId);

            var files = new ApiAttachmentService(client);
            phone.Engine = new DeviceSyncEngine(databases, store, session, phone._connectivity, client, new CompanyApi(client),
                files, NullLogger<DeviceSyncEngine>.Instance);
            phone.Data = new SqliteDataService(store, session, phone.Engine);
            phone.Files = new SqliteAttachmentService(store, files, session, phone._connectivity, phone.Engine);
            await phone.Engine.SyncNowAsync();
            return phone;
        }

        public void GoOffline() => (Cable.Pulled, _connectivity.IsOnline) = (true, false);

        public void GoOnline() => (Cable.Pulled, _connectivity.IsOnline) = (false, true);

        public async Task SyncAsync()
        {
            GoOnline();
            await Engine.SyncNowAsync();
        }

        /// <summary>Syncs, and runs <paramref name="change"/> while the upload is on its way to the server.</summary>
        public async Task WhilePushingAsync(Func<Task> change)
        {
            Cable.HoldPush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Cable.PushArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            GoOnline();
            var syncing = Engine.SyncNowAsync();
            await Cable.PushArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _connectivity.IsOnline = false; // the change is made on the device only, as during a slow upload
            await change();
            _connectivity.IsOnline = true;
            Cable.HoldPush.SetResult();
            Cable.HoldPush = null;
            await syncing;
        }

        public async Task<TransactionDto> AddAsync(string type, decimal amount, Guid from, Guid to)
        {
            var result = await Data.AddTransactionAsync(CompanyId,
                new CreateTransactionRequest(type, Today, amount, from, to, Id: Guid.CreateVersion7(), CheckForDuplicates: false));
            Assert.True(result.Ok, result.Error);
            return result.Value!;
        }

        public async Task<decimal> CashBalanceAsync() =>
            (await Data.GetMoneyAccountAsync(CompanyId, Cash)).Value!.Balance;

        public async Task<T> ServerGetAsync<T>(string path) =>
            (await Server.GetFromJsonAsync<T>($"/api/companies/{CompanyId}/{path}"))!;

        public Task<List<TransactionDto>> ServerEntriesAsync() => ServerGetAsync<List<TransactionDto>>("transactions");

        public ValueTask DisposeAsync()
        {
            Engine.Dispose();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // A background sync may still hold the file; the temp folder is cleaned by the system.
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class Cable : DelegatingHandler
    {
        public bool Pulled { get; set; }

        public bool BlockUploads { get; set; }

        public TaskCompletionSource? HoldPush { get; set; }

        public TaskCompletionSource? PushArrived { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (Pulled || (BlockUploads && request.Method == HttpMethod.Post && path.EndsWith("/attachments", StringComparison.Ordinal)))
            {
                throw new HttpRequestException("No connection.");
            }

            if (HoldPush is { } hold && path.EndsWith("/sync/push", StringComparison.Ordinal))
            {
                PushArrived!.TrySetResult();
                await hold.Task;
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Switch : IConnectivityService
    {
        public bool IsOnline { get; set; } = true;

        public event EventHandler<bool>? OnlineChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class Tokens(ApiTokens? saved) : ITokenStore
    {
        private ApiTokens? _tokens = saved;

        public Task<ApiTokens?> LoadAsync() => Task.FromResult(_tokens);

        public Task<bool> SaveAsync(ApiTokens tokens, string? returnUrl) => Task.FromResult(false);

        public Task UpdateAsync(ApiTokens tokens) => Task.FromResult(_tokens = tokens);

        public Task ClearAsync() => Task.FromResult(_tokens = null);
    }

    private sealed class Settings : ILocalSettings
    {
        private readonly Dictionary<string, string?> _values = [];

        public Task<string?> GetAsync(string key) => Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class Opener(UserDatabaseFactory databases) : IUserChangeListener
    {
        public Task OnUserChangedAsync(Guid? userId)
        {
            databases.OpenForUser(userId);
            return Task.CompletedTask;
        }
    }
}
