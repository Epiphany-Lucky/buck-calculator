using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BuckCalculator;

const string repository = "https://github.com/Epiphany-Lucky/buck-calculator";
const string versionText = "1.2.0";
const string installerName = "BuckCalculator-v1.2.0-win-x64.msi";
string installerUrl = $"{repository}/releases/download/v{versionText}/{installerName}";
byte[] payload = Enumerable.Range(0, 200000).Select(index => (byte)(index % 251)).ToArray();
string sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
string testRoot = Path.Combine(Path.GetTempPath(), "BuckCalculator-UpdateChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int checks = 0;
var clients = new List<HttpClient>();

void Check(bool condition, string message)
{
    checks++;
    if (!condition)
        throw new InvalidOperationException(message);
}

async Task RejectedAsync(Func<Task> action, string message, Type? expectedException = null)
{
    try
    {
        await action();
    }
    catch (Exception exception) when (expectedException?.IsInstanceOfType(exception) ?? exception is InvalidDataException or HttpRequestException or IOException or OperationCanceledException)
    {
        Check(!string.IsNullOrWhiteSpace(exception.Message), message);
        return;
    }
    throw new InvalidOperationException("Expected rejection: " + message);
}

JsonObject Release(string tag = "v1.2.0", bool withDigest = true)
{
    string name = $"BuckCalculator-{tag}-win-x64.msi";
    var asset = new JsonObject
    {
        ["name"] = name,
        ["browser_download_url"] = $"{repository}/releases/download/{tag}/{name}",
        ["size"] = payload.Length,
        ["digest"] = withDigest ? "sha256:" + sha256 : null
    };
    return new JsonObject
    {
        ["tag_name"] = tag,
        ["draft"] = false,
        ["prerelease"] = false,
        ["html_url"] = $"{repository}/releases/tag/{tag}",
        ["body"] = "本次更新说明",
        ["assets"] = new JsonArray(asset)
    };
}

HttpResponseMessage JsonResponse(JsonObject release) => new(HttpStatusCode.OK) { Content = new StringContent(release.ToJsonString(), Encoding.UTF8, "application/json") };
HttpResponseMessage DownloadResponse() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

(UpdateService Service, string Directory, TestHandler Handler) CreateService(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, Version? current = null)
{
    var handler = new TestHandler(responder);
    var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    clients.Add(client);
    string directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
    return (new UpdateService(client, directory, current ?? new Version(1, 0, 0)), directory, handler);
}

(UpdateService Service, string Directory, TestHandler Handler) MetadataService(JsonObject release) => CreateService((_, _) => Task.FromResult(JsonResponse(release)));
AppUpdate Update() => new(new Version(versionText), versionText, new Uri(installerUrl), sha256, payload.Length, "更新说明", new Uri($"{repository}/releases/tag/v{versionText}"));

void NoPartialFiles(string directory)
{
    Check(!Directory.Exists(directory) || Directory.GetFiles(directory, "*.partial").Length == 0, "Partial download files were cleaned");
}

try
{
    var latest = MetadataService(Release("v1.0.0"));
    Check(await latest.Service.CheckAsync() is null, "Current stable release returns no update");
    var normalized = CreateService((_, _) => Task.FromResult(JsonResponse(Release("v1.0.0"))), new Version(1, 0, 0, 0));
    Check(await normalized.Service.CheckAsync() is null, "Assembly fourth zero component compares correctly");
    var older = MetadataService(Release("v0.9.0"));
    Check(await older.Service.CheckAsync() is null, "Older stable release returns no update");

    var available = MetadataService(Release());
    var update = await available.Service.CheckAsync() ?? throw new Exception("New update missing");
    Check(update.Version == new Version(1, 2, 0) && update.VersionText == versionText, "New stable version parsed");
    Check(update.Sha256 == sha256 && update.Size == payload.Length && update.InstallerUrl.AbsoluteUri == installerUrl, "MSI digest, size and URL parsed");
    Check(available.Handler.Requests == 1, "Asset digest avoids checksum request");
    Check(update.ReleaseNotes == "本次更新说明", "Release notes retained");

    foreach (string flag in new[] { "draft", "prerelease" })
    {
        var release = Release();
        release[flag] = true;
        Check(await MetadataService(release).Service.CheckAsync() is null, flag + " excluded");
    }
    foreach (string tag in new[] { "1.2.0", "v1.2", "v1.2.0-beta", "v01.2.0", "v1.2.0\n", "v2147483648.0.0" })
        await RejectedAsync(async () => { await MetadataService(Release(tag)).Service.CheckAsync(); }, "Malformed version rejected");

    var noMsiRelease = Release();
    noMsiRelease["assets"] = new JsonArray(new JsonObject { ["name"] = "BuckCalculator-v1.2.0-win-x64.exe" });
    Check(await MetadataService(noMsiRelease).Service.CheckAsync() is null, "New release without matching MSI is excluded");
    var duplicateMsi = Release();
    ((JsonArray)duplicateMsi["assets"]!).Add(duplicateMsi["assets"]![0]!.DeepClone());
    await RejectedAsync(async () => { await MetadataService(duplicateMsi).Service.CheckAsync(); }, "Duplicate MSI rejected");

    foreach (string url in new[]
    {
        installerUrl.Replace("https://", "http://"),
        installerUrl.Replace("github.com", "github.com.attacker.example"),
        installerUrl.Replace("Epiphany-Lucky", "SomeoneElse"),
        installerUrl.Replace("/v1.2.0/", "/v1.3.0/"),
        installerUrl + "?redirect=other",
        installerUrl + "#fragment"
    })
    {
        var release = Release();
        release["assets"]![0]!["browser_download_url"] = url;
        await RejectedAsync(async () => { await MetadataService(release).Service.CheckAsync(); }, "Untrusted MSI URL rejected");
    }
    var wrongReleaseUrl = Release();
    wrongReleaseUrl["html_url"] = "https://github.com/Other/Repository/releases/tag/v1.2.0";
    await RejectedAsync(async () => { await MetadataService(wrongReleaseUrl).Service.CheckAsync(); }, "Untrusted release URL rejected");
    foreach (long size in new[] { 0L, -1L, 1024L * 1024 * 1024 + 1 })
    {
        var release = Release();
        release["assets"]![0]!["size"] = size;
        await RejectedAsync(async () => { await MetadataService(release).Service.CheckAsync(); }, "Invalid MSI size rejected");
    }
    var malformedDigest = Release();
    malformedDigest["assets"]![0]!["digest"] = "sha256:bad";
    await RejectedAsync(async () => { await MetadataService(malformedDigest).Service.CheckAsync(); }, "Malformed asset digest rejected");

    JsonObject ChecksumRelease()
    {
        var release = Release(withDigest: false);
        ((JsonArray)release["assets"]!).Add(new JsonObject
        {
            ["name"] = "SHA256SUMS.txt",
            ["browser_download_url"] = $"{repository}/releases/download/v1.2.0/SHA256SUMS.txt"
        });
        return release;
    }
    var fallbackRelease = ChecksumRelease();
    var fallback = CreateService((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com"
        ? JsonResponse(fallbackRelease)
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\uFEFF" + sha256.ToUpperInvariant() + "  " + installerName + "\r\n") }));
    Check((await fallback.Service.CheckAsync())?.Sha256 == sha256 && fallback.Handler.Requests == 2, "Checksum-file fallback supports exact filename and BOM");
    var wrongChecksumName = CreateService((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com"
        ? JsonResponse(ChecksumRelease())
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sha256 + "  prefix-" + installerName) }));
    await RejectedAsync(async () => { await wrongChecksumName.Service.CheckAsync(); }, "Checksum requires exact filename");
    var duplicateChecksum = CreateService((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com"
        ? JsonResponse(ChecksumRelease())
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{sha256}  {installerName}\n{sha256}  {installerName}\n") }));
    await RejectedAsync(async () => { await duplicateChecksum.Service.CheckAsync(); }, "Duplicate checksum entries rejected");
    await RejectedAsync(async () => { await MetadataService(Release(withDigest: false)).Service.CheckAsync(); }, "Missing checksum asset rejected");
    var malformedJson = CreateService((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{bad json") }));
    await RejectedAsync(async () => { await malformedJson.Service.CheckAsync(); }, "Malformed JSON rejected");

    var successful = CreateService((_, _) => Task.FromResult(DownloadResponse()));
    var progress = new List<double>();
    string installedPath = await successful.Service.DownloadAsync(Update(), new CallbackProgress(progress.Add));
    Check(File.ReadAllBytes(installedPath).SequenceEqual(payload), "Verified MSI downloaded");
    Check(progress.First() == 0 && progress.Last() == 1 && progress.All(value => value is >= 0 and <= 1), "Progress uses zero-to-one range");
    NoPartialFiles(successful.Directory);

    var wrongHash = CreateService((_, _) => Task.FromResult(DownloadResponse()));
    await RejectedAsync(async () => { await wrongHash.Service.DownloadAsync(Update() with { Sha256 = new string('0', 64) }); }, "SHA mismatch rejected");
    Check(!File.Exists(Path.Combine(wrongHash.Directory, installerName)), "Unverified MSI never becomes final file");
    NoPartialFiles(wrongHash.Directory);

    var preserved = CreateService((_, _) => Task.FromResult(DownloadResponse()));
    Directory.CreateDirectory(preserved.Directory);
    string previousPath = Path.Combine(preserved.Directory, installerName);
    File.WriteAllText(previousPath, "previous verified installer placeholder");
    await RejectedAsync(async () => { await preserved.Service.DownloadAsync(Update() with { Sha256 = new string('0', 64) }); }, "Retry hash failure");
    Check(File.ReadAllText(previousPath) == "previous verified installer placeholder", "Failed retry preserves existing complete file");

    var sizeMismatch = CreateService((_, _) => Task.FromResult(DownloadResponse()));
    await RejectedAsync(async () => { await sizeMismatch.Service.DownloadAsync(Update() with { Size = payload.Length + 1 }); }, "Declared content length mismatch rejected");
    NoPartialFiles(sizeMismatch.Directory);
    var truncated = CreateService((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(payload[..100])) }));
    await RejectedAsync(async () => { await truncated.Service.DownloadAsync(Update()); }, "Truncated body rejected");
    NoPartialFiles(truncated.Directory);
    var oversizedBody = CreateService((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(payload)) }));
    await RejectedAsync(async () => { await oversizedBody.Service.DownloadAsync(Update() with { Size = 100 }); }, "Body exceeding metadata size rejected");
    NoPartialFiles(oversizedBody.Directory);

    int attempts = 0;
    var retry = CreateService((_, _) => Task.FromResult(++attempts == 1
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FaultingStream(payload)) }
        : DownloadResponse()));
    await RejectedAsync(async () => { await retry.Service.DownloadAsync(Update()); }, "Interrupted body rejected", typeof(IOException));
    NoPartialFiles(retry.Directory);
    Check(File.ReadAllBytes(await retry.Service.DownloadAsync(Update())).SequenceEqual(payload), "Retry starts clean and succeeds");

    using var cancellation = new CancellationTokenSource();
    var cancelled = CreateService((_, _) => Task.FromResult(DownloadResponse()));
    await RejectedAsync(async () =>
    {
        await cancelled.Service.DownloadAsync(Update(), new CallbackProgress(value => { if (value > 0) cancellation.Cancel(); }), cancellation.Token);
    }, "Download cancellation respected", typeof(OperationCanceledException));
    NoPartialFiles(cancelled.Directory);
    Check(File.ReadAllBytes(await cancelled.Service.DownloadAsync(Update())).SequenceEqual(payload), "Download succeeds after cancelled attempt");

    var concurrent = CreateService((_, _) => Task.FromResult(DownloadResponse()));
    string[] concurrentPaths = await Task.WhenAll(concurrent.Service.DownloadAsync(Update()), concurrent.Service.DownloadAsync(Update()));
    Check(concurrentPaths[0] == concurrentPaths[1] && File.ReadAllBytes(concurrentPaths[0]).SequenceEqual(payload), "Concurrent downloads retain a complete verified final file");
    NoPartialFiles(concurrent.Directory);

    using var preCancellation = new CancellationTokenSource();
    preCancellation.Cancel();
    await RejectedAsync(async () => { await available.Service.CheckAsync(preCancellation.Token); }, "Pre-cancelled metadata request respected", typeof(OperationCanceledException));
    await RejectedAsync(async () => { await available.Service.DownloadAsync(Update(), cancellationToken: preCancellation.Token); }, "Pre-cancelled download respected", typeof(OperationCanceledException));

    var invalidDirectUrl = Update() with { InstallerUrl = new Uri("https://example.com/installer.msi") };
    await RejectedAsync(async () => { await successful.Service.DownloadAsync(invalidDirectUrl); }, "Download validates supplied update URL");
    await RejectedAsync(async () => { await successful.Service.DownloadAsync(Update() with { VersionText = "../1.2.0" }); }, "Download rejects version path traversal");
    await RejectedAsync(async () => { await successful.Service.DownloadAsync(Update() with { Size = 0 }); }, "Download rejects invalid size");
    var absent = CreateService((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
    Check(await absent.Service.CheckAsync() is null, "Repository without stable release handled");

    if (args.Contains("--live-check", StringComparer.Ordinal))
    {
        using var client = new HttpClient();
        var live = new UpdateService(client, testRoot, new Version(1, 1, 0));
        AppUpdate? result = await live.CheckAsync();
        Console.WriteLine(result is null ? "PASS live GitHub stable-release check, no newer MSI" : "PASS live GitHub stable-release check, version " + result.VersionText);
    }
    Console.WriteLine($"PASS {checks} update checks");
}
finally
{
    foreach (var client in clients)
        client.Dispose();
    Directory.Delete(testRoot, true);
}

sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (request.Headers.UserAgent.Count == 0)
            throw new InvalidOperationException("GitHub requires User-Agent");
        return responder(request, cancellationToken);
    }
}

sealed class CallbackProgress(Action<double> callback) : IProgress<double>
{
    public void Report(double value) => callback(value);
}

sealed class FaultingStream(byte[] data) : MemoryStream(data)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position != 0)
            throw new IOException("Simulated interrupted download");
        return base.ReadAsync(buffer[..Math.Min(100, buffer.Length)], cancellationToken);
    }
}

sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
{
    public override bool CanSeek => false;
}
