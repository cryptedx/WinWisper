using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinWispr.Core.Updates;

/// <summary>A published WinWispr release whose Setup can replace the running app.</summary>
internal sealed record UpdateRelease(
    Version Version,
    Uri SetupUri,
    string SetupFileName,
    long SetupBytes,
    string? SetupDigestSha256,
    Uri ChecksumUri,
    Uri PageUri)
{
    internal string DisplayVersion => AppVersion.Format(Version);
}

/// <summary>Reads the GitHub "latest release" answer of the WinWispr repository.</summary>
internal static partial class ReleaseFeed
{
    internal const string Repository = "cryptedx/WinWisper";
    internal static Uri LatestReleaseUri { get; } = new($"https://api.github.com/repos/{Repository}/releases/latest");
    internal static Uri ReleasesPageUri { get; } = new($"https://github.com/{Repository}/releases/latest");

    /// <summary>Setup is about 80 MB; anything far larger is not a WinWispr Setup.</summary>
    internal const long MaxSetupBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Returns the release, or null when the answer is no published app release (a draft, a prerelease, a
    /// <c>models-vN</c> tag) or lacks the Setup and its SHA-256 sidecar.
    /// </summary>
    internal static UpdateRelease? Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || ReadBool(root, "draft") || ReadBool(root, "prerelease")
            || ReadString(root, "tag_name") is not { } tag
            || TagPattern().Match(tag) is not { Success: true } match)
            return null;

        var version = new Version(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        string setupName = $"WinWispr-Setup-{tag}.exe";
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement? setup = null;
        JsonElement? checksum = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = asset.ValueKind == JsonValueKind.Object ? ReadString(asset, "name") : null;
            if (string.Equals(name, setupName, StringComparison.Ordinal))
                setup = asset;
            else if (string.Equals(name, setupName + ".sha256", StringComparison.Ordinal))
                checksum = asset;
        }
        if (setup is not { } setupAsset || checksum is not { } checksumAsset
            || ReadDownloadUri(setupAsset) is not { } setupUri
            || ReadDownloadUri(checksumAsset) is not { } checksumUri
            || !setupAsset.TryGetProperty("size", out JsonElement size) || !size.TryGetInt64(out long bytes)
            || bytes is <= 0 or > MaxSetupBytes)
            return null;

        string? digest = ReadString(setupAsset, "digest") is { } value
            && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            && IsSha256(value["sha256:".Length..])
                ? value["sha256:".Length..].ToLowerInvariant()
                : null;
        Uri page = ReadString(root, "html_url") is { } html && Uri.TryCreate(html, UriKind.Absolute, out Uri? pageUri)
            && IsGitHubHttps(pageUri)
                ? pageUri
                : ReleasesPageUri;
        return new UpdateRelease(version, setupUri, setupName, bytes, digest, checksumUri, page);
    }

    /// <summary>Reads the hash of a sidecar line "&lt;hex&gt; *&lt;file name&gt;" for exactly that file.</summary>
    internal static string? ParseChecksum(string text, string fileName)
    {
        string line = text.Trim();
        int separator = line.IndexOf(' ', StringComparison.Ordinal);
        if (separator <= 0 || line.Contains('\n', StringComparison.Ordinal))
            return null;
        string hash = line[..separator];
        string name = line[(separator + 1)..].TrimStart().TrimStart('*');
        return IsSha256(hash) && string.Equals(name, fileName, StringComparison.Ordinal) ? hash.ToLowerInvariant() : null;
    }

    internal static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    /// <summary>Release downloads start on github.com; the redirect to its asset storage stays on HTTPS.</summary>
    internal static bool IsGitHubHttps(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    private static Uri? ReadDownloadUri(JsonElement asset) =>
        ReadString(asset, "browser_download_url") is { } text
        && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
        && IsGitHubHttps(uri)
        && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal)
            ? uri
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"^v(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
