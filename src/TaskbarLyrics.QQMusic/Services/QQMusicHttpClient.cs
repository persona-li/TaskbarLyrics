using System.Diagnostics;
using System.Text;
using TaskbarLyrics.QQMusic.Logging;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.QQMusic.Services;

/// <summary>
/// Long-lived HttpClient wrapper for QQ Music anonymous APIs.
/// Retries only transient/5xx failures (max 2 attempts). Never retries 403/404.
/// </summary>
public sealed class QQMusicHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IQqMusicLogger _logger;
    private bool _disposed;

    public QQMusicHttpClient(IQqMusicLogger logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", QQMusicEndpoints.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", QQMusicEndpoints.Accept);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", QQMusicEndpoints.CacheControl);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", QQMusicEndpoints.Referer);
    }

    public async Task<HttpDiagnostics> GetAsync(
        string stage,
        string baseUrl,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(baseUrl, query);
        Exception? lastException = null;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                sw.Stop();

                var diag = new HttpDiagnostics
                {
                    Method = "GET",
                    Url = url,
                    StatusCode = (int)response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase ?? string.Empty,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    ContentLength = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(body),
                    Elapsed = sw.Elapsed,
                    Body = body,
                    Attempt = attempt
                };

                _logger.Http(stage, diag);
                LogStatusHints(stage, diag);

                // Retry only on 5xx.
                if ((int)response.StatusCode >= 500 && attempt < 2)
                {
                    _logger.Warn($"[{stage}] HTTP {diag.StatusCode} — retrying ({attempt}/2)...");
                    await Task.Delay(300, cancellationToken);
                    continue;
                }

                return diag;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient timeout
                sw.Stop();
                lastException = new TimeoutException($"Request timed out after {_http.Timeout.TotalSeconds}s: {url}");
                _logger.Error(stage, lastException);
                if (attempt < 2)
                {
                    _logger.Warn($"[{stage}] Timeout — retrying ({attempt}/2)...");
                    continue;
                }
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                lastException = ex;
                _logger.Error(stage, ex);
                if (attempt < 2)
                {
                    _logger.Warn($"[{stage}] Network error — retrying ({attempt}/2)...");
                    await Task.Delay(300, cancellationToken);
                    continue;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                lastException = ex;
                _logger.Error(stage, ex);
                break;
            }
        }

        return new HttpDiagnostics
        {
            Method = "GET",
            Url = url,
            StatusCode = 0,
            ReasonPhrase = lastException?.Message ?? "request failed",
            Elapsed = TimeSpan.Zero,
            Body = string.Empty,
            Attempt = 2
        };
    }

    private void LogStatusHints(string stage, HttpDiagnostics d)
    {
        if (d.StatusCode == 429)
        {
            _logger.Warn($"[{stage}] 可能触发 QQ 音乐请求频率限制。");
        }
        else if (d.StatusCode == 403)
        {
            _logger.Warn($"[{stage}] 当前匿名请求方式可能被 QQ 音乐拒绝。");
        }
        else if (d.StatusCode == 404)
        {
            _logger.Warn($"[{stage}] 接口返回 404，可能 endpoint 已变更或下线。");
        }
        else if (d.StatusCode >= 500)
        {
            _logger.Warn($"[{stage}] QQ 音乐服务端错误 HTTP {d.StatusCode}。");
        }
    }

    public static string BuildUrl(string baseUrl, IReadOnlyDictionary<string, string> query)
    {
        var sb = new StringBuilder(baseUrl);
        sb.Append('?');
        var first = true;
        foreach (var kv in query)
        {
            if (!first)
            {
                sb.Append('&');
            }

            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value ?? string.Empty));
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }
}
