using QRCoder;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BBDown.Core.Util;
using static BBDown.BBDownUtil;
using static BBDown.Core.Logger;

namespace BBDown;

internal static class BBDownLoginUtil
{
    private const int WebQrCodeLifetimeSeconds = 180;

    private static string ExtractWebCookie(string loginUrl)
    {
        if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("B站登录接口返回了无效的登录跳转地址。");

        // Only persist actual authentication cookies. The SSO URL may also contain
        // Expires/gourl query parameters, which are not browser cookies.
        var allowedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "DedeUserID",
            "DedeUserID__ckMd5",
            "SESSDATA",
            "bili_jct"
        };
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string pair = segment.Trim();
            int separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;

            string name = Uri.UnescapeDataString(pair[..separator]).Trim();
            if (!allowedNames.Contains(name))
                continue;

            // Keep the value URL-escaped. Cookie values can contain reserved characters
            // that must not be decoded before being sent back in the Cookie header.
            string value = pair[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
                cookies[name] = value;
        }

        if (!cookies.TryGetValue("SESSDATA", out var sessData) || string.IsNullOrWhiteSpace(sessData))
            throw new InvalidOperationException("登录结果中没有包含 SESSDATA，无法建立 WEB 会话。");

        string[] preferredOrder = ["DedeUserID", "DedeUserID__ckMd5", "SESSDATA", "bili_jct"];
        return string.Join("; ", preferredOrder
            .Where(cookies.ContainsKey)
            .Select(name => $"{name}={cookies[name]}"));
    }

    private static async Task SaveWebCookieAsync(string cookie)
    {
        Directory.CreateDirectory(Program.USER_DATA_DIR);

        string cookiePath = Path.Combine(Program.USER_DATA_DIR, "BBDown.data");
        string temporaryPath = cookiePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, cookie, new UTF8Encoding(false));
            File.Move(temporaryPath, cookiePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        // Keep the credential file private on Unix-like systems.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(cookiePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                LogDebug("无法设置 Cookie 文件权限：{0}", ex.Message);
            }
        }

        LogDebug("WEB Cookie 已保存至：{0}", cookiePath);
    }

    public static async Task<string> GetLoginStatusAsync(string qrcodeKey)
    {
        string queryUrl = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={Uri.EscapeDataString(qrcodeKey)}&source=main-fe-header";
        return await HTTPUtil.GetWebSourceAsync(queryUrl);
    }

    public static async Task LoginWEB()
    {
        string qrCodePath = Path.Combine(Environment.CurrentDirectory, "qrcode.png");
        bool loggedIn = false;
        bool expired = false;

        try
        {
            Log("获取登录地址...");
            const string loginUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header";
            using var qrResponse = JsonDocument.Parse(await HTTPUtil.GetWebSourceAsync(loginUrl));
            var qrRoot = qrResponse.RootElement;

            if (!qrRoot.TryGetProperty("code", out var generateCode)
                || generateCode.ValueKind != JsonValueKind.Number
                || generateCode.GetInt32() != 0
                || !qrRoot.TryGetProperty("data", out var qrData))
            {
                string message = qrRoot.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString() ?? "未知原因"
                    : "二维码生成接口返回了无效响应";
                throw new InvalidOperationException($"无法生成 B 站登录二维码：{message}");
            }

            string url = qrData.GetProperty("url").GetString()
                ?? throw new InvalidOperationException("二维码生成接口没有返回二维码地址。");
            string qrcodeKey = qrData.TryGetProperty("qrcode_key", out var keyElement)
                ? keyElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(qrcodeKey))
                qrcodeKey = GetQueryString("qrcode_key", url);
            if (string.IsNullOrWhiteSpace(qrcodeKey))
                throw new InvalidOperationException("二维码地址中没有 qrcode_key。");

            Log("生成二维码...");
            QRCodeGenerator qrGenerator = new();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            PngByteQRCode pngByteCode = new(qrCodeData);
            await File.WriteAllBytesAsync(qrCodePath, pngByteCode.GetGraphic(7));
            Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
            var consoleQRCode = new ConsoleQRCode(qrCodeData);
            consoleQRCode.GetGraphic();

            bool scanNotified = false;
            for (int attempt = 0; attempt < WebQrCodeLifetimeSeconds; attempt++)
            {
                await Task.Delay(1000);
                using var pollResponse = JsonDocument.Parse(await GetLoginStatusAsync(qrcodeKey));
                var pollRoot = pollResponse.RootElement;

                if (!pollRoot.TryGetProperty("code", out var pollApiCode)
                    || pollApiCode.ValueKind != JsonValueKind.Number
                    || pollApiCode.GetInt32() != 0
                    || !pollRoot.TryGetProperty("data", out var pollData)
                    || !pollData.TryGetProperty("code", out var statusElement))
                {
                    string message = pollRoot.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString() ?? "未知原因"
                        : "扫码状态接口返回了无效响应";
                    throw new InvalidOperationException($"查询 B 站扫码状态失败：{message}");
                }

                int status = statusElement.GetInt32();
                switch (status)
                {
                    case 86101: // Waiting for scan.
                        continue;

                    case 86090: // Scanned, waiting for confirmation.
                        if (!scanNotified)
                        {
                            Log("扫码成功, 请在手机上确认...");
                            scanNotified = true;
                        }
                        continue;

                    case 86038:
                        LogWarn("二维码已过期，请重新执行登录指令。");
                        expired = true;
                        break;

                    case 0: // Login completed.
                    {
                        string redirectUrl = pollData.TryGetProperty("url", out var urlElement)
                            ? urlElement.GetString() ?? string.Empty
                            : string.Empty;
                        if (string.IsNullOrWhiteSpace(redirectUrl))
                            throw new InvalidOperationException("B站报告扫码成功，但没有返回 Cookie 跳转地址。");

                        string cookie = ExtractWebCookie(redirectUrl);
                        Log("正在验证登录凭据...");
                        if (!await CheckLogin(cookie))
                        {
                            throw new InvalidOperationException(
                                "二维码已确认，但 B 站导航接口未能验证新 Cookie。凭据未保存；请检查网络或重新扫码。");
                        }

                        await SaveWebCookieAsync(cookie);
                        BBDown.Core.Config.COOKIE = cookie;
                        LogColor($"登录成功：B 站账号已验证，WEB Cookie 已保存至 {Program.USER_DATA_DIR}。");
                        loggedIn = true;
                        break;
                    }

                    default:
                        throw new InvalidOperationException($"B站返回了未知的二维码登录状态码：{status}");
                }

                if (loggedIn || expired)
                    break;
            }

            if (!loggedIn && !expired)
                LogWarn("二维码登录等待超时，请重新执行登录指令。");
        }
        catch (Exception e)
        {
            LogError($"WEB 登录失败：{e.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(qrCodePath))
                    File.Delete(qrCodePath);
            }
            catch (Exception cleanupException)
            {
                LogDebug("清理二维码文件失败：{0}", cleanupException.Message);
            }
        }
    }

    public static async Task LoginTV()
    {
        try
        {
            string loginUrl = "https://passport.snm0516.aisee.tv/x/passport-tv-login/qrcode/auth_code";
            string pollUrl = "https://passport.bilibili.com/x/passport-tv-login/qrcode/poll";
            var parms = GetTVLoginParms();
            Log("获取登录地址...");
            byte[] responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(loginUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
            string web = Encoding.UTF8.GetString(responseArray);
            string url = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("url").ToString();
            string authCode = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("auth_code").ToString();
            Log("生成二维码...");
            QRCodeGenerator qrGenerator = new();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            PngByteQRCode pngByteCode = new(qrCodeData);
            await File.WriteAllBytesAsync("qrcode.png", pngByteCode.GetGraphic(7));
            Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
            var consoleQRCode = new ConsoleQRCode(qrCodeData);
            consoleQRCode.GetGraphic();
            parms.Set("auth_code", authCode);
            parms.Set("ts", GetTimeStamp(true));
            parms.Remove("sign");
            parms.Add("sign", GetSign(ToQueryString(parms)));
            while (true)
            {
                await Task.Delay(1000);
                responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(pollUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
                web = Encoding.UTF8.GetString(responseArray);
                string code = JsonDocument.Parse(web).RootElement.GetProperty("code").ToString();
                if (code == "86038")
                {
                    LogColor("二维码已过期, 请重新执行登录指令.");
                    break;
                }
                else if (code == "86039") //等待扫码
                {
                    continue;
                }
                else
                {
                    string cc = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("access_token").ToString();
                    // Never print access tokens in logs.
                    await File.WriteAllTextAsync(Path.Combine(Program.APP_DIR, "BBDownTV.data"), "access_token=" + cc);
                    Log("TV 登录成功：AccessToken 已保存。");
                    File.Delete("qrcode.png");
                    break;
                }
            }
        }
        catch (Exception e) { LogError(e.Message); }
    }
}
