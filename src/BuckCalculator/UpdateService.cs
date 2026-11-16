using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BuckCalculator;

public sealed record AppUpdate(Version Version, string VersionText, Uri InstallerUrl, string Sha256, long Size, string ReleaseNotes, Uri ReleaseUrl);

public sealed class UpdateService
{
    private const string Repository = "Epiphany-Lucky/buck-calculator";
    private const long MaximumInstallerBytes = 1024L * 1024 * 1024;
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private static readonly HttpClient DefaultClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly string DefaultUpdateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BuckCalculator", "Updates");
    private readonly HttpClient httpClient;
    private readonly string updateDirectory;
    private readonly Version currentVersion;

    public static Version CurrentVersion { get; } = NormalizeVersion(Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    public UpdateService() : this(DefaultClient) { }

    public UpdateService(HttpClient httpClient, string? updateDirectory = null, Version? currentVersion = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.updateDirectory = Path.GetFullPath(updateDirectory ?? DefaultUpdateDirectory);
        this.currentVersion = NormalizeVersion(currentVersion ?? CurrentVersion);
    }

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            using var manifestResponse = await SendAsync(new Uri($"https://github.com/{Repository}/releases/latest/download/update.json"), timeout.Token);
            if (manifestResponse.StatusCode != HttpStatusCode.NotFound)
            {
                manifestResponse.EnsureSuccessStatusCode();
                byte[] manifestBytes = await ReadLimitedAsync(manifestResponse.Content, MaximumMetadataBytes, timeout.Token);
                using var manifestDocument = JsonDocument.Parse(manifestBytes);
                AppUpdate manifestUpdate = ParseManifest(manifestDocument.RootElement);
                return manifestUpdate.Version > currentVersion ? manifestUpdate : null;
            }

            using var response = await SendAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            byte[] metadata = await ReadLimitedAsync(response.Content, MaximumMetadataBytes, timeout.Token);
            using var document = JsonDocument.Parse(metadata);
            var release = document.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                return null;

            string tag = RequiredString(release, "tag_name");
            if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) || !Version.TryParse(tag[1..], out var version))
                throw new InvalidDataException("GitHub 版本标签无效，预期格式为 vX.Y.Z。");
            version = NormalizeVersion(version);
            string versionText = version.ToString(3);
            if (tag != "v" + versionText)
                throw new InvalidDataException("GitHub 版本标签必须使用规范的 vX.Y.Z 格式。");
            var releaseUrl = new Uri($"https://github.com/{Repository}/releases/tag/{tag}");
            if (!Uri.TryCreate(RequiredString(release, "html_url"), UriKind.Absolute, out var actualReleaseUrl) || actualReleaseUrl != releaseUrl)
                throw new InvalidDataException("更新发布页不属于预期的 GitHub 仓库。");
            if (version <= currentVersion)
                return null;

            string installerName = InstallerName(versionText);
            var assets = release.GetProperty("assets");
            if (assets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GitHub 附件列表无效。");
            var installers = assets.EnumerateArray().Where(asset => RequiredString(asset, "name") == installerName).ToArray();
            if (installers.Length == 0)
                return null;
            if (installers.Length != 1)
                throw new InvalidDataException("发布包含重复的 MSI 附件。");
            var installer = installers[0];
            long size = installer.GetProperty("size").GetInt64();
            ValidateSize(size);
            Uri installerUrl = AssetUrl(installer, tag, installerName);
            string? hash = null;
            if (installer.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null)
            {
                string digestText = digest.GetString() ?? "";
                if (!digestText.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("MSI 附件未提供可识别的 SHA-256 摘要。");
                hash = ValidateHash(digestText[7..]);
            }
            if (hash is null)
            {
                var checksumAssets = assets.EnumerateArray().Where(asset => RequiredString(asset, "name") == "SHA256SUMS.txt").ToArray();
                if (checksumAssets.Length != 1)
                    throw new InvalidDataException("更新缺少唯一的 SHA256SUMS.txt 校验附件。");
                Uri checksumUrl = AssetUrl(checksumAssets[0], tag, "SHA256SUMS.txt");
                using var checksumResponse = await SendAsync(checksumUrl, timeout.Token);
                checksumResponse.EnsureSuccessStatusCode();
                byte[] checksumBytes = await ReadLimitedAsync(checksumResponse.Content, 1024 * 1024, timeout.Token);
                hash = ReadChecksum(Encoding.UTF8.GetString(checksumBytes), installerName);
            }
            string notes = release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
            return new AppUpdate(version, versionText, installerUrl, hash, size, notes, releaseUrl);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("检查更新超时，请稍后重试。", exception);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("GitHub 更新元数据格式无效。", exception);
        }
    }

    public async Task<string> DownloadAsync(AppUpdate update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateSize(update.Size);
        string expectedHash = ValidateHash(update.Sha256);
        if (!Version.TryParse(update.VersionText, out var version) || update.VersionText != NormalizeVersion(version).ToString(3) || NormalizeVersion(update.Version) != NormalizeVersion(version))
            throw new InvalidDataException("更新版本与 MSI 元数据不一致。");
        string name = InstallerName(update.VersionText);
        ValidateAssetUrl(update.InstallerUrl, "v" + update.VersionText, name);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(updateDirectory);
        string finalPath = Path.Combine(updateDirectory, name);
        string partialPath = Path.Combine(updateDirectory, $".{name}.{Guid.NewGuid():N}.partial");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using var response = await SendAsync(update.InstallerUrl, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declaredLength && declaredLength != update.Size)
                throw new InvalidDataException("MSI 下载长度与发布元数据不一致。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            progress?.Report(0);
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    received += count;
                    if (received > update.Size || received > MaximumInstallerBytes)
                        throw new InvalidDataException("MSI 实际下载大小超过预期。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    progress?.Report((double)received / update.Size);
                }
                await output.FlushAsync(timeout.Token);
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (received != update.Size)
                throw new InvalidDataException("MSI 下载不完整，请重新下载。");
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expectedHash)))
                throw new InvalidDataException("MSI 的 SHA-256 校验失败，下载文件已丢弃。");
            File.Move(partialPath, finalPath, true);
            progress?.Report(1);
            return finalPath;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("下载安装包超时，请重新尝试。", exception);
        }
        finally
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }
    }

    public static void LaunchInstaller(string installerPath, string expectedSha256)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("MSI 更新需要 Windows。");
        string source = ValidateInstallerPath(installerPath);
        string expectedHash = ValidateHash(expectedSha256);
        string originalExe = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前应用程序。");
        if (string.Equals(Path.GetFileNameWithoutExtension(originalExe), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前程序由 dotnet 开发主机运行。请使用已发布的自包含 EXE 执行安装更新。");
        string helperDirectory = Path.Combine(DefaultUpdateDirectory, "helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);
        string helperExe = Path.Combine(helperDirectory, "BuckCalculator.UpdateHelper.exe");
        string helperMsi = Path.Combine(helperDirectory, Path.GetFileName(source));
        using (var input = OpenVerifiedInstaller(source, expectedHash))
        using (var output = new FileStream(helperMsi, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            input.CopyTo(output);
        File.Copy(originalExe, helperExe, false);
        var start = new ProcessStartInfo(helperExe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "--apply-update", helperMsi, expectedHash, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), originalExe })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新助手。");
    }

    public static int RunInstaller(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("MSI 更新需要 Windows。");
        if (args.Length != 5 || args[0] != "--apply-update" || !int.TryParse(args[3], out int parentPid) || parentPid <= 0 || parentPid == Environment.ProcessId)
            throw new ArgumentException("更新助手参数无效。");
        string installer = ValidateInstallerPath(args[1]);
        string expectedHash = ValidateHash(args[2]);
        if (!Path.IsPathFullyQualified(args[4]) || !string.Equals(Path.GetExtension(args[4]), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("原应用程序路径无效。");
        string originalExe = Path.GetFullPath(args[4]);
        string logDirectory = Path.GetDirectoryName(installer)!;
        string installLog = Path.Combine(logDirectory, "msi-install.log");
        bool canRestartOriginal = false;
        try
        {
            using var verifiedInstaller = OpenVerifiedInstaller(installer, expectedHash);
            // Windows Installer runs in a service and needs the physical file path
            // when a packaged host redirects the user's LocalAppData directory.
            string serviceInstallerPath = PhysicalInstallerPath(verifiedInstaller);
            logDirectory = Path.GetDirectoryName(serviceInstallerPath)!;
            installLog = Path.Combine(logDirectory, "msi-install.log");
            string userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..24];
            using var installationMutex = new Mutex(false, @"Local\BuckCalculator.Update." + userKey);
            bool ownsMutex;
            try { ownsMutex = installationMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
                throw new InvalidOperationException("另一个 BUCK 更新安装正在进行，请等待完成后重试。");
            try
            {
                WaitForParent(parentPid, originalExe);
                canRestartOriginal = true;
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (string argument in new[] { "/i", serviceInstallerPath, "/passive", "/norestart", "/l*v", installLog })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Windows Installer。");
                canRestartOriginal = false;
                if (!process.WaitForExit(15 * 60 * 1000))
                    throw new TimeoutException($"Windows Installer 在 15 分钟内未结束。安装事务仍可能继续，请等待安装结束后从快捷方式启动应用。日志位于 {installLog}。");
                canRestartOriginal = true;
                if (process.ExitCode is not 0 and not 3010)
                    throw new InvalidOperationException($"Windows Installer 返回 {process.ExitCode}，请查看 {installLog}。");
                string installedExe = InstalledExecutablePath() ?? throw new FileNotFoundException("安装程序结束后未找到新版应用，请查看 MSI 安装日志。", installLog);
                RestartApplication(installedExe);
                return process.ExitCode;
            }
            finally
            {
                installationMutex.ReleaseMutex();
            }
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(logDirectory, "update-error.txt"), exception.ToString());
            if (canRestartOriginal && File.Exists(originalExe))
            {
                try { RestartApplication(originalExe); }
                catch (Exception restartException) { File.AppendAllText(Path.Combine(logDirectory, "update-error.txt"), Environment.NewLine + restartException); }
            }
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("BuckCalculator/" + CurrentVersion.ToString(3));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(uri.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("更新元数据超过允许大小。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > maximumBytes)
                throw new InvalidDataException("更新元数据超过允许大小。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static string ReadChecksum(string content, string installerName)
    {
        string? found = null;
        foreach (string line in content.TrimStart('\uFEFF').Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), @"^([a-fA-F0-9]{64})[ \t]+\*?(.+)$", RegexOptions.CultureInvariant);
            if (!match.Success || match.Groups[2].Value != installerName)
                continue;
            if (found is not null)
                throw new InvalidDataException("校验文件包含重复的 MSI 条目。");
            found = ValidateHash(match.Groups[1].Value);
        }
        return found ?? throw new InvalidDataException("校验文件中缺少匹配 MSI 文件名的 SHA-256。");
    }

    private static Uri AssetUrl(JsonElement asset, string tag, string name)
    {
        if (!Uri.TryCreate(RequiredString(asset, "browser_download_url"), UriKind.Absolute, out var uri))
            throw new InvalidDataException("GitHub 附件下载地址无效。");
        ValidateAssetUrl(uri, tag, name);
        return uri;
    }

    private static void ValidateAssetUrl(Uri uri, string tag, string name)
    {
        string expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsoluteUri != expected)
            throw new InvalidDataException("更新附件下载地址必须属于固定的 GitHub 仓库与版本。");
    }

    private static string RequiredString(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new InvalidDataException($"更新元数据缺少 {name}。");

    private static AppUpdate ParseManifest(JsonElement manifest)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in manifest.EnumerateObject())
            if (!names.Add(property.Name))
                throw new InvalidDataException("更新清单包含重复字段。");

        string versionText = RequiredString(manifest, "version");
        if (!Regex.IsMatch(versionText, @"\A\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant)
            || !Version.TryParse(versionText, out var version)
            || NormalizeVersion(version).ToString(3) != versionText)
            throw new InvalidDataException("更新清单版本必须使用规范的 X.Y.Z 格式。");
        version = NormalizeVersion(version);
        string tag = "v" + versionText;
        string installerName = InstallerName(versionText);
        if (!Uri.TryCreate(RequiredString(manifest, "url"), UriKind.Absolute, out var installerUrl))
            throw new InvalidDataException("更新清单 MSI 下载地址无效。");
        ValidateAssetUrl(installerUrl, tag, installerName);
        string hash = ValidateHash(RequiredString(manifest, "sha256"));
        long size = manifest.GetProperty("size").GetInt64();
        ValidateSize(size);
        string expectedReleaseUrl = $"https://github.com/{Repository}/releases/tag/{tag}";
        if (!Uri.TryCreate(RequiredString(manifest, "releaseUrl"), UriKind.Absolute, out var releaseUrl)
            || releaseUrl.AbsoluteUri != expectedReleaseUrl)
            throw new InvalidDataException("更新清单发布页必须属于固定 GitHub 仓库与版本。");
        string releaseNotes = RequiredString(manifest, "releaseNotes");
        return new AppUpdate(version, versionText, installerUrl, hash, size, releaseNotes, releaseUrl);
    }
    private static string InstallerName(string versionText) => $"BuckCalculator-v{versionText}-win-x64.msi";
    private static Version NormalizeVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static string ValidateHash(string hash)
    {
        if (hash is null || !Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("SHA-256 摘要必须包含 64 个十六进制字符。");
        return hash.ToLowerInvariant();
    }

    private static void ValidateSize(long size)
    {
        if (size <= 0 || size > MaximumInstallerBytes)
            throw new InvalidDataException("MSI 大小必须介于 1 字节与 1 GiB 之间。");
    }

    private static string ValidateInstallerPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("安装包必须使用绝对路径。");
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetFullPath(DefaultUpdateDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(fullPath), ".msi", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("安装包必须位于 BUCK 更新缓存中，并使用 .msi 扩展名。");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("更新安装包不存在。", fullPath);
        return fullPath;
    }

    private static FileStream OpenVerifiedInstaller(string path, string expectedHash)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            ValidateSize(stream.Length);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(expectedHash)))
                throw new InvalidDataException("安装前 SHA-256 复核失败。");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string PhysicalInstallerPath(FileStream installer)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(installer.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("无法解析安装服务所需的安装包实际路径。", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        string path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    private static void WaitForParent(int parentPid, string originalExe)
    {
        Process parent;
        try { parent = Process.GetProcessById(parentPid); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (!parent.HasExited && !string.Equals(parent.MainModule?.FileName, originalExe, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新助手检测到原进程路径不匹配。");
            if (!parent.WaitForExit(60_000))
                throw new TimeoutException("原应用程序在 60 秒内未退出，安装未开始。");
        }
    }

    private static string? InstalledExecutablePath()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Epiphany-Lucky\BuckCalculator");
            if (key?.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location))
            {
                string candidate = Path.Combine(location, "BuckCalculator.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        string defaultExecutable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BuckCalculator", "BuckCalculator.exe");
        return File.Exists(defaultExecutable) ? defaultExecutable : null;
    }

    private static void RestartApplication(string executable)
    {
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable) })
            ?? throw new InvalidOperationException("无法重新启动 BUCK 计算器。");
    }
}
