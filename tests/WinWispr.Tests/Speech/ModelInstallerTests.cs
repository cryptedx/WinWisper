using System.Net;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Speech;

namespace WinWispr.Tests.Speech;

[TestClass]
public sealed class ModelInstallerTests
{
    private static readonly byte[] Payload = "test"u8.ToArray();
    private static readonly string PayloadHash = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
    private static readonly ModelSpec TestSpec = new(
        Uri: new Uri("https://example.test/model.tar.bz2"),
        ArchiveBytes: Payload.Length,
        ArchiveSha256: PayloadHash,
        Files:
        [
            new("test-model/encoder.int8.onnx", "encoder.int8.onnx", 1),
            new("test-model/decoder.int8.onnx", "decoder.int8.onnx", 1),
            new("test-model/joiner.int8.onnx", "joiner.int8.onnx", 1),
            new("test-model/tokens.txt", "tokens.txt", 1)
        ]);

    [TestMethod]
    public void Default_http_handler_does_not_follow_redirects_implicitly()
    {
        using HttpClientHandler handler = ModelInstaller.CreateHttpHandler();

        Assert.IsFalse(handler.AllowAutoRedirect);
    }

    [TestMethod]
    public void Parakeet_manifest_is_the_pinned_model()
    {
        Assert.AreEqual(487_170_055L, ModelManifest.Parakeet.ArchiveBytes);
        Assert.AreEqual(
            "5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf",
            ModelManifest.Parakeet.ArchiveSha256);
        CollectionAssert.AreEquivalent(
            new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" },
            ModelManifest.Parakeet.Files.Select(file => file.InstalledName).ToArray());
    }

    [TestMethod]
    public async Task Bundled_install_uses_local_archive_without_http()
    {
        using var root = new TemporaryDirectory();
        string bundledArchive = System.IO.Path.Combine(root.Path, "ModelData", "model.tar.bz2");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(bundledArchive)!);
        await File.WriteAllBytesAsync(bundledArchive, Payload);
        using var client = new HttpClient(new ThrowingHttpHandler());
        var installer = CreateInstaller(root.Path, client);

        ModelInstallStatus result = await installer.InstallBundledAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.Installed, result);
        Assert.IsNotNull(installer.GetActiveModelDirectory());
        AssertStagingIsEmpty(root.Path);
    }

    [TestMethod]
    public async Task Bundled_install_failure_preserves_existing_activation()
    {
        using var root = new TemporaryDirectory();
        await root.WriteTextAsync("Models/active.json", "{\"installId\":\"known-good\",\"archiveSha256\":\"trusted\"}");
        string bundledArchive = System.IO.Path.Combine(root.Path, "ModelData", "model.tar.bz2");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(bundledArchive)!);
        await File.WriteAllBytesAsync(bundledArchive, "bad!"u8.ToArray());
        using var client = new HttpClient(new ThrowingHttpHandler());
        var installer = CreateInstaller(root.Path, client);

        ModelInstallStatus result = await installer.InstallBundledAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.IntegrityFailed, result);
        Assert.AreEqual(
            "{\"installId\":\"known-good\",\"archiveSha256\":\"trusted\"}",
            await root.ReadTextAsync("Models/active.json"));
        AssertStagingIsEmpty(root.Path);
    }

    [TestMethod]
    public async Task Wrong_archive_hash_leaves_existing_activation_unchanged()
    {
        using var root = new TemporaryDirectory();
        await root.WriteTextAsync("Models/active.json", "{\"installId\":\"known-good\",\"archiveSha256\":\"trusted\"}");
        using var client = new HttpClient(new BytesHttpHandler("bad!"u8.ToArray()));
        var installer = CreateInstaller(root.Path, client);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.IntegrityFailed, result);
        Assert.AreEqual(
            "{\"installId\":\"known-good\",\"archiveSha256\":\"trusted\"}",
            await root.ReadTextAsync("Models/active.json"));
        AssertStagingIsEmpty(root.Path);
    }

    [TestMethod]
    public async Task Cancellation_removes_staging_and_preserves_active_model()
    {
        using var root = new TemporaryDirectory();
        await root.WriteTextAsync("Models/active.json", "{\"installId\":\"installed\",\"archiveSha256\":\"trusted\"}");
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new CancellingHttpHandler(cancellation));
        var installer = CreateInstaller(root.Path, client);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, cancellation.Token);

        Assert.AreEqual(ModelInstallStatus.Cancelled, result);
        Assert.AreEqual(
            "{\"installId\":\"installed\",\"archiveSha256\":\"trusted\"}",
            await root.ReadTextAsync("Models/active.json"));
        AssertStagingIsEmpty(root.Path);
    }

    [TestMethod]
    public async Task Cancellation_during_extraction_preserves_active_model_and_cleans_staging()
    {
        using var root = new TemporaryDirectory();
        await root.WriteTextAsync("Models/active.json", "{\"installId\":\"installed\",\"archiveSha256\":\"trusted\"}");
        string oldInstall = System.IO.Path.Combine(root.Path, "Models", "installs", "installed");
        Directory.CreateDirectory(oldInstall);
        await File.WriteAllTextAsync(System.IO.Path.Combine(oldInstall, "tokens.txt"), "keep");
        using var client = new HttpClient(new BytesHttpHandler(Payload));
        using var cancellation = new CancellationTokenSource();
        var installer = CreateInstaller(
            root.Path,
            client,
            (_, _, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            });

        ModelInstallStatus result = await installer.InstallAsync(progress: null, cancellation.Token);

        Assert.AreEqual(ModelInstallStatus.Cancelled, result);
        Assert.AreEqual(
            "{\"installId\":\"installed\",\"archiveSha256\":\"trusted\"}",
            await root.ReadTextAsync("Models/active.json"));
        Assert.IsTrue(Directory.Exists(oldInstall));
        AssertStagingIsEmpty(root.Path);
    }

    [TestMethod]
    public async Task Successful_install_activates_expected_files_and_removes_old_install()
    {
        using var root = new TemporaryDirectory();
        await root.WriteTextAsync("Models/active.json", "{\"installId\":\"old-install\",\"archiveSha256\":\"old-hash\"}");
        string oldInstall = System.IO.Path.Combine(root.Path, "Models", "installs", "old-install");
        Directory.CreateDirectory(oldInstall);
        await File.WriteAllTextAsync(System.IO.Path.Combine(oldInstall, "old.txt"), "old");
        using var client = new HttpClient(new BytesHttpHandler(Payload));
        var installer = CreateInstaller(root.Path, client);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.Installed, result);
        Assert.IsFalse(Directory.Exists(oldInstall));
        string installPath = installer.GetActiveModelDirectory()!;
        foreach (ModelFileSpec file in TestSpec.Files)
            Assert.IsTrue(File.Exists(System.IO.Path.Combine(installPath, file.InstalledName)));
        Assert.AreEqual(installPath, installer.GetActiveModelDirectory());
        Assert.IsTrue((await root.ReadTextAsync("Models/active.json")).Contains(
            System.IO.Path.GetFileName(installPath),
            StringComparison.Ordinal));
        AssertStagingIsEmpty(root.Path);
    }

    [TestMethod]
    public async Task Successful_install_reports_bounded_progress_and_updates_active_directory()
    {
        using var root = new TemporaryDirectory();
        using var client = new HttpClient(new BytesHttpHandler(Payload));
        var installer = CreateInstaller(root.Path, client);
        var updates = new List<double>();

        ModelInstallStatus result = await installer.InstallAsync(new CollectingProgress(updates), CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.Installed, result);
        Assert.AreEqual(0d, updates[0]);
        Assert.AreEqual(1d, updates[^1]);
        Assert.IsTrue(updates.All(value => value >= 0d && value <= 1d));
        string? installPath = installer.GetActiveModelDirectory();
        Assert.IsNotNull(installPath);
        Assert.IsTrue(Directory.Exists(installPath));
        StringAssert.Contains(
            await root.ReadTextAsync("Models/active.json"),
            System.IO.Path.GetFileName(installPath));
    }

    [TestMethod]
    public async Task Transient_server_failure_is_retried_before_installing()
    {
        using var root = new TemporaryDirectory();
        int attempts = 0;
        using var client = new HttpClient(new ResponseHandler((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) < 3)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Payload)
            };
        }));
        var installer = CreateInstaller(
            root.Path,
            client,
            delay: static (_, _) => Task.CompletedTask);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.Installed, result);
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    public async Task Invalid_active_install_id_never_deletes_a_traversed_path()
    {
        using var root = new TemporaryDirectory();
        string outside = System.IO.Path.Combine(root.Path, "outside");
        Directory.CreateDirectory(outside);
        string marker = System.IO.Path.Combine(outside, "marker.txt");
        await File.WriteAllTextAsync(marker, "keep");
        await root.WriteTextAsync("Models/active.json", "{\"installId\":\"..\\\\outside\",\"archiveSha256\":\"old-hash\"}");
        using var client = new HttpClient(new BytesHttpHandler(Payload));
        var installer = CreateInstaller(root.Path, client);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.Installed, result);
        Assert.IsTrue(File.Exists(marker));
    }

    [TestMethod]
    public async Task Http_redirect_to_http_is_rejected_before_download()
    {
        using var root = new TemporaryDirectory();
        using var client = new HttpClient(new ResponseHandler((_, _) => Redirect(HttpStatusCode.Found, new Uri("http://example.test/model"))));
        var installer = CreateInstaller(root.Path, client);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.IntegrityFailed, result);
    }

    [TestMethod]
    public async Task Wrong_content_length_is_rejected()
    {
        using var root = new TemporaryDirectory();
        using var client = new HttpClient(new BytesHttpHandler(Payload, declaredLength: Payload.Length + 1));
        var installer = CreateInstaller(root.Path, client);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.IntegrityFailed, result);
    }

    [TestMethod]
    public async Task Excessive_redirects_are_rejected()
    {
        using var root = new TemporaryDirectory();
        using var client = new HttpClient(new ResponseHandler((_, request) =>
            Redirect(HttpStatusCode.Found, new Uri(request.RequestUri!, request.RequestUri!.AbsolutePath + "-next"))));
        var installer = CreateInstaller(root.Path, client);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.IntegrityFailed, result);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(5)]
    public async Task Wrong_body_size_is_rejected(int bodyLength)
    {
        using var root = new TemporaryDirectory();
        byte[] body = Enumerable.Range(0, bodyLength).Select(index => (byte)index).ToArray();
        using var client = new HttpClient(new BytesHttpHandler(body));
        var installer = CreateInstaller(root.Path, client);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.IntegrityFailed, result);
    }

    [TestMethod]
    public async Task Extraction_failure_keeps_existing_activation()
    {
        using var root = new TemporaryDirectory();
        await root.WriteTextAsync("Models/active.json", "{\"installId\":\"known-good\",\"archiveSha256\":\"trusted\"}");
        using var client = new HttpClient(new BytesHttpHandler(Payload));
        var installer = CreateInstaller(
            root.Path,
            client,
            static (_, _, _) => throw new InvalidDataException("tar failed"));

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.ExtractionFailed, result);
        Assert.AreEqual(
            "{\"installId\":\"known-good\",\"archiveSha256\":\"trusted\"}",
            await root.ReadTextAsync("Models/active.json"));
        AssertStagingIsEmpty(root.Path);
    }

    [TestMethod]
    public async Task Missing_expected_member_is_rejected()
    {
        using var root = new TemporaryDirectory();
        using var client = new HttpClient(new BytesHttpHandler(Payload));
        var installer = CreateInstaller(root.Path, client, ExtractMissingFileAsync);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.ExtractionFailed, result);
    }

    [TestMethod]
    public async Task Wrong_extracted_size_is_rejected()
    {
        using var root = new TemporaryDirectory();
        using var client = new HttpClient(new BytesHttpHandler(Payload));
        var installer = CreateInstaller(root.Path, client, ExtractWrongSizeAsync);

        ModelInstallStatus result = await installer.InstallAsync(progress: null, CancellationToken.None);

        Assert.AreEqual(ModelInstallStatus.ExtractionFailed, result);
    }

    private static ModelInstaller CreateInstaller(
        string rootPath,
        HttpClient client,
        Func<string, string, CancellationToken, Task>? extractor = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            rootPath,
            client,
            extractor ?? ExtractExpectedFilesAsync,
            TestSpec,
            delay,
            System.IO.Path.Combine(rootPath, "ModelData", "model.tar.bz2"));

    private static async Task ExtractExpectedFilesAsync(string _, string stagingPath, CancellationToken cancellationToken)
    {
        string modelRoot = System.IO.Path.Combine(stagingPath, "test-model");
        Directory.CreateDirectory(modelRoot);
        foreach (ModelFileSpec file in TestSpec.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(
                System.IO.Path.Combine(modelRoot, file.InstalledName),
                new byte[file.ExpectedBytes],
                cancellationToken);
        }
    }

    private static Task ExtractMissingFileAsync(string archivePath, string stagingPath, CancellationToken cancellationToken) =>
        WriteFilesAsync(stagingPath, cancellationToken, skipLast: true, wrongSize: false);

    private static Task ExtractWrongSizeAsync(string archivePath, string stagingPath, CancellationToken cancellationToken) =>
        WriteFilesAsync(stagingPath, cancellationToken, skipLast: false, wrongSize: true);

    private static async Task WriteFilesAsync(
        string stagingPath,
        CancellationToken cancellationToken,
        bool skipLast,
        bool wrongSize)
    {
        string modelRoot = System.IO.Path.Combine(stagingPath, "test-model");
        Directory.CreateDirectory(modelRoot);
        for (int index = 0; index < TestSpec.Files.Count - (skipLast ? 1 : 0); index++)
        {
            ModelFileSpec file = TestSpec.Files[index];
            cancellationToken.ThrowIfCancellationRequested();
            int length = checked((int)file.ExpectedBytes + (wrongSize && index == 0 ? 1 : 0));
            await File.WriteAllBytesAsync(
                System.IO.Path.Combine(modelRoot, file.InstalledName),
                new byte[length],
                cancellationToken);
        }
    }

    private static void AssertStagingIsEmpty(string rootPath)
    {
        string stagingRoot = System.IO.Path.Combine(rootPath, "Models", "staging");
        Assert.IsTrue(!Directory.Exists(stagingRoot) || Directory.GetFileSystemEntries(stagingRoot).Length == 0);
    }

    private sealed class CollectingProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }

    private static HttpResponseMessage Redirect(HttpStatusCode statusCode, Uri location)
    {
        var response = new HttpResponseMessage(statusCode);
        response.Headers.Location = location;
        return response;
    }

    private sealed class BytesHttpHandler(byte[] bytes, long? declaredLength = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentLength = declaredLength ?? bytes.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class CancellingHttpHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Bundled model installation must not use HTTP.");
    }

    private sealed class ResponseHandler(Func<int, HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(Interlocked.Increment(ref _requestCount), request));
    }
}
