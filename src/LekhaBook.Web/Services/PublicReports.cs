using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using LekhaBook.Contracts;
using LekhaBook.Domain;
using LekhaBook.UI.Formatting;
using LekhaBook.UI.Localization;
using LekhaBook.Web.Components.PublicReport;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Localization;

namespace LekhaBook.Web.Services;

/// <summary>
/// The public side of a report link (decision D24): the page at /r/{token}, its logo and files, and the CSV export.
/// The browser only ever talks to this host, which reads the report from the API.
/// </summary>
public static class PublicReports
{
    private const string RateLimitPolicy = "public-reports";

    public static IServiceCollection AddPublicReports(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Generous: one page with photos is many requests.
            o.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
        });

    public static void MapPublicReports(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/r/{token}").RequireRateLimiting(RateLimitPolicy);

        // The address is the key, so keep it out of caches, search engines and the Referer header.
        group.AddEndpointFilter(async (context, next) =>
        {
            var headers = context.HttpContext.Response.Headers;
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Robots-Tag"] = "noindex, nofollow";
            headers.XContentTypeOptions = "nosniff";
            return await next(context);
        });

        group.MapGet("/", Page);
        group.MapGet("/csv", Csv);
        group.MapGet("/logo", (string token, IHttpClientFactory clients, CancellationToken ct) =>
            FileAsync(clients, token, "/api/public/report/logo", ct));
        group.MapGet("/files/{attachmentId:guid}", (string token, Guid attachmentId, IHttpClientFactory clients, CancellationToken ct) =>
            FileAsync(clients, token, $"/api/public/report/files/{attachmentId}", ct));
    }

    private static async Task<RazorComponentResult<PublicReportDocument>> Page(
        string token, HttpContext http, IHttpClientFactory clients, CancellationToken ct)
    {
        var (report, status) = await ReadAsync(clients, token, ct);
        var unavailable = report is null && status != HttpStatusCode.NotFound;
        return new RazorComponentResult<PublicReportDocument>(new Dictionary<string, object?>
        {
            [nameof(PublicReportDocument.Report)] = report,
            [nameof(PublicReportDocument.Unavailable)] = unavailable,
            [nameof(PublicReportDocument.Token)] = token,
            [nameof(PublicReportDocument.SignedIn)] = http.User.Identity?.IsAuthenticated == true,
        })
        {
            StatusCode = report is not null ? StatusCodes.Status200OK
                : unavailable ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status404NotFound,
        };
    }

    private static async Task<IResult> Csv(
        string token, IHttpClientFactory clients, IStringLocalizer<SharedResource> loc, CancellationToken ct)
    {
        var (report, status) = await ReadAsync(clients, token, ct);
        if (report is null)
        {
            return Results.StatusCode(status == HttpStatusCode.NotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status503ServiceUnavailable);
        }

        // The byte-order mark makes Excel read Nepali and other scripts correctly.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BuildCsv(report, loc))).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"statement-{report.GeneratedOn:yyyy-MM-dd}.csv");
    }

    /// <summary>The statement as comma-separated rows, with signed balances so a spreadsheet can use them.</summary>
    public static string BuildCsv(PublicReportDto report, IStringLocalizer<SharedResource> loc)
    {
        var calendar = report.Calendar == nameof(CalendarMode.AD) ? CalendarMode.AD : CalendarMode.BS;
        var isParty = report.PartyType is not null;
        var csv = new StringBuilder();

        void Row(params string?[] cells) => csv.AppendJoin(',', cells.Select(Quote)).Append("\r\n");

        Row(report.CompanyName, report.Name);
        Row(
            loc["Common.Date"], loc["Ledger.Particulars"], loc["PublicReport.Details"], loc["Transactions.Form.ChequeNoLabel"],
            isParty ? loc["Ledger.GaveAdded"] : loc["Accounts.Detail.MoneyIn"],
            isParty ? loc["Ledger.GotReduced"] : loc["Accounts.Detail.MoneyOut"],
            loc["Common.Balance"]);
        Row(
            report.OpeningAsOf is { } asOf ? CalendarDates.Format(asOf, calendar) : null,
            loc["Ledger.OpeningBalance"], null, null, null, null, Amount(report.OpeningBalance));
        foreach (var line in report.Lines)
        {
            var title = loc[MasterLabels.TransactionType(line.Type)].Value;
            Row(
                CalendarDates.Format(line.Date, calendar),
                line.VoucherNo.Length == 0 ? title : $"{title} #{line.VoucherNo}",
                string.Join(" · ", new[] { line.OtherAccount, line.Remarks }.Where(s => !string.IsNullOrWhiteSpace(s))),
                line.ChequeNo,
                line.Debit > 0 ? Amount(line.Debit) : null,
                line.Credit > 0 ? Amount(line.Credit) : null,
                Amount(line.Balance));
        }

        return csv.ToString();
    }

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Quotes a cell. Text starting like a formula gets a leading apostrophe, so a remark can't run in a
    /// spreadsheet; plain negative numbers are left as numbers.
    /// </summary>
    private static string Quote(string? cell)
    {
        var text = cell ?? string.Empty;
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])
            && !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            text = "'" + text;
        }

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static async Task<(PublicReportDto? Report, HttpStatusCode Status)> ReadAsync(
        IHttpClientFactory clients, string token, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(clients, token, "/api/public/report", ct);
            return response is null ? (null, HttpStatusCode.NotFound)
                : response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<PublicReportDto>(ct), response.StatusCode)
                : (null, response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, HttpStatusCode.ServiceUnavailable);
        }
    }

    private static async Task<IResult> FileAsync(IHttpClientFactory clients, string token, string path, CancellationToken ct)
    {
        using var response = await SendAsync(clients, token, path, ct);
        if (response is not { IsSuccessStatusCode: true } || response.Content.Headers.ContentType?.MediaType is not { } type)
        {
            return Results.NotFound();
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        // Images show in the page; a PDF is downloaded under its own name.
        var name = type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? null
            : response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        return Results.File(bytes, type, name);
    }

    /// <summary>Null for a token that can't be one of ours, so it never reaches a header.</summary>
    private static async Task<HttpResponseMessage?> SendAsync(IHttpClientFactory clients, string token, string path, CancellationToken ct)
    {
        if (token.Length > 100 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(ReportLinkHeaders.Token, token);
        return await clients.CreateClient("lekhabook-api").SendAsync(request, ct);
    }
}
