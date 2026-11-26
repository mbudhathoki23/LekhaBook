namespace LekhaBook.UI.Formatting;

/// <summary>Turns a file's bytes into a data: URL, for an image or a download link.</summary>
public static class FileDataUrl
{
    public static string From(string contentType, byte[] content) =>
        $"data:{contentType};base64,{Convert.ToBase64String(content)}";
}
