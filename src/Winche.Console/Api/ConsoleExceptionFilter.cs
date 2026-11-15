using Microsoft.AspNetCore.Http;
using Winche.Core;

namespace Winche.Console.Api;

/// <summary>
/// The console API's answer to what the products throw for a bad request: a 400 (or 409) saying why, not a 500. A
/// conflicting settings save, input the database or storage refuse, and a product used before it's set up.
/// </summary>
internal static class ConsoleExceptionFilter
{
    public static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try
        {
            return await next(ctx);
        }
        catch (SettingsConflictException ex)
        {
            return new WincheError(WincheErrorCode.Aborted, ex.Message, "settings-conflict",
                new System.Text.Json.Nodes.JsonObject { ["latest"] = ex.Latest }).ToResult();
        }
        catch (Winche.Storage.Models.StorageException ex)
        {
            return ex.Error.ToResult();
        }
        catch (Winche.Database.Querying.Planning.PlanValidationException ex)
        {
            return new WincheError(WincheErrorCode.InvalidArgument, ex.Message, "invalid-query").ToResult();
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UndefinedTable)
        {
            // A product's tables aren't there: it hasn't been set up (started) yet.
            return new WincheError(WincheErrorCode.FailedPrecondition, "This product isn't set up yet: start it from its page first.", "not-set-up").ToResult();
        }
    }
}
