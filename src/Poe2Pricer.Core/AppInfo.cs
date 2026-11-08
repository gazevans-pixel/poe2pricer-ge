using System.Reflection;

namespace Poe2Pricer.Core;

public static class AppInfo
{
    // What changed in each version, on github.com; the version number in the window header
    // links to it (opens in the browser on a click, the program itself requests nothing).
    public const string ChangelogPage = "https://github.com/saxe78/poe2pricer/blob/main/CHANGELOG.md";

    // The repository: where a site operator who sees the program in a log finds its author.
    public const string Repository = "https://github.com/saxe78/poe2pricer";

    // How every request of the program introduces itself (poe.ninja, poecdn, the trade site,
    // GitHub, the rumour sources): the program, its version and the repository, the form
    // operators know ("+URL" for the contact). It proves nothing — anyone can send the same
    // line — it only says who is asking and where to turn.
    public static string UserAgent => $"poe2pricer/{Version} (+{Repository})";

    // The program's version: <Version> in Directory.Build.props. The build may append the
    // commit after a "+"; that part is not shown.
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    // Name and version as shown to the user: "poe2pricer v0.1.0".
    public static string NameAndVersion => $"poe2pricer v{Version}";

    // The game version this release was built and tested against, as the game's log names it
    // (tools/game_version.py; the same as CHANGELOG's "Built and tested against"). The status
    // line names the game's version when its number differs (ChainTracker reads it from
    // Client.txt): the letter of a hotfix (0.5.5e, 0.5.5f: every two to three weeks, measured
    // 26.09.–08.11.2026) does not count, releases of the program come rarer than that, and a
    // hotfix seldom moves a window.
    public const string TestedGameVersion = "0.5.5f";

    // Whether two game versions are the same patch but for the hotfix letter: "0.5.5f" and
    // "0.5.5g" are, "0.5.5f" and "0.5.6" are not.
    public static bool SameGamePatch(string a, string b) => PatchNumber(a) == PatchNumber(b);

    private static string PatchNumber(string version) => version.TrimEnd("abcdefghijklmnopqrstuvwxyz".ToCharArray());
}
