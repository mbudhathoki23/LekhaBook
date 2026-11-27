using LekhaBook.Contracts;

namespace LekhaBook.UI.Services;

/// <summary>
/// Reads a file from another app (CSV, Excel, HTML or PDF) into plain tables (D26). Nothing is saved: imported
/// rows go through <see cref="IDataService"/> like typed ones.
/// </summary>
public interface IImportService
{
    Task<ApiResult<ImportFileDto>> ReadFileAsync(
        Guid companyId, Stream file, string fileName, CancellationToken cancellationToken = default);
}
