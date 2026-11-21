using Winche.Integrity;

namespace Winche.Console.Integrity;

/// <summary>
/// The apps as they were registered before Integrity (area <c>apps</c>): read once, so a project's apps carry over until
/// Integrity's settings are first saved. Nothing writes it any more.
/// </summary>
public sealed record LegacyAppsSettings
{
    public const string Area = "apps";
    public IReadOnlyList<IntegrityApp> Apps { get; init; } = [];
}
