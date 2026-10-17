using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core.Updates;

namespace WinWispr.Tests.Core.Updates;

[TestClass]
public sealed class ReleaseFeedTests
{
    internal const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static string Release(
        string tag = "v1.11.0",
        bool draft = false,
        bool prerelease = false,
        string? setupUrl = null,
        bool withChecksum = true,
        long size = 1000,
        string? digest = "sha256:" + Digest)
    {
        string download = $"https://github.com/cryptedx/WinWisper/releases/download/{tag}/";
        var assets = new List<object>
        {
            new
            {
                name = $"WinWispr-Setup-{tag}.exe",
                size,
                digest,
                browser_download_url = setupUrl ?? download + $"WinWispr-Setup-{tag}.exe"
            },
            new
            {
                name = $"WinWispr-portable-{tag}.zip",
                size = 2000,
                digest = (string?)null,
                browser_download_url = download + $"WinWispr-portable-{tag}.zip"
            }
        };
        if (withChecksum)
            assets.Add(new
            {
                name = $"WinWispr-Setup-{tag}.exe.sha256",
                size = 100,
                digest = (string?)null,
                browser_download_url = download + $"WinWispr-Setup-{tag}.exe.sha256"
            });
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            html_url = $"https://github.com/cryptedx/WinWisper/releases/tag/{tag}",
            assets
        });
    }

    [TestMethod]
    public void A_published_release_yields_its_setup_checksum_and_digest()
    {
        UpdateRelease release = ReleaseFeed.Parse(Release())!;

        Assert.AreEqual(new Version(1, 11, 0), release.Version);
        Assert.AreEqual("1.11.0", release.DisplayVersion);
        Assert.AreEqual("WinWispr-Setup-v1.11.0.exe", release.SetupFileName);
        Assert.AreEqual(1000, release.SetupBytes);
        Assert.AreEqual(Digest, release.SetupDigestSha256);
        Assert.AreEqual(
            "https://github.com/cryptedx/WinWisper/releases/download/v1.11.0/WinWispr-Setup-v1.11.0.exe.sha256",
            release.ChecksumUri.AbsoluteUri);
        Assert.AreEqual("https://github.com/cryptedx/WinWisper/releases/tag/v1.11.0", release.PageUri.AbsoluteUri);
    }

    [TestMethod]
    public void Drafts_prereleases_model_releases_and_odd_tags_are_not_updates()
    {
        Assert.IsNull(ReleaseFeed.Parse(Release(draft: true)));
        Assert.IsNull(ReleaseFeed.Parse(Release(prerelease: true)));
        foreach (string tag in new[] { "models-v3", "v1.11", "v1.11.0-rc1", "1.11.0", "v01.2.3" })
            Assert.IsNull(ReleaseFeed.Parse(Release(tag)), tag);
    }

    [TestMethod]
    public void A_release_without_its_checksum_or_with_an_implausible_size_is_not_an_update()
    {
        Assert.IsNull(ReleaseFeed.Parse(Release(withChecksum: false)));
        Assert.IsNull(ReleaseFeed.Parse(Release(size: 0)));
        Assert.IsNull(ReleaseFeed.Parse(Release(size: ReleaseFeed.MaxSetupBytes + 1)));
    }

    [TestMethod]
    public void Downloads_must_come_from_this_repository_on_github_over_https()
    {
        foreach (string url in new[]
                 {
                     "http://github.com/cryptedx/WinWisper/releases/download/v1.11.0/WinWispr-Setup-v1.11.0.exe",
                     "https://example.com/cryptedx/WinWisper/releases/download/v1.11.0/WinWispr-Setup-v1.11.0.exe",
                     "https://github.com/someone/WinWisper/releases/download/v1.11.0/WinWispr-Setup-v1.11.0.exe",
                     "https://user@github.com/cryptedx/WinWisper/releases/download/v1.11.0/WinWispr-Setup-v1.11.0.exe",
                     "https://github.com:8443/cryptedx/WinWisper/releases/download/v1.11.0/WinWispr-Setup-v1.11.0.exe"
                 })
            Assert.IsNull(ReleaseFeed.Parse(Release(setupUrl: url)), url);
    }

    [TestMethod]
    public void A_missing_or_malformed_digest_leaves_the_sidecar_as_the_only_checksum()
    {
        Assert.IsNull(ReleaseFeed.Parse(Release(digest: null))!.SetupDigestSha256);
        Assert.IsNull(ReleaseFeed.Parse(Release(digest: "sha256:abc"))!.SetupDigestSha256);
        Assert.IsNull(ReleaseFeed.Parse(Release(digest: "md5:" + Digest))!.SetupDigestSha256);
    }

    [TestMethod]
    public void The_sidecar_names_the_hash_of_exactly_that_file()
    {
        string upper = Digest.ToUpperInvariant();
        Assert.AreEqual(Digest, ReleaseFeed.ParseChecksum($"{upper} *WinWispr-Setup-v1.11.0.exe\r\n", "WinWispr-Setup-v1.11.0.exe"));
        Assert.AreEqual(Digest, ReleaseFeed.ParseChecksum($"{Digest}  WinWispr-Setup-v1.11.0.exe", "WinWispr-Setup-v1.11.0.exe"));
        Assert.IsNull(ReleaseFeed.ParseChecksum($"{Digest} *WinWispr-Setup-v1.10.0.exe", "WinWispr-Setup-v1.11.0.exe"));
        Assert.IsNull(ReleaseFeed.ParseChecksum($"{Digest[..63]} *WinWispr-Setup-v1.11.0.exe", "WinWispr-Setup-v1.11.0.exe"));
        Assert.IsNull(ReleaseFeed.ParseChecksum(
            $"{Digest} *WinWispr-Setup-v1.11.0.exe\n{Digest} *other.exe", "WinWispr-Setup-v1.11.0.exe"));
        Assert.IsNull(ReleaseFeed.ParseChecksum(string.Empty, "WinWispr-Setup-v1.11.0.exe"));
    }

    [TestMethod]
    public void Versions_read_as_major_minor_patch_without_build_metadata()
    {
        Assert.AreEqual(new Version(1, 10, 3), AppVersion.Parse("1.10.3+abc123"));
        Assert.AreEqual(new Version(1, 10, 0), AppVersion.Parse("1.10"));
        Assert.AreEqual(new Version(1, 10, 3), AppVersion.Parse("1.10.3.0"));
        Assert.IsNull(AppVersion.Parse("next"));
        Assert.IsNull(AppVersion.Parse(null));
        Assert.AreEqual("1.10.0", AppVersion.Format(new Version(1, 10)));
    }
}
