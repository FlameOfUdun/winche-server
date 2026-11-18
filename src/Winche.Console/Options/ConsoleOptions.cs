using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Email;
using Winche.Console.Tabs;

namespace Winche.Console.Options;

/// <summary>Configuration for the console's built-in authentication.</summary>
public sealed class ConsoleOptions
{
    /// <summary>Connection string for the console's own auth database (Identity tables). Required.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Where console accounts live, when not in <see cref="ConnectionString"/>: several consoles (one per project, as
    /// Winche Server runs them) pointing at the same accounts database share their accounts.
    /// </summary>
    public string? AccountsConnectionString { get; set; }

    /// <summary>
    /// The sign-in cookie's path (default: the app's path base). <c>/</c> lets consoles under different paths of one
    /// address (<c>/p/clinic/_console</c>, <c>/p/wards/_console</c>) share a sign-in, with <see cref="AccountsConnectionString"/>
    /// and one data-protection key ring.
    /// </summary>
    public string? SignInCookiePath { get; set; }

    /// <summary>
    /// Where the Logs tab's entries (with their log levels and saved queries) are kept, when not in
    /// <see cref="ConnectionString"/>: a project's data database, away from its configuration.
    /// </summary>
    public string? LogsConnectionString { get; set; }

    /// <summary>How long a console sign-in lasts without use (default 7 days).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The server's own settings, for its admins (Winche Server provides them); null: the console has no Server settings.
    /// </summary>
    public Project.IWincheServerSettings? ServerSettings { get; set; }

    /// <summary>
    /// The code first-run setup asks for (Winche Server prints it in its log), so only someone who can see the server
    /// claims a fresh console; null: no code is asked for.
    /// </summary>
    public Func<CancellationToken, Task<string>>? SetupCode { get; set; }

    /// <summary>Where this project's data is kept, and moving it (Winche Server provides it); null: not shown.</summary>
    public Project.IWincheDataLocation? DataLocation { get; set; }

    internal string LogsDatabase => string.IsNullOrWhiteSpace(LogsConnectionString) ? ConnectionString : LogsConnectionString;

    internal string AccountsDatabase => string.IsNullOrWhiteSpace(AccountsConnectionString) ? ConnectionString : AccountsConnectionString;


    /// <summary>
    /// The project's id, as apps see it in <c>/.well-known/winche</c> and in the <c>winche_options.dart</c> that
    /// <c>winche configure</c> and the console's Project settings → General write (default <c>winche</c>).
    /// </summary>
    public string ProjectId { get; set; } = "winche";

    /// <summary>The project's name for people, if different from <see cref="ProjectId"/>.</summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// Guided setup, for a server run on its own (Winche Server): after the first admin, the console asks to
    /// create the project (name, id) and which products to start; every product is off until started, and its public
    /// API refuses requests until then (<c>app.UseWincheProjectGate()</c>). Needs <c>AddWincheConfig</c>. A console that
    /// was already in use keeps every product on. <see cref="ProjectId"/> and <see cref="ProjectName"/> become the
    /// suggestions the setup starts from.
    /// </summary>
    public ConsoleOptions UseProjectSetup(bool carryOver = true)
    {
        ProjectSetup = true;
        ProjectCarryOver = carryOver;
        return this;
    }

    internal bool ProjectSetup { get; private set; }

    /// <summary>
    /// Who may use this console, and as what, is this project's own list (Project settings → Members), not the
    /// accounts' roles: for a project among several sharing accounts (<see cref="AccountsConnectionString"/>). Accounts
    /// with the Admin role (server admins) are admins of every project. Console accounts.
    /// </summary>
    public ConsoleOptions UseProjectMembers()
    {
        ProjectMembers = true;
        return this;
    }

    internal bool ProjectMembers { get; private set; }

    /// <summary>
    /// The server's own console (Winche Server at its root): first-run setup (the first admin), sign-in, the accounts,
    /// Server settings and the projects — and no project of its own. Each project has its own console at
    /// <c>/p/&lt;id&gt;</c>. Accounts' roles are server roles here: Admin (a server admin) or none.
    /// </summary>
    public ConsoleOptions UseServerConsole()
    {
        ServerConsole = true;
        return this;
    }

    internal bool ServerConsole { get; private set; }

    /// <summary>
    /// Whether a console that was in use before setup existed becomes a project with every product on. Off for
    /// projects a server creates (they share accounts with older ones, which would look like "in use").
    /// </summary>
    internal bool ProjectCarryOver { get; private set; } = true;

    /// <summary>Optional first-admin seed; applied on startup only when no users exist.</summary>
    public string? SeedAdminEmail { get; set; }
    public string? SeedAdminPassword { get; set; }

    /// <summary>
    /// The console's accounts: a Winche Auth of their own that the host started (<see cref="Identity.ConsoleAccountsApp"/>),
    /// the same for every console the host runs.
    /// </summary>
    public Identity.IConsoleAccountsHost? Accounts { get; set; }

    /// <summary>Self-service password reset (effective only when an IConsoleEmailSender is registered). Phase 4.</summary>
    public bool AllowSelfServicePasswordReset { get; set; } = true;

    /// <summary>
    /// Console admins must have two-step sign-in on to change sensitive settings — trusted providers, token mapping,
    /// its secrets (default true). Signing in through an identity provider can count as two-step
    /// (Server settings → Authentication → Sign-in method).
    /// </summary>
    public bool RequireTwoFactorForSettings { get; set; } = true;

    /// <summary>The longest a service account's key may last, in days (default 730).</summary>
    public int ServiceAccountKeyMaxDays { get; set; } = 730;

    /// <summary>Whether service accounts may have keys; when false, only trusted identities sign them in.</summary>
    public bool ServiceAccountKeysAllowed { get; set; } = true;

    /// <summary>
    /// Buffered email-sender registration; applied against the host's service collection by
    /// AddWincheConsole. Null when the consumer never calls UseEmailSender (email features stay off).
    /// </summary>
    internal Action<IServiceCollection>? EmailSenderRegistration { get; private set; }

    /// <summary>
    /// Register an <see cref="IConsoleEmailSender"/> implementation type; resolved from DI so it can take
    /// constructor dependencies. Enables self-service password reset and admin invites.
    /// </summary>
    public ConsoleOptions UseEmailSender<TSender>(ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TSender : class, IConsoleEmailSender
    {
        EmailSenderRegistration = services =>
            services.Add(new ServiceDescriptor(typeof(IConsoleEmailSender), typeof(TSender), lifetime));
        return this;
    }

    /// <summary>
    /// Register an <see cref="IConsoleEmailSender"/> via a factory with access to the
    /// <see cref="IServiceProvider"/> for full control over construction.
    /// </summary>
    public ConsoleOptions UseEmailSender(
        Func<IServiceProvider, IConsoleEmailSender> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(factory);
        EmailSenderRegistration = services =>
            services.Add(new ServiceDescriptor(typeof(IConsoleEmailSender), factory, lifetime));
        return this;
    }

    /// <summary>Register an already-constructed singleton <see cref="IConsoleEmailSender"/> instance.</summary>
    /// <summary>
    /// Sends console emails (invites, password resets) through Winche Auth's SMTP server, set up in the console under
    /// Authentication → Settings → Email. Until it's set up, invites and resets say so. Needs AddWincheAuth.
    /// </summary>
    public ConsoleOptions UseWincheAuthEmail() =>
        UseEmailSender(sp => new Email.WincheAuthConsoleEmail(
            (Winche.Auth.WincheAuthOptions)(sp.GetService(typeof(Winche.Auth.WincheAuthOptions))
                ?? throw new InvalidOperationException("UseWincheAuthEmail() needs AddWincheAuth(…).")), sp));

    public ConsoleOptions UseEmailSender(IConsoleEmailSender instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        EmailSenderRegistration = services => services.AddSingleton(instance);
        return this;
    }

    internal BuiltInTabConfig? DatabaseTab { get; private set; }
    internal BuiltInTabConfig? StorageTab { get; private set; }

    // Kept as pass-throughs so the existing rules wiring (store factory, startup service, subsystem
    // registration, MapConsoleRulesEndpoints) is unchanged: a rules editor is enabled iff its tab set one.
    internal RulesEditorOptions? DatabaseRulesEditor => DatabaseTab?.RulesEditor;
    internal RulesEditorOptions? StorageRulesEditor => StorageTab?.RulesEditor;
    internal BuiltInTabConfig? AssetsTab { get; private set; }
    internal RulesEditorOptions? AssetsRulesEditor => AssetsTab?.RulesEditor;

    /// <summary>Enable the Database browser tab (requires AddWincheDatabase). Configure its min role and optional rules editor.</summary>
    public ConsoleOptions AddDatabaseTab(Action<BuiltInTabBuilder>? configure = null)
    {
        DatabaseTab = BuildBuiltInTab(configure, DatabaseTab, "Database");
        return this;
    }

    /// <summary>Enable the Storage browser tab (requires AddWincheStorage). Configure its min role and optional rules editor.</summary>
    public ConsoleOptions AddStorageTab(Action<BuiltInTabBuilder>? configure = null)
    {
        StorageTab = BuildBuiltInTab(configure, StorageTab, "Storage");
        if (StorageTab.History)
            throw new InvalidOperationException("UseHistory() is for the Database tab: document history comes from Winche.Database.");
        return this;
    }

    /// <summary>Enable the Assets page (requires AddWincheAssets): channels, releases, its rules editor and settings.</summary>
    public ConsoleOptions AddAssetsTab(Action<BuiltInTabBuilder>? configure = null)
    {
        AssetsTab = BuildBuiltInTab(configure, AssetsTab, "Assets");
        if (AssetsTab.History || AssetsTab.Logs)
            throw new InvalidOperationException("The Assets tab has no history or logs of its own: its rules editor only.");
        return this;
    }

    private static BuiltInTabConfig BuildBuiltInTab(Action<BuiltInTabBuilder>? configure, BuiltInTabConfig? existing, string name)
    {
        if (existing is not null)
            throw new InvalidOperationException($"The {name} tab is already registered.");
        var b = new BuiltInTabBuilder();
        configure?.Invoke(b);
        return new BuiltInTabConfig { MinRole = b.MinRole, RulesEditor = b.RulesEditor, Logs = b.Logs, History = b.History };
    }

    /// <summary>What the Logs tab and the Logs sub-tabs keep: level, retention, size (see <see cref="Logs.LogsOptions"/>).</summary>
    public Logs.LogsOptions Logs { get; } = new();

    internal bool AppLogsTab { get; private set; }

    /// <summary>
    /// Add a top-level <b>Logs</b> tab (Admins) with the app's log entries — its own code and the framework, as well as
    /// Winche's — searchable by level, source, text (error ids too) and time, with a live mode. Entries at
    /// <see cref="Logs.LogsOptions.MinimumLevel"/> and above (Warning by default) are kept in the console's database
    /// (<see cref="ConnectionString"/>) for <see cref="Logs.LogsOptions.Retention"/>.
    /// </summary>
    public ConsoleOptions AddLogsTab(Action<Logs.LogsOptions>? configure = null)
    {
        configure?.Invoke(Logs);
        AppLogsTab = true;
        return this;
    }

    internal bool AuthenticationTab { get; private set; }

    /// <summary>
    /// Add an <b>Authentication</b> tab (Admins) for the app's users — needs <c>AddWincheAuth</c> (Winche.Auth): search
    /// users, see how they sign in and their sessions, set custom claims, disable, unlock, sign out everywhere, delete;
    /// and the sign-in methods, token settings and signing keys. (The console's own admins stay in Access.)
    /// </summary>
    public ConsoleOptions AddAuthenticationTab()
    {
        AuthenticationTab = true;
        return this;
    }

    internal bool AnyLogs => AppLogsTab || DatabaseTab?.Logs == true || StorageTab?.Logs == true;
}
