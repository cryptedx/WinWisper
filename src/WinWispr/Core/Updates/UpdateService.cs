using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinWispr.Core.Updates;

internal enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Failed,
    DisabledByPolicy
}

/// <summary>A release or its downloaded Setup failed a check; the message says which.</summary>
internal sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>
/// Finds the latest WinWispr release on GitHub and downloads its Setup. A Setup is only handed out when its size, its
/// SHA-256 (from the release's sidecar and, when GitHub reports it, the asset digest) and its signer all match.
/// Callers stay on one thread; overlapping calls return the work already running.
/// </summary>
internal sealed class UpdateService
{
    /// <summary>The certificate every WinWispr Setup is signed with; see eng/signing/code-signing.json.</summary>
    internal const string SigningThumbprint = "F8D419ED06D2038754C6BB2E5042BBF6DBE5903C";

    private const int MaxFeedBytes = 1024 * 1024;
    private const int MaxChecksumBytes = 4096;
    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly Version _current;
    private readonly string _updatesRoot;
    private readonly Func<string, bool> _verifySignature;
    private readonly Func<bool> _isDisabledByPolicy;
    private Task<UpdateState>? _check;
    private Task<string?>? _download;

    internal UpdateService(
        HttpClient http,
        Version current,
        string updatesRoot,
        Func<string, bool> verifySignature,
        Func<bool> isDisabledByPolicy)
    {
        _http = http;
        _current = current;
        _updatesRoot = updatesRoot;
        _verifySignature = verifySignature;
        _isDisabledByPolicy = isDisabledByPolicy;
    }

    internal UpdateState State { get; private set; } = UpdateState.Idle;
    internal UpdateRelease? Available { get; private set; }
    internal string? FailureMessage { get; private set; }
    internal double DownloadProgress { get; private set; }
    internal Exception? LastException { get; private set; }
    internal bool IsDisabledByPolicy => _isDisabledByPolicy();
    internal string UpdatesRoot => _updatesRoot;

    internal event Action? Changed;

    internal static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinWispr", AppVersion.Display));
        return client;
    }

    internal Task<UpdateState> CheckAsync(CancellationToken cancellationToken)
    {
        if (_check is { IsCompleted: false })
            return _check;
        if (State is UpdateState.Downloading or UpdateState.Ready)
            return Task.FromResult(State);
        return _check = CheckCoreAsync(cancellationToken);
    }

    private async Task<UpdateState> CheckCoreAsync(CancellationToken cancellationToken)
    {
        if (_isDisabledByPolicy())
            return SetState(UpdateState.DisabledByPolicy);
        SetState(UpdateState.Checking);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CheckTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseFeed.LatestReleaseUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Fail("No published WinWispr release was found.", null);
            if (!response.IsSuccessStatusCode)
                return Fail($"GitHub answered {(int)response.StatusCode}. Try again later.", null);
            string json = await ReadTextAsync(response, MaxFeedBytes, timeout.Token);
            UpdateRelease? release = ReleaseFeed.Parse(json);
            if (release is not null && release.Version > _current)
            {
                Available = release;
                return SetState(UpdateState.Available);
            }
            Available = null;
            return SetState(UpdateState.UpToDate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SetState(UpdateState.Idle);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                              or JsonException or IOException or InvalidDataException)
        {
            return Fail("Could not reach GitHub. Check the internet connection.", exception);
        }
    }

    /// <summary>Downloads and verifies the available Setup; returns its path, or null when it failed.</summary>
    internal Task<string?> DownloadAsync(CancellationToken cancellationToken)
    {
        if (_download is { IsCompleted: false })
            return _download;
        if (Available is not { } release || State is not (UpdateState.Available or UpdateState.Failed or UpdateState.Ready))
            return Task.FromResult<string?>(null);
        return _download = DownloadCoreAsync(release, cancellationToken);
    }

    private async Task<string?> DownloadCoreAsync(UpdateRelease release, CancellationToken cancellationToken)
    {
        DownloadProgress = 0;
        SetState(UpdateState.Downloading);
        string directory = Path.Combine(_updatesRoot, "v" + release.DisplayVersion);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(directory);

            string sidecar;
            using (HttpResponseMessage response = await GetAsync(release.ChecksumUri, timeout.Token))
                sidecar = await ReadTextAsync(response, MaxChecksumBytes, timeout.Token);
            string expected = ReleaseFeed.ParseChecksum(sidecar, release.SetupFileName)
                ?? throw new UpdateVerificationException("The release has no valid SHA-256 checksum.");
            if (release.SetupDigestSha256 is { } digest && !string.Equals(digest, expected, StringComparison.Ordinal))
                throw new UpdateVerificationException("The release checksum does not match GitHub's digest.");

            string partial = Path.Combine(directory, release.SetupFileName + ".partial");
            using (HttpResponseMessage response = await GetAsync(release.SetupUri, timeout.Token))
            {
                if (response.Content.Headers.ContentLength is long length && length != release.SetupBytes)
                    throw new UpdateVerificationException("The downloaded Setup has an unexpected size.");
                await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await CopyAndVerifyAsync(input, partial, release.SetupBytes, expected, timeout.Token);
            }
            string setup = Path.Combine(directory, release.SetupFileName);
            File.Move(partial, setup);
            if (!_verifySignature(setup))
                throw new UpdateVerificationException("The downloaded Setup is not signed by WinWispr.");

            PruneOtherVersions(directory);
            DownloadProgress = 1;
            SetState(UpdateState.Ready);
            return setup;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(directory);
            SetState(UpdateState.Available);
            return null;
        }
        catch (UpdateVerificationException exception)
        {
            TryDelete(directory);
            Fail(exception.Message, exception);
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                              or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(directory);
            Fail("The update could not be downloaded. Check the internet connection and try again.", exception);
            return null;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
        {
            response.Dispose();
            throw new UpdateVerificationException("The download left HTTPS.");
        }
        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Download answered {status}.");
        }
        return response;
    }

    private void DownloadProgressed(double value)
    {
        DownloadProgress = value;
        Changed?.Invoke();
    }

    private async Task CopyAndVerifyAsync(
        Stream input, string destination, long expectedBytes, string expectedSha256, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long total = 0;
        int lastPercent = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > expectedBytes)
                throw new UpdateVerificationException("The downloaded Setup has an unexpected size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            int percent = (int)(total * 100 / expectedBytes);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                DownloadProgressed(percent / 100.0);
            }
        }
        await output.FlushAsync(cancellationToken);
        if (total != expectedBytes)
            throw new UpdateVerificationException("The downloaded Setup has an unexpected size.");
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expectedSha256)))
            throw new UpdateVerificationException("The downloaded Setup does not match its SHA-256 checksum.");
    }

    private static async Task<string> ReadTextAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("The answer is too large.");
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("The answer is too large.");
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private void PruneOtherVersions(string keep)
    {
        foreach (string directory in Directory.EnumerateDirectories(_updatesRoot, "v*"))
            if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                TryDelete(directory);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private UpdateState Fail(string message, Exception? exception)
    {
        FailureMessage = message;
        LastException = exception;
        return SetState(UpdateState.Failed);
    }

    private UpdateState SetState(UpdateState state)
    {
        if (state != UpdateState.Failed)
        {
            FailureMessage = null;
            LastException = null;
        }
        State = state;
        Changed?.Invoke();
        return state;
    }
}
