using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Winche.Functions;
using Winche.Integrity;
using Winche.Integrity.AspNetCore;

namespace Winche.Console.Integrity;

/// <summary>Functions' view of Integrity: which app proved it's the project's own, for <c>ctx.app</c> and <c>integrity: true</c> callables.</summary>
public static class FunctionsIntegrity
{
    /// <summary>
    /// The request's proof: the gate's verdict, or (where Functions isn't checked, mode off) the token checked now.
    /// For <c>FunctionsOptions.AppProof</c>.
    /// </summary>
    public static FunctionAppProof? ProofOf(HttpContext http)
    {
        var verdict = IntegrityEndpoints.VerdictOf(http)
            ?? http.RequestServices.GetService<WincheIntegrity>()?.Verify(IntegrityEndpoints.TokenOf(http.Request));
        return verdict is null ? null : new FunctionAppProof(verdict.AppId, verdict.Provider);
    }
}
