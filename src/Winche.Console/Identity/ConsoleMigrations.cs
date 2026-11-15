using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Winche.Console.Identity;

/// <summary>Migrating the console's databases without EF logging failed queries on a new database.</summary>
internal static class ConsoleMigrations
{
    /// <summary>
    /// The database (if it isn't there), then the migrations' history table, then the migrations: EF's own probe for the
    /// history table would otherwise log a failed query on every new database.
    /// </summary>
    public static async Task MigrateQuietlyAsync(DatabaseFacade db, CancellationToken ct)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct)) await creator.CreateAsync(ct);
        await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(ct);
        await db.MigrateAsync(ct);
    }
}
