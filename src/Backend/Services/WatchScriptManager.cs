using System.Runtime.CompilerServices;
using ChasePie.Lookout.Backend.Database;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Services;

public class WatchScriptManager
{
    internal const string LOOKOUT_WATCH_SCRIPTS_DIR = "LOOKOUT_WATCH_SCRIPTS_DIR";

    private readonly LookoutDbContext _dbContext;
    private readonly string _scriptDir;

    public WatchScriptManager(
        IConfiguration configuration,
        LookoutDbContext dbContext)
    {
        _dbContext = dbContext;

        var scriptDir = configuration[LOOKOUT_WATCH_SCRIPTS_DIR];
        if (string.IsNullOrWhiteSpace(scriptDir))
        {
            throw new InvalidOperationException($"Environment variable {LOOKOUT_WATCH_SCRIPTS_DIR} must be set");
        }

        _scriptDir = scriptDir;
        if (!Directory.Exists(_scriptDir))
        {
            Directory.CreateDirectory(_scriptDir);
        }
    }

    public async Task<(bool successful, string? errorMessage)> SaveScriptAsync(
        int watchId, IFormFile scriptFile, CancellationToken cancellationToken)
    {
        var watch = await _dbContext.Watches.SingleAsync(w => w.Id == watchId, cancellationToken);

        var fileKind = Path.GetExtension(scriptFile.FileName).ToLowerInvariant();
        if (fileKind is not (".ts" or ".js") || scriptFile.Length is < 1 or > 65536)
        {
            return (false, "Upload a .ts or .js Script between 1 byte and 64 KiB.");
        }

        using var reader = new StreamReader(scriptFile.OpenReadStream());
        var source = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(source))
        {
            return (false, "Script source must not be empty.");
        }

        using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        watch.ScriptFileName = scriptFile.FileName;
        watch.ScriptFileId ??= Guid.NewGuid();

        var fileExtension = Path.GetExtension(scriptFile.FileName);
        var saveFileName = watch.ScriptFileId + fileExtension;
        var saveFilePath = Path.Combine(_scriptDir, saveFileName);
        var backupFilePath = saveFilePath + ".bak";

        var revertOnFail = false;
        if (File.Exists(saveFilePath))
        {
            revertOnFail = true;
            File.Move(saveFilePath, backupFilePath);
        }

        try
        {
            await File.WriteAllTextAsync(saveFilePath, source, cancellationToken);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (revertOnFail)
            {
                File.Move(backupFilePath, saveFilePath);
            }
            return (false, ex.Message);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, null);
    }
}
