namespace Winche.Server;

/// <summary>
/// The server's own secrets (its settings, the console accounts', the server console's) and AWS: references read
/// from Secrets Manager and Parameter Store, and those Winche keeps there under <c>&lt;prefix&gt;/_server/</c> (no
/// project id has a <c>_</c>, so never a project's). One place, so every store the server's pages save through agrees.
/// </summary>
internal static class ServerSecretsInAws
{
    public static void Add(IServiceCollection services, ServerOptions server)
    {
        Winche.Secrets.Aws.SecretsAwsServiceCollectionExtensions.AddWincheSecretsAws(services, o => o.ServiceUrl = server.SecretsAwsServiceUrl);
        Winche.Secrets.SecretsServiceCollectionExtensions.AddWincheSecrets(services, o => o.ManagedPrefix = $"{server.SecretsPrefix.Trim('/')}/_server/");
    }
}
