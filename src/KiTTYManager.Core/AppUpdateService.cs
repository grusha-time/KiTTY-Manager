using System.Net.Http.Headers;
using System.Text.Json;

namespace KiTTYManager.Core;

public class AppUpdateService : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool disposeClient;

    public const string DefaultOwner = "grusha-time";
    public const string DefaultRepo = "KiTTY-Manager";

    public AppUpdateService(HttpClient? customClient = null)
    {
        if (customClient is not null)
        {
            httpClient = customClient;
            disposeClient = false;
        }
        else
        {
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("KiTTYManager-Updater", ProductInfo.Version));
            httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            disposeClient = true;
        }
    }

    public async Task<AppReleaseInfo> GetLatestReleaseAsync(
        string owner = DefaultOwner,
        string repo = DefaultRepo,
        CancellationToken token = default)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KiTTYManager-Updater", ProductInfo.Version));
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"Ошибка соединения с GitHub API при проверке обновлений: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Таймаут запроса к GitHub API при проверке обновлений.", ex);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException($"Репозиторий {owner}/{repo} или его релизы не найдены на GitHub.");

            if ((int)response.StatusCode == 403 && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
                remaining.FirstOrDefault() == "0")
            {
                throw new InvalidOperationException("Превышен лимит запросов к GitHub API (Rate Limit). Повторите попытку позже.");
            }

            if (!response.IsSuccessStatusCode)
                throw new IOException($"GitHub API вернул статус: {(int)response.StatusCode} {response.ReasonPhrase}");

            var jsonStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return ParseRelease(jsonStream);
        }
    }

    public static AppReleaseInfo ParseRelease(Stream jsonStream)
    {
        using var doc = JsonDocument.Parse(jsonStream);
        return ParseRelease(doc.RootElement);
    }

    public static AppReleaseInfo ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseRelease(doc.RootElement);
    }

    public static AppReleaseInfo ParseRelease(JsonElement root)
    {
        var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
        var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
        var htmlUrl = root.TryGetProperty("html_url", out var htmlUrlProp) ? htmlUrlProp.GetString() ?? "" : "";
        var isPrerelease = root.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean();

        DateTimeOffset? publishedAt = null;
        if (root.TryGetProperty("published_at", out var pubProp) && pubProp.TryGetDateTimeOffset(out var pubDate))
            publishedAt = pubDate;

        if (!AppUpdatePolicy.TryParseVersion(tagName, out var version) &&
            !AppUpdatePolicy.TryParseVersion(name, out version))
        {
            throw new InvalidDataException($"Не удалось определить версию из релиза GitHub (tag: '{tagName}', name: '{name}').");
        }

        var assetsList = new List<AppReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetElement in assetsProp.EnumerateArray())
            {
                var assetName = assetElement.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                var downloadUrl = assetElement.TryGetProperty("browser_download_url", out var du) ? du.GetString() ?? "" : "";
                var size = assetElement.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0L;
                var contentType = assetElement.TryGetProperty("content_type", out var ct) ? ct.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(assetName) && !string.IsNullOrWhiteSpace(downloadUrl))
                    assetsList.Add(new AppReleaseAsset(assetName, downloadUrl, size, contentType));
            }
        }

        var selectedAsset = AppUpdatePolicy.SelectPackageAsset(assetsList);

        return new AppReleaseInfo(
            TagName: tagName,
            Name: string.IsNullOrWhiteSpace(name) ? tagName : name,
            Body: body,
            PublishedAt: publishedAt,
            HtmlUrl: htmlUrl,
            Asset: selectedAsset,
            NormalizedVersion: version,
            IsPrerelease: isPrerelease);
    }

    public async Task DownloadAssetAsync(
        AppReleaseAsset asset,
        string destinationFilePath,
        Action<long, long?>? progress = null,
        CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationFilePath))!);

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : (long?)null);

        var tempFile = destinationFilePath + ".part";
        try
        {
            using (var contentStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, token).ConfigureAwait(false);
                    totalRead += bytesRead;
                    progress?.Invoke(totalRead, totalBytes);
                }
            }

            if (File.Exists(destinationFilePath))
                File.Delete(destinationFilePath);

            File.Move(tempFile, destinationFilePath);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }
    }

    public void Dispose()
    {
        if (disposeClient) httpClient.Dispose();
    }
}
