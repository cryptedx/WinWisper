using System.Reflection;

namespace WinWispr.Core.Updates;

/// <summary>The version of the running app as MAJOR.MINOR.PATCH, the form release tags carry.</summary>
internal static class AppVersion
{
    internal static Version Current { get; } = Read();

    internal static string Display => Format(Current);

    internal static string Format(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    /// <summary>Reads MAJOR.MINOR.PATCH and drops build metadata after a <c>+</c>; anything else is null.</summary>
    internal static Version? Parse(string? text)
    {
        string? core = text?.Split('+', 2)[0].Trim();
        if (string.IsNullOrEmpty(core) || !Version.TryParse(core, out Version? version))
            return null;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    private static Version Read()
    {
        // Only the E2E profile may pretend to be an older version, so that an update can be tried on a test machine.
        if (AppDataPaths.IsTaskBoardE2e
            && Parse(Environment.GetEnvironmentVariable("WINWISPR_UPDATE_TEST_CURRENT_VERSION")) is { } pretended)
            return pretended;
        Assembly assembly = typeof(AppVersion).Assembly;
        return Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
            ?? Parse(assembly.GetName().Version?.ToString(3))
            ?? new Version(0, 0, 0);
    }
}
