using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CanVariableMonitor;

internal sealed class AppUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public sealed class UpdateConfig
    {
        public bool AutoCheck { get; set; } = true;
        public bool AutoInstall { get; set; }
        public string Channel { get; set; } = "stable";
        public string ManifestUrl { get; set; } = "";
        public string DownloadApiUrl { get; set; } = "";
        public string DownloadApiMethod { get; set; } = "POST";
        public string FileNameParameter { get; set; } = "fileName";
        public string VersionHeaderName { get; set; } = "X-Version";
        public string VersionFileName { get; set; } = "";
        public string PackageFileName { get; set; } = "";
        public string PackageSha256 { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public bool Force { get; set; }
        public int TimeoutSeconds { get; set; } = 8;
    }

    public sealed class UpdateManifest
    {
        public string Version { get; set; } = "";
        public string Channel { get; set; } = "stable";
        public string PackageUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public bool Force { get; set; }
    }

    public sealed record UpdateCheckResult(
        bool Configured,
        bool UpdateAvailable,
        string Message,
        UpdateConfig? Config = null,
        UpdateManifest? Manifest = null);

    private readonly string _configPath;
    private readonly string _appDataConfigPath;
    private readonly string _downloadDirectory;

    public AppUpdateService()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "update_config.json");
        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanVariableMonitor");
        _appDataConfigPath = Path.Combine(appDataDirectory, "update_config.json");
        _downloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CanVariableMonitor",
            "updates");
    }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken)
    {
        UpdateConfig? config = ReadConfig();
        if (config == null || (string.IsNullOrWhiteSpace(config.ManifestUrl) && string.IsNullOrWhiteSpace(config.DownloadApiUrl)))
        {
            return new UpdateCheckResult(false, false, "未配置 update_config.json，跳过服务器版本检查。");
        }

        if (!config.AutoCheck)
        {
            return new UpdateCheckResult(true, false, "自动检查更新已关闭。", config);
        }

        if (!string.IsNullOrWhiteSpace(config.DownloadApiUrl))
        {
            return await CheckDownloadApiAsync(config, currentVersion, cancellationToken).ConfigureAwait(false);
        }

        return await CheckManifestAsync(config, currentVersion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateCheckResult> CheckManifestAsync(
        UpdateConfig config,
        string currentVersion,
        CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient(config);
        using HttpResponseMessage response = await httpClient.GetAsync(config.ManifestUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        UpdateManifest? manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (manifest == null)
        {
            return new UpdateCheckResult(true, false, "服务器版本文件为空或格式错误。", config);
        }

        if (!string.IsNullOrWhiteSpace(manifest.Channel) &&
            !manifest.Channel.Equals(config.Channel, StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateCheckResult(true, false, $"服务器通道为 {manifest.Channel}，当前配置通道为 {config.Channel}，不更新。", config, manifest);
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            return new UpdateCheckResult(true, false, "服务器版本文件缺少 version。", config, manifest);
        }

        if (CompareVersions(manifest.Version, currentVersion) <= 0)
        {
            return new UpdateCheckResult(true, false, $"当前已经是最新版本：{currentVersion}。", config, manifest);
        }

        if (string.IsNullOrWhiteSpace(manifest.PackageUrl))
        {
            return new UpdateCheckResult(true, false, $"发现新版本 {manifest.Version}，但服务器版本文件缺少 packageUrl。", config, manifest);
        }

        return new UpdateCheckResult(true, true, $"发现新版本：{manifest.Version}。", config, manifest);
    }

    private async Task<UpdateCheckResult> CheckDownloadApiAsync(
        UpdateConfig config,
        string currentVersion,
        CancellationToken cancellationToken)
    {
        string probeFileName = FirstNonEmpty(config.VersionFileName, config.PackageFileName);
        if (string.IsNullOrWhiteSpace(probeFileName))
        {
            return new UpdateCheckResult(true, false, "自动更新 API 模式缺少 packageFileName 或 versionFileName。", config);
        }

        using var httpClient = CreateHttpClient(config);
        using HttpRequestMessage request = CreateDownloadApiRequest(config, probeFileName);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string version = GetHeaderValue(response, FirstNonEmpty(config.VersionHeaderName, "X-Version"));
        if (string.IsNullOrWhiteSpace(version))
        {
            return new UpdateCheckResult(true, false, "自动更新 API 返回头缺少 X-Version，无法判断版本。", config);
        }

        string packageFileName = FirstNonEmpty(config.PackageFileName, probeFileName);
        var manifest = new UpdateManifest
        {
            Version = version.Trim(),
            Channel = config.Channel,
            PackageUrl = packageFileName,
            Sha256 = config.PackageSha256,
            ReleaseNotes = config.ReleaseNotes,
            Force = config.Force
        };

        if (CompareVersions(manifest.Version, currentVersion) <= 0)
        {
            return new UpdateCheckResult(true, false, $"当前已经是最新版本：{currentVersion}。", config, manifest);
        }

        return new UpdateCheckResult(true, true, $"发现新版本：{manifest.Version}。", config, manifest);
    }

    public async Task<string> DownloadPackageAsync(
        UpdateConfig config,
        UpdateManifest manifest,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        bool downloadApiMode = !string.IsNullOrWhiteSpace(config.DownloadApiUrl);
        Uri? packageUri = downloadApiMode ? null : ResolvePackageUri(config.ManifestUrl, manifest.PackageUrl);
        Directory.CreateDirectory(_downloadDirectory);
        string safeVersion = Regex.Replace(manifest.Version.Trim(), @"[^\w\.-]+", "_");
        string packagePath = Path.Combine(_downloadDirectory, $"update_{safeVersion}.zip");
        string tempPath = packagePath + ".download";

        using var httpClient = CreateHttpClient(config);
        using HttpRequestMessage? apiRequest = downloadApiMode ? CreateDownloadApiRequest(config, manifest.PackageUrl) : null;
        using HttpResponseMessage response = downloadApiMode
            ? await httpClient.SendAsync(apiRequest!, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)
            : await httpClient.GetAsync(packageUri!, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? totalBytes = response.Content.Headers.ContentLength;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream target = File.Create(tempPath);

        var buffer = new byte[1024 * 128];
        long received = 0;
        int lastPercent = -1;
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            long total = totalBytes.GetValueOrDefault();
            if (total > 0)
            {
                int percent = (int)Math.Clamp(received * 100 / total, 0, 100);
                if (percent >= lastPercent + 10 || percent == 100)
                {
                    lastPercent = percent;
                    progress?.Report($"下载 {percent}%");
                }
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Close();

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            string actualHash = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                throw new InvalidOperationException("更新包 SHA256 校验失败，已取消更新。");
            }
        }

        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }
        File.Move(tempPath, packagePath);
        progress?.Report("下载完成");
        return packagePath;
    }

    public void LaunchUpdater(string packagePath)
    {
        string installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string exePath = Environment.ProcessPath ?? Path.Combine(installDirectory, "上位机监控.exe");
        string scriptPath = Path.Combine(_downloadDirectory, "run_update.ps1");
        Directory.CreateDirectory(_downloadDirectory);
        File.WriteAllText(scriptPath, BuildUpdaterScript(Process.GetCurrentProcess().Id, installDirectory, packagePath, exePath));

        var processStartInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = installDirectory
        };
        Process.Start(processStartInfo);
    }

    private UpdateConfig? ReadConfig()
    {
        string path = File.Exists(_configPath) ? _configPath : _appDataConfigPath;
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<UpdateConfig>(File.ReadAllText(path), JsonOptions);
    }

    private static HttpClient CreateHttpClient(UpdateConfig config)
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 3, 60))
        };
    }

    private static Uri ResolvePackageUri(string manifestUrl, string packageUrl)
    {
        if (Uri.TryCreate(packageUrl, UriKind.Absolute, out Uri? absolute))
        {
            return absolute;
        }

        var manifestUri = new Uri(manifestUrl, UriKind.Absolute);
        return new Uri(manifestUri, packageUrl);
    }

    private static HttpRequestMessage CreateDownloadApiRequest(UpdateConfig config, string fileName)
    {
        string method = FirstNonEmpty(config.DownloadApiMethod, "POST").ToUpperInvariant();
        return new HttpRequestMessage(new HttpMethod(method), BuildDownloadApiUri(config, fileName));
    }

    private static Uri BuildDownloadApiUri(UpdateConfig config, string fileName)
    {
        if (string.IsNullOrWhiteSpace(config.DownloadApiUrl))
        {
            throw new InvalidOperationException("downloadApiUrl is empty.");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("fileName is empty.");
        }

        var builder = new UriBuilder(config.DownloadApiUrl);
        string parameterName = FirstNonEmpty(config.FileNameParameter, "fileName");
        string query = builder.Query;
        if (query.StartsWith("?", StringComparison.Ordinal))
        {
            query = query[1..];
        }

        string encodedPair = Uri.EscapeDataString(parameterName) + "=" + Uri.EscapeDataString(fileName);
        builder.Query = string.IsNullOrWhiteSpace(query) ? encodedPair : query + "&" + encodedPair;
        return builder.Uri;
    }

    private static string GetHeaderValue(HttpResponseMessage response, string headerName)
    {
        if (response.Headers.TryGetValues(headerName, out IEnumerable<string>? values))
        {
            return values.FirstOrDefault() ?? "";
        }

        if (response.Content.Headers.TryGetValues(headerName, out values))
        {
            return values.FirstOrDefault() ?? "";
        }

        return "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int CompareVersions(string left, string right)
    {
        int[] a = ExtractVersionParts(left);
        int[] b = ExtractVersionParts(right);
        int count = Math.Max(a.Length, b.Length);
        for (int i = 0; i < count; i++)
        {
            int av = i < a.Length ? a[i] : 0;
            int bv = i < b.Length ? b[i] : 0;
            int comparison = av.CompareTo(bv);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int[] ExtractVersionParts(string value)
    {
        return Regex.Matches(value ?? "", @"\d+")
            .Select(match => int.TryParse(match.Value, out int number) ? number : 0)
            .ToArray();
    }

    private static string BuildUpdaterScript(int processId, string installDirectory, string packagePath, string exePath)
    {
        static string Quote(string value)
        {
            return "'" + value.Replace("'", "''") + "'";
        }

        return $$"""
$ErrorActionPreference = 'Stop'
$processId = {{processId}}
$installDir = {{Quote(installDirectory)}}
$packageZip = {{Quote(packagePath)}}
$exePath = {{Quote(exePath)}}
$logDir = Join-Path $env:APPDATA 'CanVariableMonitor'
$logPath = Join-Path $logDir 'update.log'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
function Write-UpdateLog([string]$message) {
    Add-Content -LiteralPath $logPath -Value ('[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}' -f (Get-Date), $message)
}

try {
    Write-UpdateLog '等待主程序退出。'
    Wait-Process -Id $processId -Timeout 90 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Get-Process 'CanVariableMonitor.OfflineCWorker' -ErrorAction SilentlyContinue | Stop-Process -Force

    $extractDir = Join-Path $env:TEMP ('canmon_update_extract_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
    Expand-Archive -LiteralPath $packageZip -DestinationPath $extractDir -Force

    $sourceDir = $extractDir
    $items = @(Get-ChildItem -LiteralPath $extractDir -Force)
    if ($items.Count -eq 1 -and $items[0].PSIsContainer -and (Test-Path -LiteralPath (Join-Path $items[0].FullName '上位机监控.exe'))) {
        $sourceDir = $items[0].FullName
    }

    Write-UpdateLog ('复制更新文件：' + $sourceDir)
    Get-ChildItem -LiteralPath $sourceDir -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $installDir -Recurse -Force
    }

    Write-UpdateLog '更新完成，重启主程序。'
    Start-Process -FilePath $exePath -WorkingDirectory $installDir
} catch {
    Write-UpdateLog ('更新失败：' + $_.Exception.Message)
}
""";
    }
}
