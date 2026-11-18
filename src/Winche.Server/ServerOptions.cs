
namespace Winche.Server;

/// <summary>
/// What the server needs to start — the only settings outside the console (section <c>Winche</c>, so environment
/// variables <c>Winche__ConnectionString</c> etc.). Everything else is set up in the console.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// The server's PostgreSQL database: the default project's documents, files' records, users and settings, the
    /// console accounts every project shares, and the list of other projects (each gets a database of its own, so
    /// this user needs CREATEDB to create projects).
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// A long random secret (at least 32 characters): encrypts secrets saved in the console, and the keys that sign
    /// tokens and file links are derived from it. Keep it safe and the same on every server; losing it loses those.
    /// </summary>
    public string MasterKey { get; set; } = "";

    /// <summary>
    /// While changing the master key: the old one(s), newest first (<c>Winche__PreviousMasterKeys__0</c>). What they
    /// encrypted is still read and moved to the new key; Server settings → Security says when they can go.
    /// </summary>
    public string[] PreviousMasterKeys { get; set; } = [];

    /// <summary>The address apps reach the server at, e.g. https://api.example.com (tokens' issuer, file links).</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>
    /// Where projects keep their files on disk, each in its own folder (<c>&lt;ProjectsPath&gt;/&lt;id&gt;/files</c>; default
    /// /data/projects, the image's volume). Set by the deployment only: the console doesn't show or change it.
    /// </summary>
    public string ProjectsPath { get; set; } = "/data/projects";

    /// <summary>
    /// The code first-run setup asks for, so only someone who can see the server claims it. Unset (the usual), the
    /// server makes one and prints it in its log until someone has set it up. Set it for scripted installs.
    /// </summary>
    public string? SetupCode { get; set; }

    /// <summary>Where the console is served: its pages, its API under <c>/console/v1</c>.</summary>
    public const string ConsolePath = "/console";

    /// <summary>Where projects' apps are: <c>/projects/&lt;id&gt;/…</c>.</summary>
    public const string ProjectsPrefix = "/projects";

    /// <summary>
    /// Console admins need two-step sign-in on to change sensitive settings (default true). Turn off only to try the
    /// server out locally.
    /// </summary>
    public bool RequireTwoFactorForSettings { get; set; } = true;

    /// <summary>The longest a service account's key may last, in days (<c>Winche__ServiceAccountKeyMaxDays</c>, default 730).</summary>
    public int ServiceAccountKeyMaxDays { get; set; } = 730;

    /// <summary>Whether service accounts may have keys (<c>Winche__ServiceAccountKeysAllowed</c>, default true); false: trusted identities only.</summary>
    public bool ServiceAccountKeysAllowed { get; set; } = true;

    /// <summary>The longest any function may run, in minutes (default 9, at most 60): where the server setting starts.</summary>
    public int FunctionsMaxMinutes { get; set; } = 9;

    /// <summary>The most memory a function may ask for, in MB (default 1024): where the server setting starts.</summary>
    public int FunctionsMaxMemoryMb { get; set; } = 1024;

    /// <summary>
    /// How many functions of a project may run at once: where the server setting starts. Default 8 per CPU the server
    /// has (its container's, e.g. 16 on a 2 vCPU Fargate task), at least 8.
    /// </summary>
    public int FunctionsMaxConcurrency { get; set; } = DefaultConcurrency;

    internal static int DefaultConcurrency => Math.Clamp(Environment.ProcessorCount * 8, 8, 256);

    /// <summary>
    /// How long a stop (SIGTERM: a deploy, a scale-in) waits for requests and function runs going on to end, in seconds
    /// (default 30). Set the container's stop timeout to this or more (Fargate's <c>stopTimeout</c>, at most 120).
    /// </summary>
    public int StopTimeoutSeconds { get; set; } = 30;

    /// <summary>Each worker's heap limit in a project's shared functions process, in MB (default 1024).</summary>
    public int FunctionsSharedProcessMb { get; set; } = 1024;

    /// <summary>The most functions processes per project (default 4: the shared one and isolated functions' own).</summary>
    public int FunctionsMaxProcesses { get; set; } = 4;

    /// <summary>
    /// The deno program that runs functions, only when running the server outside its image (the image installs deno
    /// and has it on the PATH). Not a console setting.
    /// </summary>
    public string? DenoPath { get; set; }

    /// <summary>
    /// A folder of npm packages this server provides to functions that import them without naming them in deno.json
    /// (<c>@winche/functions/</c>, <c>@winche/admin/</c>: each a package.json and its built files), for servers that
    /// can't reach npm (<c>Winche__FunctionPackagesPath</c>). Unset: projects name their packages themselves.
    /// </summary>
    public string? FunctionPackagesPath { get; set; }


    /// <summary>
    /// Console sign-in through an OpenID Connect provider, as it starts (<c>Winche__ConsoleSignIn__Issuer</c>, …);
    /// changed later in Server settings → Authentication → Sign-in method.
    /// </summary>
    public ConsoleSignInOptions? ConsoleSignIn { get; set; }

    /// <summary>
    /// Email through the server's own account with an email service (<c>Winche__Email__Provider=ses</c>,
    /// <c>Winche__Email__Region</c>, <c>Winche__Email__FromAddress</c>): the console's emails, and every project's
    /// sign-in emails unless a project sets an SMTP server. No keys are kept: the provider's own credentials of the
    /// server's environment are used (for Amazon SES: AWS_* variables, the ECS task's role, the instance's profile).
    /// </summary>
    public ServerEmailOptions? Email { get; set; }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ConnectionString)) problems.Add("Winche__ConnectionString: the PostgreSQL connection string.");
        if (MasterKey.Length < 32) problems.Add("Winche__MasterKey: a random secret of at least 32 characters (e.g. `openssl rand -base64 48`).");
        if (PreviousMasterKeys.Any(k => k.Length < 32)) problems.Add("Winche__PreviousMasterKeys: each is a master key used before, at least 32 characters.");
        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http"))
            problems.Add("Winche__PublicUrl: the address apps reach this server at, e.g. https://api.example.com.");
        if (Email is { Provider.Length: > 0 } e) problems.AddRange(e.Problems());
        return problems;
    }

}

public sealed class ServerEmailOptions
{
    /// <summary>ses (Amazon SES); none when unset.</summary>
    public string? Provider { get; set; }
    /// <summary>The service's region, e.g. eu-west-1.</summary>
    public string? Region { get; set; }
    /// <summary>The address emails come from (verified with the service), e.g. no-reply@example.com.</summary>
    public string? FromAddress { get; set; }
    /// <summary>The name shown beside it; each project's app name when unset.</summary>
    public string? FromName { get; set; }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Provider != "ses") problems.Add("Winche__Email__Provider: ses (Amazon SES) is the one there is.");
        if (string.IsNullOrWhiteSpace(Region)) problems.Add("Winche__Email__Region: the email service's region, e.g. eu-west-1.");
        if (string.IsNullOrWhiteSpace(FromAddress) || !FromAddress.Contains('@')) problems.Add("Winche__Email__FromAddress: the address emails come from, e.g. no-reply@example.com.");
        return problems;
    }
}

public sealed class ConsoleSignInOptions
{
    public string? Issuer { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Used until one is saved in Server settings.</summary>
    public string? ClientSecret { get; set; }
    public string? Scopes { get; set; }
    public string? ButtonLabel { get; set; }
    public string? RoleClaim { get; set; }
    public string[] AdminValues { get; set; } = [];
    public bool CreateAccounts { get; set; } = true;
    public bool PasswordsOnlyForAdmins { get; set; }
    public bool TrustProviderTwoStep { get; set; } = true;
}
