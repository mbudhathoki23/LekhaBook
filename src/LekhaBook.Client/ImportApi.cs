using System.Net.Http.Headers;
using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

/// <summary>Sends a file to the server to be read into tables (D26).</summary>
public sealed class ImportApi(LekhaBookApi api) : IImportService
{
    public async Task<ApiResult<ImportFileDto>> ReadFileAsync(
        Guid companyId, Stream file, string fileName, CancellationToken cancellationToken = default)
    {
        // Held in memory, so the request can be sent again after the sign-in token is refreshed.
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);

        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(buffer.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, "file", fileName);
        return await api.SendContentAsync<ImportFileDto>(HttpMethod.Post, $"/api/companies/{companyId}/import/preview", content);
    }
}
