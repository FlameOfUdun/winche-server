namespace Winche.Console.Identity;

/// <summary>The three console roles (rank: Admin ⊃ Member ⊃ Viewer) and their authorization policy names.</summary>
public static class ConsoleRoles
{
    public const string Admin = "Admin";
    public const string Member = "Member";
    public const string Viewer = "Viewer";
    public static readonly string[] All = [Admin, Member, Viewer];

    public const string ViewerPolicy = "ConsoleViewer";   // Viewer|Member|Admin
    /// <summary>Any console account, with a role here or not: approving the CLI (one sign-in for the server) and listing the projects it can use.</summary>
    public const string SignedInPolicy = "ConsoleSignedIn";
    public const string MemberPolicy = "ConsoleMember";   // Member|Admin
    public const string AdminPolicy = "ConsoleAdmin";     // Admin
}
