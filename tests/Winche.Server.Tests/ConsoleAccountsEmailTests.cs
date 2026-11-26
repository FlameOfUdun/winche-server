using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Winche.Server.Tests;

/// <summary>The console's own accounts never write a working link to the log, even with no email server (or a saved "log links only").</summary>
[Collection("postgres")]
public class ConsoleAccountsEmailTests(PostgresFixture pg)
{
    [Fact]
    public async Task Console_account_emails_are_never_logged_with_their_links()
    {
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), Directory.CreateTempSubdirectory("winche-server-files-").FullName);
        _ = server.CreateClient();
        var accounts = server.Services.GetRequiredService<Winche.Server.ConsoleAccounts>().Services;
        var email = accounts.GetRequiredService<Winche.Auth.WincheAuthOptions>().EmailSending!;
        Assert.False(email.LogWithLinks);
    }
}
