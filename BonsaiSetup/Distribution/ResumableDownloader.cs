using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace BonsaiSetup.Distribution;

internal sealed class ResumableDownloader : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task DownloadAsync(DownloadAsset asset, string destinationPath, CancellationToken cancellationToken, bool existingVerifiedByReceipt = false)
    {
        asset.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var partialPath = destinationPath + ".partial";

        if (File.Exists(partialPath) && new FileInfo(partialPath).Length == asset.SizeBytes)
        {
            var partialHash = await ComputeSha256Async(partialPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(partialHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(partialPath, destinationPath, overwrite: true);
                Console.WriteLine($"續傳檔已完整並通過 SHA-256：{asset.Id}");
                return;
            }
            File.Delete(partialPath);
        }

        if (File.Exists(destinationPath))
        {
            var existing = new FileInfo(destinationPath);
            if (existing.Length == asset.SizeBytes && existingVerifiedByReceipt)
            {
                Console.WriteLine($"已存在且安裝收據匹配：{asset.Id}");
                return;
            }
            if (existing.Length == asset.SizeBytes && string.Equals(await ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"已存在並符合 manifest：{asset.Id}");
                return;
            }
            File.Delete(destinationPath);
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await DownloadPartialAsync(asset, partialPath, cancellationToken).ConfigureAwait(false);
                var actualLength = new FileInfo(partialPath).Length;
                if (actualLength != asset.SizeBytes) throw new IOException($"{asset.Id} 位元組數不符：{actualLength} / {asset.SizeBytes}");
                var actualHash = await ComputeSha256Async(partialPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(partialPath);
                    if (attempt == 2) throw new InvalidDataException($"{asset.Id} SHA-256 不符，已重新下載一次仍失敗。");
                    Console.WriteLine($"{asset.Id} 完整性檢查不符，重新下載一次。");
                    continue;
                }

                File.Move(partialPath, destinationPath, overwrite: true);
                Console.WriteLine($"下載完成並通過 SHA-256：{asset.Id}");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt == 1 && exception is (HttpRequestException or IOException or InvalidDataException))
            {
                Console.WriteLine($"下載未完成，保留 partial 並嘗試續傳一次：{exception.Message}");
            }
        }

        throw new IOException($"下載失敗：{asset.Id}");
    }

    private async Task DownloadPartialAsync(DownloadAsset asset, string partialPath, CancellationToken cancellationToken)
    {
        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (offset > asset.SizeBytes)
        {
            File.Delete(partialPath);
            offset = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.UserAgent.ParseAdd("BonsaiLocalDistribution/1.0");
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partialPath);
            await DownloadPartialAsync(asset, partialPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException($"{asset.Id} redirected to a non-HTTPS URL.");
        var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent
                     && response.Content.Headers.ContentRange?.From == offset;
        if (offset > 0 && response.StatusCode == HttpStatusCode.PartialContent && !append)
        {
            File.Delete(partialPath);
            throw new InvalidDataException($"{asset.Id} 續傳 Range 起點與現有 partial 不相符。");
        }
        if (offset > 0 && !append)
        {
            offset = 0;
            File.Delete(partialPath);
        }

        var total = asset.SizeBytes;
        if (response.Content.Headers.ContentRange?.Length is long rangeLength) total = rangeLength;
        else if (response.Content.Headers.ContentLength is long contentLength && !append) total = contentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(partialPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        var transferred = offset;
        var lastReport = DateTimeOffset.UtcNow;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            transferred += read;
            if ((DateTimeOffset.UtcNow - lastReport).TotalMilliseconds >= 700)
            {
                var percent = Math.Min(100d, transferred * 100d / Math.Max(asset.SizeBytes, 1));
                Console.Write($"\r正在下載 {asset.Id}：{FormatBytes(transferred)} / {FormatBytes(asset.SizeBytes)} · {percent:0}%   ");
                lastReport = DateTimeOffset.UtcNow;
            }
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"\r下載完成 {asset.Id}：{FormatBytes(transferred)} / {FormatBytes(asset.SizeBytes)} · 100%     ");
        if (transferred != asset.SizeBytes) throw new IOException($"{asset.Id} 下載大小不符：{transferred} / {asset.SizeBytes}");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string FormatBytes(long bytes)
    {
        var value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        var number = bytes / Math.Pow(1000, unit);
        return $"{number:0.0} {units[unit]}";
    }

    public void Dispose() => _http.Dispose();
}
