using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinWispr.Core;

namespace WinWispr.Speech;

internal enum ModelInstallStatus
{
    Installed,
    IntegrityFailed,
    Cancelled,
    ExtractionFailed,
    Failed
}

/// <summary>
/// Installs and locates the speech model. It lives in its own folder of the app data (its <c>SetDirectory</c>)
/// with its own active pointer, installations and staging area. It comes from a single pinned archive, bundled or
/// downloaded, that is staged, verified and then activated as one unit.
/// </summary>
internal sealed class ModelInstaller
{
    private const int MaxRedirects = 5;
    private const int MaxDownloadAttempts = 3;
    private const int BufferSize = 64 * 1024;

    private readonly string _modelsRoot;
    private readonly string _bundledArchivePath;
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<InstalledFile> _installedFiles;
    private readonly string _stagedRootName;
    private readonly Func<string, IProgress<double>?, CancellationToken, Task> _populateFromNetwork;
    private readonly Func<string, IProgress<double>?, CancellationToken, Task> _populateFromBundle;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal ModelInstaller(
        string applicationRoot,
        HttpClient httpClient,
        Func<string, string, CancellationToken, Task>? extractor = null,
        ModelSpec? model = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? bundledArchivePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        ArgumentNullException.ThrowIfNull(httpClient);

        ModelSpec archive = model ?? ModelManifest.Parakeet;
        ValidateModel(archive);
        _modelsRoot = Path.Combine(Path.GetFullPath(applicationRoot), archive.SetDirectory);
        _bundledArchivePath = bundledArchivePath ?? Path.Combine(
            AppContext.BaseDirectory,
            "ModelData",
            "model.tar.bz2");
        _httpClient = httpClient;
        _installedFiles = archive.Files.Select(file => new InstalledFile(file.InstalledName, file.ExpectedBytes)).ToList();
        _stagedRootName = GetArchiveRoot(archive);
        _delay = delay ?? Task.Delay;

        Func<string, string, CancellationToken, Task> extract = extractor ?? ((archivePath, stagingPath, cancellationToken) =>
            ExtractWithTarAsync(archivePath, stagingPath, archive, cancellationToken));
        _populateFromNetwork = (stagingPath, progress, cancellationToken) => StageAndExtractAsync(
            stagingPath,
            (archivePath, archiveProgress, token) => DownloadArchiveAsync(archive, archivePath, archiveProgress, token),
            extract,
            progress,
            cancellationToken);
        _populateFromBundle = (stagingPath, progress, cancellationToken) => StageAndExtractAsync(
            stagingPath,
            (archivePath, archiveProgress, token) => CopyBundledArchiveAsync(archive, archivePath, archiveProgress, token),
            extract,
            progress,
            cancellationToken);
    }

    internal static ModelInstaller CreateDefault() =>
        new(
            AppDataPaths.Root,
            new HttpClient(CreateHttpHandler()),
            bundledArchivePath: Path.Combine(AppContext.BaseDirectory, "ModelData", "model.tar.bz2"));

    internal static HttpClientHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false
    };

    internal bool HasBundledModelArchive() => File.Exists(_bundledArchivePath);

    internal string? GetActiveModelDirectory()
    {
        string? installId = ReadActiveInstallId();
        if (installId is null)
            return null;

        string path = Path.Combine(_modelsRoot, "installs", installId);
        return Directory.Exists(path) ? path : null;
    }

    internal Task<ModelInstallStatus> InstallAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        InstallCoreAsync(_populateFromNetwork, progress, cancellationToken);

    internal async Task<ModelInstallStatus> InstallBundledAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!HasBundledModelArchive())
            return ModelInstallStatus.Failed;

        return await InstallCoreAsync(_populateFromBundle, progress, cancellationToken);
    }

    /// <summary>
    /// Stages the set's files with <paramref name="populate"/>, checks them against the manifest, and only then
    /// activates them as one unit. Any failure or cancellation leaves the previous activation untouched and removes
    /// what this run staged.
    /// </summary>
    private async Task<ModelInstallStatus> InstallCoreAsync(
        Func<string, IProgress<double>?, CancellationToken, Task> populate,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string? stagingPath = null;
        string? installPath = null;
        string? activeTempPath = null;
        bool activated = false;

        try
        {
            string? previousInstallId = ReadActiveInstallId();
            string installId = Guid.NewGuid().ToString("N");
            stagingPath = Path.Combine(_modelsRoot, "staging", installId);
            Directory.CreateDirectory(stagingPath);

            await populate(stagingPath, progress, cancellationToken);

            string stagedRoot = ValidateStagedModel(stagingPath);
            string installsRoot = Path.Combine(_modelsRoot, "installs");
            Directory.CreateDirectory(installsRoot);
            installPath = Path.Combine(installsRoot, installId);
            Directory.Move(stagedRoot, installPath);

            activeTempPath = Path.Combine(_modelsRoot, $"active.{installId}.tmp");
            string activePath = Path.Combine(_modelsRoot, "active.json");
            string pointer = JsonSerializer.Serialize(new ActiveModelPointer(installId));
            await File.WriteAllTextAsync(activeTempPath, pointer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceActivePointer(activeTempPath, activePath);
            activeTempPath = null;
            activated = true;

            if (previousInstallId is not null && !string.Equals(previousInstallId, installId, StringComparison.Ordinal))
                TryDeleteDirectory(Path.Combine(installsRoot, previousInstallId));

            return ModelInstallStatus.Installed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ModelInstallStatus.Cancelled;
        }
        catch (IntegrityException)
        {
            return ModelInstallStatus.IntegrityFailed;
        }
        catch (ExtractionException)
        {
            return ModelInstallStatus.ExtractionFailed;
        }
        catch
        {
            return ModelInstallStatus.Failed;
        }
        finally
        {
            if (!activated && installPath is not null)
                TryDeleteDirectory(installPath);
            if (activeTempPath is not null)
                TryDeleteFile(activeTempPath);
            if (stagingPath is not null)
                TryDeleteDirectory(stagingPath);
        }
    }

    /// <summary>The archive path: stage the archive next to the extracted files, then unpack it.</summary>
    private static async Task StageAndExtractAsync(
        string stagingPath,
        Func<string, IProgress<double>?, CancellationToken, Task> stageArchive,
        Func<string, string, CancellationToken, Task> extract,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string archivePath = Path.Combine(stagingPath, "model.tar.bz2");
        await stageArchive(archivePath, progress, cancellationToken);

        try
        {
            await extract(archivePath, stagingPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExtractionException(exception);
        }
    }

    private async Task DownloadArchiveAsync(
        ModelSpec archive,
        string archivePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await DownloadVerifiedAsync(
            archive.Uri,
            archivePath,
            archive.ArchiveBytes,
            archive.ArchiveSha256,
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Downloads one pinned file over HTTPS, following at most five HTTPS redirects, and keeps it only when it has
    /// exactly the expected size and SHA-256.
    /// </summary>
    private async Task DownloadVerifiedAsync(
        Uri source,
        string destinationPath,
        long expectedBytes,
        string expectedSha256,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Uri current = ValidateUri(source);
        int redirects = 0;

        while (true)
        {
            using HttpResponseMessage response = await SendAsyncWithRetryAsync(current, cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                if (redirects++ >= MaxRedirects || response.Headers.Location is null)
                    throw new IntegrityException();
                current = ValidateUri(new Uri(current, response.Headers.Location));
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new IntegrityException();
            if (response.Content.Headers.ContentLength is long contentLength
                && contentLength != expectedBytes)
            {
                throw new IntegrityException();
            }

            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await CopyAndVerifyAsync(input, destinationPath, expectedBytes, expectedSha256, progress, cancellationToken);
            return;
        }
    }

    private async Task CopyBundledArchiveAsync(
        ModelSpec archive,
        string archivePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var bundledArchive = new FileInfo(_bundledArchivePath);
        if (!bundledArchive.Exists || bundledArchive.Length != archive.ArchiveBytes)
            throw new IntegrityException();

        await using Stream input = new FileStream(
            bundledArchive.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await CopyAndVerifyAsync(input, archivePath, archive.ArchiveBytes, archive.ArchiveSha256, progress, cancellationToken);
    }

    private static async Task CopyAndVerifyAsync(
        Stream input,
        string destinationPath,
        long expectedBytes,
        string expectedSha256,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long total = 0;

        progress?.Report(0);
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > expectedBytes)
                throw new IntegrityException();
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report((double)total / expectedBytes);
        }

        await output.FlushAsync(cancellationToken);
        if (total != expectedBytes
            || !CryptographicOperations.FixedTimeEquals(
                hash.GetHashAndReset(),
                Convert.FromHexString(expectedSha256)))
        {
            throw new IntegrityException();
        }

        progress?.Report(1);
    }

    private async Task<HttpResponseMessage> SendAsyncWithRetryAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!IsTransient(response.StatusCode) || attempt == MaxDownloadAttempts)
                    return response;

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < MaxDownloadAttempts)
            {
            }

            await _delay(TimeSpan.FromSeconds(1 << (attempt - 1)), cancellationToken);
        }

        throw new InvalidOperationException("Model download retry loop did not complete.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    /// <summary>
    /// Checks that the staged folder holds exactly the manifest's files at their pinned sizes, no more, no
    /// fewer and nothing that is a link, and returns the folder that becomes the installation.
    /// </summary>
    private string ValidateStagedModel(string stagingPath)
    {
        string stagedRoot = Path.Combine(stagingPath, _stagedRootName);
        if (!Directory.Exists(stagedRoot) || HasReparsePoint(stagedRoot))
            throw new ExtractionException();

        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledFile file in _installedFiles)
        {
            expectedFiles.Add(file.Name);
            string[] segments = file.Name.Split('/');
            for (int length = 1; length < segments.Length; length++)
                expectedDirectories.Add(string.Join('/', segments[..length]));

            string path = Path.Combine(stagedRoot, ToNativePath(file.Name));
            if (!File.Exists(path) || HasReparsePoint(path) || new FileInfo(path).Length != file.Bytes)
                throw new ExtractionException();
        }

        ValidateOnlyExpectedEntries(stagedRoot, relativePrefix: string.Empty, expectedFiles, expectedDirectories);
        return stagedRoot;
    }

    private static void ValidateOnlyExpectedEntries(
        string directory,
        string relativePrefix,
        HashSet<string> expectedFiles,
        HashSet<string> expectedDirectories)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(entry);
            string relative = relativePrefix.Length == 0 ? name : $"{relativePrefix}/{name}";
            if (HasReparsePoint(entry))
                throw new ExtractionException();

            if (Directory.Exists(entry))
            {
                if (!expectedDirectories.Contains(relative))
                    throw new ExtractionException();
                ValidateOnlyExpectedEntries(entry, relative, expectedFiles, expectedDirectories);
            }
            else if (!expectedFiles.Contains(relative))
            {
                throw new ExtractionException();
            }
        }
    }

    private string? ReadActiveInstallId()
    {
        string path = Path.Combine(_modelsRoot, "active.json");
        try
        {
            ActiveModelPointer? pointer = JsonSerializer.Deserialize<ActiveModelPointer>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            string? installId = pointer?.InstallId;
            return IsSafeInstallId(installId) ? installId : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ReplaceActivePointer(string temporaryPath, string activePath)
    {
        if (File.Exists(activePath))
            File.Replace(temporaryPath, activePath, destinationBackupFileName: null);
        else
            File.Move(temporaryPath, activePath);
    }

    private static async Task ExtractWithTarAsync(
        string archivePath,
        string stagingPath,
        ModelSpec model,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("tar.exe")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-xjf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(stagingPath);
        foreach (ModelFileSpec file in model.Files)
            startInfo.ArgumentList.Add(file.ArchivePath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidDataException();

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            throw;
        }

        await Task.WhenAll(standardOutput, standardError);
        if (process.ExitCode != 0)
            throw new InvalidDataException();
    }

    private static Uri ValidateUri(Uri uri)
    {
        if (!IsPlainHttps(uri))
            throw new IntegrityException();

        return uri;
    }

    private static bool IsPlainHttps(Uri uri) =>
        uri.IsAbsoluteUri
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    /// <summary>A file cannot also be the folder of another file.</summary>
    private static void ValidateNoFileIsAFolder(HashSet<string> names, string parameterName)
    {
        foreach (string name in names)
        {
            string[] segments = name.Split('/');
            for (int length = 1; length < segments.Length; length++)
            {
                if (names.Contains(string.Join('/', segments[..length])))
                    throw new ArgumentException("Invalid model manifest file.", parameterName);
            }
        }
    }

    private static bool IsSha256(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);

    private static void ValidateModel(ModelSpec model)
    {
        if (model.Files.Count == 0
            || model.ArchiveBytes <= 0
            || !IsSha256(model.ArchiveSha256))
        {
            throw new ArgumentException("Invalid model manifest.", nameof(model));
        }

        if (!IsSafeNameSegment(model.SetDirectory))
            throw new ArgumentException("Invalid model set directory.", nameof(model));

        string root = GetArchiveRoot(model);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelFileSpec file in model.Files)
        {
            string[] segments = file.ArchivePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2
                || !string.Equals(segments[0], root, StringComparison.Ordinal)
                || !string.Equals(file.ArchivePath, $"{root}/{file.InstalledName}", StringComparison.Ordinal)
                || file.ArchivePath.Contains('\\')
                || !IsSafeRelativePath(file.InstalledName)
                || file.ExpectedBytes < 0
                || !names.Add(file.InstalledName))
            {
                throw new ArgumentException("Invalid model manifest file.", nameof(model));
            }
        }

        ValidateNoFileIsAFolder(names, nameof(model));
    }

    /// <summary>A path below the installation directory: '/'-separated, every segment a plain file name.</summary>
    private static bool IsSafeRelativePath(string path) =>
        path.Length > 0 && path.Split('/').All(IsSafeNameSegment);

    private static bool IsSafeNameSegment(string segment) =>
        !string.IsNullOrWhiteSpace(segment)
        && segment is not ("." or "..")
        && segment.IndexOfAny(UnsafeNameCharacters) < 0
        && segment.All(character => character >= ' ')
        && !segment.EndsWith('.')
        && !segment.EndsWith(' ');

    private static readonly char[] UnsafeNameCharacters = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static string ToNativePath(string installedName) =>
        installedName.Replace('/', Path.DirectorySeparatorChar);

    private static string GetArchiveRoot(ModelSpec model)
    {
        string? archivePath = model.Files.FirstOrDefault()?.ArchivePath;
        int separator = archivePath?.IndexOf('/') ?? -1;
        string? root = separator > 0 ? archivePath![..separator] : null;
        if (string.IsNullOrWhiteSpace(root) || root is "." or "..")
            throw new ArgumentException("Invalid model archive root.", nameof(model));
        return root;
    }

    private static bool IsSafeInstallId(string? installId) =>
        !string.IsNullOrWhiteSpace(installId)
        && installId.Length <= 64
        && installId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record ActiveModelPointer(string InstallId);

    /// <summary>A file of the installed set: its path below the installation directory and its pinned size.</summary>
    private sealed record InstalledFile(string Name, long Bytes);

    private sealed class IntegrityException : Exception
    {
    }

    private sealed class ExtractionException : Exception
    {
        internal ExtractionException()
        {
        }

        internal ExtractionException(Exception innerException)
            : base(null, innerException)
        {
        }
    }
}
