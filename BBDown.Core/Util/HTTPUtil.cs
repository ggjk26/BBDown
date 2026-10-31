using System.Net;
using System.Net.Http.Headers;
using static BBDown.Core.Logger;

namespace BBDown.Core.Util;

public static class HTTPUtil
{

    public static readonly HttpClient AppHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    private static readonly Random random = new Random();
    private static readonly string[] platforms = { "Windows NT 10.0; Win64", "Macintosh; Intel Mac OS X 10_15", "X11; Linux x86_64" };

    private static string RandomVersion(int min, int max)
    {
        double version = random.NextDouble() * (max - min) + min;
        return version.ToString("F3");
    }

    private static string GetRandomUserAgent()
    {
        string[] browsers = { $"AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{RandomVersion(80, 110)} Safari/537.36", $"Gecko/20100101 Firefox/{RandomVersion(80, 110)}" };
        return $"Mozilla/5.0 ({platforms[random.Next(platforms.Length)]}) {browsers[random.Next(browsers.Length)]}";
    }

    public static string UserAgent { get; set; } = GetRandomUserAgent();

    public static async Task<string> GetWebSourceAsync(string url, string? userAgent = null, string? cookie = null)
    {
        using var webRequest = new HttpRequestMessage(HttpMethod.Get, url);
        webRequest.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? UserAgent);
        webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");

        // Prefer an explicitly supplied cookie over global state. This is important for
        // login verification, where the cookie being tested must be the one actually sent.
        string requestCookie = cookie ?? Config.COOKIE;
        if (url.Contains("/ep") || url.Contains("/ss"))
        {
            requestCookie = string.IsNullOrWhiteSpace(requestCookie)
                ? "CURRENT_FNVAL=4048"
                : $"{requestCookie.Trim().TrimEnd(';')}; CURRENT_FNVAL=4048";
        }

        if (!string.IsNullOrWhiteSpace(requestCookie))
            webRequest.Headers.TryAddWithoutValidation("Cookie", requestCookie);

        if (url.Contains("api.bilibili.com"))
            webRequest.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        if (url.Contains("api.bilibili.tv"))
            webRequest.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
        webRequest.Headers.Connection.Clear();

        // Never write authentication cookies or QR-login response URLs to debug logs.
        LogDebug("获取网页内容: Url: {0}, Cookie: {1}", url,
            string.IsNullOrWhiteSpace(requestCookie) ? "无" : "[已隐藏]");
        var webResponse = (await AppHttpClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();

        string htmlCode = await webResponse.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(requestCookie) && !url.Contains("/passport-login/web/qrcode/"))
            LogDebug("Response: {0}", htmlCode);
        else
            LogDebug("已收到可能包含认证信息的响应正文（日志中已隐藏）。");
        return htmlCode;
    }

    // 重写重定向处理, 自动跟随多次重定向
    public static async Task<string> GetWebLocationAsync(string url)
    {
        using var webRequest = new HttpRequestMessage(HttpMethod.Head, url);
        webRequest.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
        webRequest.Headers.Connection.Clear();

        LogDebug("获取网页重定向地址: Url: {0}, Headers: {1}", url, webRequest.Headers);
        var webResponse = (await AppHttpClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();
        string location = webResponse.RequestMessage.RequestUri.AbsoluteUri;
        LogDebug("Location: {0}", location);
        return location;
    }

    public static async Task<byte[]> GetPostResponseAsync(string Url, byte[] postData, Dictionary<string, string>? headers = null)
    {
        LogDebug("Post to: {0}, data: {1}", Url, Convert.ToBase64String(postData));

        ByteArrayContent content = new(postData);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/grpc");

        HttpRequestMessage request = new()
        {
            RequestUri = new Uri(Url),
            Method = HttpMethod.Post,
            Content = content,
            //Version = HttpVersion.Version20
        };

        if (headers != null)
        {
            foreach (KeyValuePair<string, string> header in headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 6.0.1; oneplus a5010 Build/V417IR) 6.10.0 os/android model/oneplus a5010 mobi_app/android build/6100500 channel/bili innerVer/6100500 osVer/6.0.1 network/2");
            request.Headers.TryAddWithoutValidation("grpc-encoding", "gzip");
        }

        HttpResponseMessage response = await AppHttpClient.SendAsync(request);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();

        return bytes;
    }
}
