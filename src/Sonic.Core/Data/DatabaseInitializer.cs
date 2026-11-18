using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;

namespace Sonic.Core.Data;

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class DatabaseInitializer(
    IDbContextFactory<SonicDbContext> factory,
    IAppPaths paths,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A damaged database must not prevent the app from starting: keep a copy and start fresh.
            logger.LogError(ex, "Database migration failed; backing up and recreating {Path}", paths.DatabasePath);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var backup = paths.DatabasePath + $".{DateTime.Now:yyyyMMddHHmmss}.bak";
            if (File.Exists(paths.DatabasePath)) File.Move(paths.DatabasePath, backup);
            await MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
    }
}
