using LekhaBook.Contracts;
using LekhaBook.Domain;

namespace LekhaBook.Api.Endpoints;

/// <summary>
/// Where attachment and logo files are stored, how new file names are made, and how a stored attachment maps to
/// its DTO (AGENT_PLAN § 7, D16). Shared by the transaction and company endpoints so both follow the same
/// path-safety rule.
/// </summary>
internal static class FileStore
{
    /// <summary>The largest company logo accepted, in bytes.</summary>
    public const long MaxLogoBytes = 2 * 1024 * 1024;

    /// <summary>How many attachments one entry can carry (D16).</summary>
    public const int MaxAttachmentsPerEntry = 5;

    public static readonly string[] LogoContentTypes = ["image/jpeg", "image/png", "image/webp"];

    public static string CompanyFolder(Guid companyId) =>
        Path.Combine(AppContext.BaseDirectory, "attachments", companyId.ToString());

    /// <summary>
    /// Where a stored file lives, or null if the key isn't a plain file name: a path (absolute or with "..") must
    /// never be able to reach another file on the server.
    /// </summary>
    public static string? StoragePath(Guid companyId, string key) =>
        key.Length > 0 && key is not ("." or "..") && key == Path.GetFileName(key)
            ? Path.Combine(CompanyFolder(companyId), key)
            : null;

    /// <summary>A new, unguessable file name that keeps the given extension.</summary>
    public static string NewKey(string extension) => $"{Guid.CreateVersion7()}{extension}";

    /// <summary>The file extension for an allowed logo content type.</summary>
    public static string LogoExtension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
    };

    /// <summary>
    /// Deletes a stored file on a best-effort basis. The database row is already changed, so a file that is missing
    /// or locked is not an error worth failing the request for.
    /// </summary>
    public static void DeleteQuietly(Guid companyId, string? key)
    {
        if (key is null || StoragePath(companyId, key) is not { } path)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static AttachmentDto ToDto(Attachment a) =>
        new(a.Id, a.TransactionId, a.AccountId, a.FileName, a.ContentType, a.SizeBytes, a.StorageKey, a.CreatedAtUtc);
}
