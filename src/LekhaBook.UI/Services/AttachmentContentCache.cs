using LekhaBook.Contracts;

namespace LekhaBook.UI.Services;

/// <summary>
/// Keeps the bytes of recently shown attachments, so rows that re-render don't fetch the same file again.
/// Scoped: one per browser circuit, or per window in the MAUI app. Failed fetches are not kept.
/// </summary>
public sealed class AttachmentContentCache(IAttachmentService attachments)
{
    private const int MaxEntries = 20;

    private readonly Dictionary<(Guid CompanyId, Guid AttachmentId), Task<ApiResult<AttachmentFile>>> _entries = [];

    public Task<ApiResult<AttachmentFile>> GetAsync(Guid companyId, Guid attachmentId)
    {
        var key = (companyId, attachmentId);
        if (_entries.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var fetch = FetchAsync(key);
        _entries[key] = fetch;
        if (_entries.Count > MaxEntries)
        {
            // Not an LRU: dropping the oldest entry keeps memory bounded for a few dozen thumbnails.
            _entries.Remove(_entries.Keys.First());
        }

        return fetch;
    }

    /// <summary>Forgets a deleted attachment.</summary>
    public void Invalidate(Guid companyId, Guid attachmentId) => _entries.Remove((companyId, attachmentId));

    private async Task<ApiResult<AttachmentFile>> FetchAsync((Guid CompanyId, Guid AttachmentId) key)
    {
        var result = await attachments.GetContentAsync(key.CompanyId, key.AttachmentId);
        if (!result.Ok)
        {
            _entries.Remove(key);
        }

        return result;
    }
}
