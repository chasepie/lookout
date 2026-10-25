namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>A Script uploaded with a request, to create a Watch, become a new Script version, or execute as a Test run.</summary>
public sealed record UploadedScript(string Source, string FileKind)
{
    /// <summary>Reads an uploaded Script, or explains why it can't be used.</summary>
    public static async Task<(UploadedScript? Script, string? Problem)> ReadAsync(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var fileKind = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (fileKind is not (".ts" or ".js") || file.Length is < 1 or > 65536)
        {
            return (null, "Upload a .ts or .js Script between 1 byte and 64 KiB.");
        }

        using var reader = new StreamReader(file.OpenReadStream());
        var source = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(source))
        {
            return (null, "Script source must not be empty.");
        }

        return (new UploadedScript(source, fileKind[1..]), null);
    }
}
