using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core.Updates;

namespace WinWispr.Tests.Core.Updates;

[TestClass]
public sealed class UpdateServiceTests
{
    private static readonly byte[] SetupBytes = Encoding.ASCII.GetBytes("pretend this is WinWispr Setup");
    private static readonly string SetupHash = Convert.ToHexStringLower(SHA256.HashData(SetupBytes));

    [TestMethod]
    public async Task A_newer_release_is_available_and_the_same_or_an_older_one_is_not()
    {
        using var directory = new TemporaryDirectory();
        var newer = Service(directory, new FeedHandler(ReleaseJson()), current: new Version(1, 10, 3));
        Assert.AreEqual(UpdateState.Available, await newer.CheckAsync(CancellationToken.None));
        Assert.AreEqual("1.11.0", newer.Available!.DisplayVersion);

        var same = Service(directory, new FeedHandler(ReleaseJson()), current: new Version(1, 11, 0));
        Assert.AreEqual(UpdateState.UpToDate, await same.CheckAsync(CancellationToken.None));
        Assert.IsNull(same.Available);
    }

    [TestMethod]
    public async Task The_check_asks_the_latest_release_of_the_repository_as_json()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FeedHandler(ReleaseJson());
        await Service(directory, handler).CheckAsync(CancellationToken.None);

        HttpRequestMessage request = handler.Requests.Single();
        Assert.AreEqual("https://api.github.com/repos/cryptedx/WinWisper/releases/latest", request.RequestUri!.AbsoluteUri);
        Assert.AreEqual("application/vnd.github+json", request.Headers.Accept.Single().MediaType);
    }

    [TestMethod]
    public async Task A_missing_release_or_an_unreachable_feed_fails_with_a_message()
    {
        using var directory = new TemporaryDirectory();
        var missing = Service(directory, new FeedHandler(null, HttpStatusCode.NotFound));
        Assert.AreEqual(UpdateState.Failed, await missing.CheckAsync(CancellationToken.None));
        StringAssert.Contains(missing.FailureMessage, "No published WinWispr release");

        var offline = Service(directory, new FeedHandler(null, throws: true));
        Assert.AreEqual(UpdateState.Failed, await offline.CheckAsync(CancellationToken.None));
        Assert.IsInstanceOfType<HttpRequestException>(offline.LastException);
    }

    [TestMethod]
    public async Task A_policy_turns_the_check_off_without_asking_github()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FeedHandler(ReleaseJson());
        var service = Service(directory, handler, policyDisabled: true);

        Assert.AreEqual(UpdateState.DisabledByPolicy, await service.CheckAsync(CancellationToken.None));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task A_verified_setup_is_kept_and_older_downloads_are_removed()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "v1.10.0"));
        string? signedPath = null;
        var service = Service(directory, new FeedHandler(ReleaseJson()), verify: path => (signedPath = path) is not null);
        await service.CheckAsync(CancellationToken.None);

        string? setup = await service.DownloadAsync(CancellationToken.None);

        Assert.AreEqual(Path.Combine(directory.Path, "v1.11.0", "WinWispr-Setup-v1.11.0.exe"), setup);
        CollectionAssert.AreEqual(SetupBytes, await File.ReadAllBytesAsync(setup!));
        Assert.AreEqual(setup, signedPath);
        Assert.AreEqual(UpdateState.Ready, service.State);
        Assert.IsFalse(Directory.Exists(Path.Combine(directory.Path, "v1.10.0")));
    }

    [TestMethod]
    public async Task A_setup_that_fails_a_check_is_deleted_and_reported()
    {
        foreach ((string name, FeedHandler handler, Func<string, bool> verify, string message) in new (string, FeedHandler, Func<string, bool>, string)[]
                 {
                     ("altered bytes", new FeedHandler(ReleaseJson(), setup: Encoding.ASCII.GetBytes("pretend this is WinWispr Setuq")),
                         _ => true, "SHA-256"),
                     ("wrong digest", new FeedHandler(ReleaseJson(digest: new string('a', 64))), _ => true, "digest"),
                     ("wrong sidecar", new FeedHandler(ReleaseJson(), sidecar: $"{SetupHash} *WinWispr-Setup-v1.10.0.exe"),
                         _ => true, "checksum"),
                     ("longer file", new FeedHandler(ReleaseJson(size: SetupBytes.Length - 1)), _ => true, "size"),
                     ("foreign signer", new FeedHandler(ReleaseJson()), _ => false, "not signed")
                 })
        {
            using var directory = new TemporaryDirectory();
            var service = Service(directory, handler, verify: verify);
            await service.CheckAsync(CancellationToken.None);

            Assert.IsNull(await service.DownloadAsync(CancellationToken.None), name);
            Assert.AreEqual(UpdateState.Failed, service.State, name);
            StringAssert.Contains(service.FailureMessage, message, name);
            Assert.IsFalse(Directory.Exists(Path.Combine(directory.Path, "v1.11.0")), name);
            Assert.IsNotNull(service.Available, name);
        }
    }

    [TestMethod]
    public async Task Nothing_is_downloaded_before_a_release_is_known()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FeedHandler(ReleaseJson());
        Assert.IsNull(await Service(directory, handler).DownloadAsync(CancellationToken.None));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    private static UpdateService Service(
        TemporaryDirectory directory,
        FeedHandler handler,
        Version? current = null,
        Func<string, bool>? verify = null,
        bool policyDisabled = false) =>
        new(new HttpClient(handler), current ?? new Version(1, 10, 3), directory.Path,
            verify ?? (_ => true), () => policyDisabled);

    private static string ReleaseJson(string? digest = null, long? size = null) => ReleaseFeedTests.Release(
        size: size ?? SetupBytes.Length,
        digest: "sha256:" + (digest ?? SetupHash));

    private sealed class FeedHandler(
        string? feed,
        HttpStatusCode feedStatus = HttpStatusCode.OK,
        bool throws = false,
        byte[]? setup = null,
        string? sidecar = null) : HttpMessageHandler
    {
        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (throws)
                throw new HttpRequestException("offline");
            string path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response = path switch
            {
                _ when request.RequestUri.Host == "api.github.com" => feed is null
                    ? new HttpResponseMessage(feedStatus)
                    : new HttpResponseMessage(feedStatus) { Content = new StringContent(feed) },
                _ when path.EndsWith(".exe.sha256", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sidecar ?? $"{SetupHash} *WinWispr-Setup-v1.11.0.exe\r\n")
                },
                _ when path.EndsWith(".exe", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(setup ?? SetupBytes)
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
