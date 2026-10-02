using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VideoDownloader.Models;
using VideoDownloader.Utils;

namespace VideoDownloader.Downloaders
{
    /// <summary>
    /// 抖音下载器（多策略兜底）。
    /// 解析顺序：
    ///   A. 移动端 iesdouyin iteminfo 接口（免签名）
    ///   B. 桌面 web detail 接口（Cookie 预热后）
    ///   C. 视频页 HTML RENDER_DATA 提取
    ///   D. 第三方 api.douyin.wtf 兜底
    /// 每个策略失败都把 HTTP 状态码 + 响应前 1000 字符写入日志。
    /// </summary>
    public class DouyinDownloader : VideoDownloaderBase
    {
        protected override string GetReferer() => "https://www.douyin.com/";

        public override async Task<VideoInfo> ResolveAsync(string inputUrl, CancellationToken ct)
        {
            Log("正在解析抖音分享链接...");
            string awemeId = await ExtractAwemeIdAsync(inputUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(awemeId))
                throw new Exception("未能从链接中解析出抖音视频 ID，请确认链接是否正确。");
            Log("视频 ID：" + awemeId);

            string title = null;
            string bestUrl = null;
            string bestQuality = null;

            // 方案 0（最高优先级）：CDP 启动系统真实 Chromium/Edge，视频页上下文 fetch。
            // 这是成功率最高的方案（与 Python playwright 同原理），直接返回完整 VideoInfo。
            try
            {
                Log("[0] 启动真实浏览器（CDP）请求 detail API...");
                var cdpResolver = new Services.DouyinCdpResolver();
                var cdpInfo = await cdpResolver.ResolveAsync(awemeId, this.Log, ct).ConfigureAwait(false);
                Log("[0] 成功，已自动选择最高清晰度");
                return cdpInfo;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log("[0] CDP 失败：" + ex.Message);
                Log("[0] 回退到 HttpClient 兜底方案...");
            }

            // 方案 A：移动端 iesdouyin iteminfo
            try
            {
                Log("[A] 尝试移动端 iesdouyin iteminfo 接口...");
                var (t, u, q) = await StrategyA_IesDouyinAsync(awemeId, ct).ConfigureAwait(false);
                if (u != null) { title = t; bestUrl = u; bestQuality = q; Log("[A] 成功"); }
            }
            catch (Exception ex) { Log("[A] 失败：" + ex.Message); }

            // 方案 B：web detail + 移动端 UA + Cookie 预热
            if (bestUrl == null)
            {
                try
                {
                    Log("[B] 预热 Cookie 后尝试 web detail 接口...");
                    await WarmupCookieAsync(ct).ConfigureAwait(false);
                    var (t, u, q) = await StrategyB_WebDetailAsync(awemeId, ct).ConfigureAwait(false);
                    if (u != null) { title = t; bestUrl = u; bestQuality = q; Log("[B] 成功"); }
                }
                catch (Exception ex) { Log("[B] 失败：" + ex.Message); }
            }

            // 方案 C：HTML RENDER_DATA
            if (bestUrl == null)
            {
                try
                {
                    Log("[C] 尝试视频页 HTML RENDER_DATA 提取...");
                    var (t, u, q) = await StrategyC_RenderDataAsync(awemeId, ct).ConfigureAwait(false);
                    if (u != null) { title = t; bestUrl = u; bestQuality = q; Log("[C] 成功"); }
                }
                catch (Exception ex) { Log("[C] 失败：" + ex.Message); }
            }

            // 方案 D：第三方 API
            if (bestUrl == null)
            {
                try
                {
                    Log("[D] 尝试第三方解析接口...");
                    var (t, u, q) = await StrategyD_ThirdPartyAsync(inputUrl, awemeId, ct).ConfigureAwait(false);
                    if (u != null) { title = t; bestUrl = u; bestQuality = q; Log("[D] 成功"); }
                }
                catch (Exception ex) { Log("[D] 失败：" + ex.Message); }
            }

            if (bestUrl == null)
                throw new Exception("未能解析到可下载的视频直链（A/B/C/D 四种方案均失败，可能需要登录或被风控）。");

            if (string.IsNullOrEmpty(title)) title = "抖音_" + awemeId;
            return new VideoInfo
            {
                Title = title,
                VideoUrl = bestUrl,
                AudioUrl = null,
                Platform = "抖音",
                Quality = string.IsNullOrEmpty(bestQuality) ? "标准" : bestQuality,
                FileName = SanitizeFileName(title) + ".mp4"
            };
        }

        // ========== 方案 A：移动端 iesdouyin ==========
        private async Task<(string title, string url, string quality)> StrategyA_IesDouyinAsync(
            string awemeId, CancellationToken ct)
        {
            string api = "https://www.iesdouyin.com/web/api/v2/aweme/iteminfo/?item_ids=" + awemeId;
            using (var resp = await HttpHelper.GetAsync(api,
                userAgent: HttpHelper.MobileiPhoneUA,
                referer: "https://www.iesdouyin.com/").ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Log("[A] HTTP " + (int)resp.StatusCode + "，body 长度 " + (body?.Length ?? 0));
                if (!resp.IsSuccessStatusCode) return (null, null, null);
                if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith("{"))
                {
                    Log("[A] 非 JSON 响应前1000字符：" + Preview(body, 1000));
                    return (null, null, null);
                }
                var root = JObject.Parse(body);
                var list = root["item_list"] as JArray;
                if (list == null || list.Count == 0) return (null, null, null);
                var item = list[0] as JObject;
                string title = (string)item["desc"];

                string bestUrl = null; long bestBw = -1; string q = null;
                var brs = item["video"]?["bit_rate"] as JArray;
                if (brs != null)
                {
                    foreach (var br in brs)
                    {
                        long bw = (long?)br["bit_rate"] ?? 0;
                        var urls = br["play_addr"]?["url_list"] as JArray;
                        if (urls == null || urls.Count == 0) continue;
                        string u = (string)urls[0];
                        if (string.IsNullOrEmpty(u)) continue;
                        if (bw > bestBw) { bestBw = bw; bestUrl = u; q = (string)br["gear_name"]; }
                    }
                }
                if (bestUrl == null)
                {
                    var urls = item["video"]?["play_addr"]?["url_list"] as JArray;
                    if (urls != null && urls.Count > 0) bestUrl = (string)urls[0];
                }
                return (title, bestUrl, q);
            }
        }

        // ========== 方案 B：web detail + 移动端 UA ==========
        private async Task<(string title, string url, string quality)> StrategyB_WebDetailAsync(
            string awemeId, CancellationToken ct)
        {
            string api = "https://www.douyin.com/aweme/v1/web/aweme/detail/?aweme_id=" + awemeId + "&aid=6383";
            using (var resp = await HttpHelper.GetAsync(api,
                userAgent: HttpHelper.MobileiPhoneUA,
                referer: "https://www.douyin.com/").ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Log("[B] HTTP " + (int)resp.StatusCode + "，body 长度 " + (body?.Length ?? 0));
                if (!resp.IsSuccessStatusCode) return (null, null, null);
                if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith("{"))
                {
                    Log("[B] 非 JSON 响应前1000字符：" + Preview(body, 1000));
                    return (null, null, null);
                }
                var root = JObject.Parse(body);
                var detail = root["aweme_detail"] as JObject;
                if (detail == null) return (null, null, null);
                string title = (string)detail["desc"];
                string bestUrl = null; long bestBw = -1; string q = null;
                var brs = detail["video"]?["bit_rate"] as JArray;
                if (brs != null)
                {
                    foreach (var br in brs)
                    {
                        long bw = (long?)br["bit_rate"] ?? 0;
                        var urls = br["play_addr"]?["url_list"] as JArray;
                        if (urls == null || urls.Count == 0) continue;
                        string u = (string)urls[0];
                        if (string.IsNullOrEmpty(u)) continue;
                        if (bw > bestBw) { bestBw = bw; bestUrl = u; q = (string)br["gear_name"]; }
                    }
                }
                return (title, bestUrl, q);
            }
        }

        // ========== 方案 C：HTML RENDER_DATA ==========
        private async Task<(string title, string url, string quality)> StrategyC_RenderDataAsync(
            string awemeId, CancellationToken ct)
        {
            string page = "https://www.douyin.com/video/" + awemeId;
            using (var resp = await HttpHelper.GetAsync(page,
                userAgent: HttpHelper.DesktopChromeUA,
                referer: "https://www.douyin.com/").ConfigureAwait(false))
            {
                string html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Log("[C] HTTP " + (int)resp.StatusCode + "，HTML 长度 " + (html?.Length ?? 0));
                if (!resp.IsSuccessStatusCode || string.IsNullOrEmpty(html)) return (null, null, null);

                var m = Regex.Match(html,
                    @"<script id=""RENDER_DATA"" type=""application/json"">([^<]+)</script>",
                    RegexOptions.Singleline);
                if (!m.Success)
                {
                    Log("[C] 未找到 RENDER_DATA，HTML 前1000字符：" + Preview(html, 1000));
                    return (null, null, null);
                }
                string raw = Uri.UnescapeDataString(m.Groups[1].Value);
                JObject root;
                try { root = JObject.Parse(raw); }
                catch (Exception ex) { Log("[C] RENDER_DATA JSON 解析失败：" + ex.Message); return (null, null, null); }

                var item = root.SelectToken("app.videoInfoRes.item_list[0]") as JObject;
                if (item == null) item = FindFirstItem(root);
                if (item == null) return (null, null, null);

                string title = (string)item["desc"];
                string bestUrl = null; long bestBw = -1; string q = null;
                var brs = item["video"]?["bit_rate"] as JArray;
                if (brs != null)
                {
                    foreach (var br in brs)
                    {
                        long bw = (long?)br["bit_rate"] ?? 0;
                        var urls = br["play_addr"]?["url_list"] as JArray;
                        if (urls == null || urls.Count == 0) continue;
                        string u = (string)urls[0];
                        if (string.IsNullOrEmpty(u)) continue;
                        if (bw > bestBw) { bestBw = bw; bestUrl = u; q = (string)br["gear_name"]; }
                    }
                }
                return (title, bestUrl, q);
            }
        }

        private static JObject FindFirstItem(JToken token)
        {
            if (token is JObject jobj)
            {
                if (jobj["video"]?["bit_rate"] is JArray arr && arr.Count > 0) return jobj;
                foreach (var p in jobj.Properties())
                {
                    var r = FindFirstItem(p.Value);
                    if (r != null) return r;
                }
            }
            else if (token is JArray jarr)
            {
                foreach (var it in jarr)
                {
                    var r = FindFirstItem(it);
                    if (r != null) return r;
                }
            }
            return null;
        }

        // ========== 方案 D：第三方 API ==========
        private async Task<(string title, string url, string quality)> StrategyD_ThirdPartyAsync(
            string inputUrl, string awemeId, CancellationToken ct)
        {
            string target = inputUrl.Trim();
            string api = "https://api.douyin.wtf/api?url=" + Uri.EscapeDataString(target);
            using (var resp = await HttpHelper.GetAsync(api,
                userAgent: HttpHelper.DesktopChromeUA,
                referer: "https://www.douyin.wtf/").ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Log("[D] HTTP " + (int)resp.StatusCode + "，body 长度 " + (body?.Length ?? 0));
                if (!resp.IsSuccessStatusCode) return (null, null, null);
                if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith("{"))
                {
                    Log("[D] 非 JSON 响应前1000字符：" + Preview(body, 1000));
                    return (null, null, null);
                }
                var root = JObject.Parse(body);
                string url = (string)root["nwm_video_url_HQ"] ?? (string)root["nwm_video_url"];
                string title = (string)root["video_title"] ?? (string)root["desc"];
                return (title, url, "无水印");
            }
        }

        private static string Preview(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        private async Task WarmupCookieAsync(CancellationToken ct)
        {
            try
            {
                using (var resp = await HttpHelper.GetAsync("https://www.douyin.com/",
                    userAgent: HttpHelper.DesktopChromeUA,
                    referer: "https://www.douyin.com/").ConfigureAwait(false))
                {
                    await resp.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Log("Cookie 预热失败（忽略）：" + ex.Message); }
        }

        private async Task<string> ExtractAwemeIdAsync(string input, CancellationToken ct)
        {
            string url = input.Trim();
            var m = Regex.Match(url, @"/video/(\d+)");
            if (m.Success) return m.Groups[1].Value;

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", HttpHelper.DesktopChromeUA);
                using (var resp = await HttpHelper.Client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    string finalUrl = resp.RequestMessage.RequestUri.ToString();
                    m = Regex.Match(finalUrl, @"/video/(\d+)");
                    if (m.Success) return m.Groups[1].Value;
                    m = Regex.Match(finalUrl, @"modal_id=(\d+)");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            return null;
        }
    }
}
