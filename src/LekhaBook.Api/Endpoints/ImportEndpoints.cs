using LekhaBook.Api.Import;
using LekhaBook.Api.Security;
using LekhaBook.Contracts;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LekhaBook.Api.Endpoints;

/// <summary>
/// Reads a file to be imported into plain tables (D26). Nothing is stored or written: the rows are saved later
/// through the party and entry endpoints, which apply their usual rules.
/// </summary>
public static class ImportEndpoints
{
    /// <summary>Any one of these lets a member have a file read, since each allows adding something a file can hold.</summary>
    private static readonly string[] AddPermissions =
    [
        Permission.CustomersCreate, Permission.SuppliersCreate,
        Permission.PaymentInCreate, Permission.PaymentOutCreate, Permission.IncomeExpenseCreate, Permission.AccountsAdjust,
    ];

    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var company = app.MapGroup("/api/companies/{companyId:guid}").RequireAuthorization().RequireCompanyMember();
        company.MapPost("/import/preview", Preview);
    }

    private static async Task<Results<Ok<ImportFileDto>, ValidationProblem, ProblemHttpResult>> Preview(
        HttpRequest request, CompanyAccess access, CancellationToken ct)
    {
        var member = await access.RequireMemberAsync();
        if (!AddPermissions.Any(member.Has))
        {
            return TypedResults.Problem("You don't have permission to add anything, so you can't import.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (!request.HasFormContentType)
        {
            return Invalid("Expected multipart form data.");
        }

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return Invalid("A file is required.");
        }

        if (file.Length > ImportLimits.MaxFileBytes)
        {
            return Invalid("File too large. Maximum 5 MB.");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        try
        {
            return TypedResults.Ok(FileTableReader.Read(buffer.ToArray(), file.FileName));
        }
        catch (ImportFileException ex)
        {
            return Invalid(ex.Message);
        }
    }

    private static ValidationProblem Invalid(string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [message] });
}
