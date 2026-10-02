using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoDownloader.Models;
using VideoDownloader.Utils;

namespace VideoDownloader.Downloaders
{
    /// <summary>
    /// 下载进度回调数据。UI 层用这个对象刷新进度条、速度、大小显示。
    /// 扩展：B站 DASH 时额外携带视频流/音频流各自的进度。
    /// </summary>
    public class DownloadProgress
    {
        /// <summary>已下载字节数（B站为视频+音频合计）</summary>
        public long BytesReceived { get; set; }
        /// <summary>总字节数（未知时为 -1）</summary>
        public long TotalBytes { get; set; }
        /// <summary>瞬时速度（字节/秒）。新版由 UI 层滑动窗口计算，这里保留兼容字段</summary>
        public double BytesPerSecond { get; set; }

        // ===== B站 DASH 双进度 =====
        /// <summary>视频流已下载字节（非 B站为 0）</summary>
        public long VideoBytesReceived { get; set; }
        /// <summary>视频流总字节（非 B站为 0）</summary>
        public long VideoTotalBytes { get; set; }
        /// <summary>音频流已下载字节（非 B站为 0）</summary>
        public long AudioBytesReceived { get; set; }
        /// <summary>音频流总字节（非 B站为 0）</summary>
        public long AudioTotalBytes { get; set; }

        /// <summary>0~100，总大小未知时按已下载量估算显示</summary>
        public int Percent => TotalBytes > 0
            ? (int)Math.Min(100, BytesReceived * 100 / TotalBytes)
            : 0;
    }

    /// <summary>
    /// 所有平台下载器的基类。
    /// 负责：
    /// 1. 平台识别（识别输入链接属于哪个平台）
    /// 2. 解析视频信息（抽象方法，子类实现）
    /// 3. 实际下载文件（断点续传 + 进度回调 + 取消）
    /// </summary>
    public abstract class VideoDownloaderBase
    {
        /// <summary>下载目录：默认桌面/视频下载，可由 ConfigService 在启动时覆盖</summary>
        public static string DownloadDir { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "视频下载");

        /// <summary>日志回调（由 DownloadManager 注入），子类用它输出步骤日志</summary>
        public Action<string> LogCallback { get; set; }

        /// <summary>写日志</summary>
        protected void Log(string line) => LogCallback?.Invoke(line);

        /// <summary>
        /// 识别链接属于哪个平台。返回平台名，识别不出来返回 null。
        /// </summary>
        public static string DetectPlatform(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string url = input.Trim();

            // 抖音：短链 v.douyin.com 或 www.douyin.com
            if (url.IndexOf("douyin.com", StringComparison.OrdinalIgnoreCase) >= 0)
                return "抖音";

            // 快手：短链 v.kuaishou.com 或 www.kuaishou.com / m.gifshow.com
            if (url.IndexOf("kuaishou.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("gifshow.com", StringComparison.OrdinalIgnoreCase) >= 0)
                return "快手";

            // B站：bilibili.com，且能匹配到 BV 号
            if (url.IndexOf("bilibili.com", StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(url, @"BV[0-9A-Za-z]{10}"))
                return "B站";

            return null;
        }

        /// <summary>
        /// 工厂方法：根据平台名返回对应的下载器实例。
        /// </summary>
        public static VideoDownloaderBase Create(string platform)
        {
            switch (platform)
            {
                case "抖音": return new DouyinDownloader();
                case "快手": return new KuaishouDownloader();
                case "B站":  return new BilibiliDownloader();
                default:
                    throw new NotSupportedException("不支持的平台：" + platform);
            }
        }

        /// <summary>
        /// 子类实现：从用户输入的链接解析出 VideoInfo。
        /// </summary>
        public abstract Task<VideoInfo> ResolveAsync(string inputUrl, CancellationToken ct);

        /// <summary>
        /// 下载视频到本地。支持断点续传、进度回调、取消。
        /// 对于 B站这种音视频分离的情况，子类重写这个方法做合并。
        /// </summary>
        public virtual async Task<string> DownloadAsync(
            VideoInfo info,
            IProgress<DownloadProgress> progress,
            CancellationToken ct)
        {
            // 确保下载目录存在
            Directory.CreateDirectory(DownloadDir);
            string savePath = Path.Combine(DownloadDir, SanitizeFileName(info.FileName));

            // 普通平台（抖音/快手）：音视频合一，直接下载 VideoUrl
            await DownloadSingleFileAsync(info.VideoUrl, savePath,
                referer: GetReferer(),
                onProgress: (recv, total) => progress?.Report(new DownloadProgress
                {
                    BytesReceived = recv,
                    TotalBytes = total
                }),
                ct: ct).ConfigureAwait(false);

            return savePath;
        }

        /// <summary>每个平台自己的 Referer，下载直链时带上防盗链校验</summary>
        protected abstract string GetReferer();

        /// <summary>
        /// 下载单个文件，支持断点续传。
        /// 实现思路：
        /// 1. 先看本地是否已有部分文件，拿到已下载长度 existingLen
        /// 2. 发一个带 Range: bytes=existingLen- 的请求
        /// 3. 如果服务器返回 206（部分内容），就接着追加写；
        ///    如果返回 200（不支持断点），就从头覆盖写
        /// 4. 边下边统计，每 500ms 通过 onProgress 回调一次 (已下载, 总大小)
        /// </summary>
        protected async Task DownloadSingleFileAsync(
            string url,
            string savePath,
            string referer,
            Action<long, long> onProgress,
            CancellationToken ct)
        {
            long existingLen = 0;
            bool serverSupportsRange = false;

            // 检查本地已有文件大小
            if (File.Exists(savePath))
            {
                var fi = new FileInfo(savePath);
                existingLen = fi.Length;
            }

            // 构造请求
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", HttpHelper.DesktopChromeUA);
                if (!string.IsNullOrEmpty(referer))
                    request.Headers.Referrer = new Uri(referer);

                // 断点续传：从 existingLen 开始
                if (existingLen > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existingLen, null);
                }

                using (var response = await HttpHelper.Client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    // 206 = 服务器支持断点续传，我们从断点继续
                    // 200  = 服务器忽略了 Range，返回完整内容，需要从头覆盖
                    serverSupportsRange = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                    if (!serverSupportsRange && existingLen > 0)
                    {
                        existingLen = 0; // 重置，从头下载
                    }

                    // 总大小：Content-Range 里有完整长度；否则用 Content-Length
                    long totalBytes = -1;
                    if (response.Content.Headers.ContentRange?.Length != null)
                    {
                        totalBytes = response.Content.Headers.ContentRange.Length.Value;
                    }
                    else if (response.Content.Headers.ContentLength.HasValue)
                    {
                        totalBytes = response.Content.Headers.ContentLength.Value + existingLen;
                    }

                    // 打开文件流：续传用 Append，新下载用 Create
                    var fileMode = serverSupportsRange && existingLen > 0
                        ? FileMode.Append
                        : FileMode.Create;

                    using (var fs = new FileStream(savePath, fileMode, FileAccess.Write, FileShare.None))
                    using (var netStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        var buffer = new byte[81920]; // 80KB 缓冲区
                        long downloaded = existingLen;
                        var lastReportTime = DateTime.UtcNow;

                        onProgress?.Invoke(downloaded, totalBytes);

                        int read;
                        while ((read = await netStream.ReadAsync(buffer, 0, buffer.Length, ct)
                                                .ConfigureAwait(false)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                            downloaded += read;

                            // 每 500ms 回调一次进度，避免 UI 刷新太频繁
                            var now = DateTime.UtcNow;
                            if ((now - lastReportTime).TotalMilliseconds >= 500)
                            {
                                onProgress?.Invoke(downloaded, totalBytes);
                                lastReportTime = now;
                            }
                        }

                        // 最终回调一次，确保 UI 显示 100%
                        onProgress?.Invoke(downloaded, totalBytes > 0 ? totalBytes : downloaded);
                    }
                }
            }
        }

        /// <summary>
        /// 清理文件名：去掉 Windows 不允许的字符 \ / : * ? " &lt; &gt; |
        /// </summary>
        protected static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                name = "video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            // 去掉首尾空格和点
            name = name.Trim(' ', '.');
            if (name.Length > 120) name = name.Substring(0, 120);
            return name;
        }

        /// <summary>
        /// 从 HTML 中正则提取第一个匹配组。工具方法，子类解析页面时复用。
        /// </summary>
        protected static string RegexMatch(string input, string pattern, int group = 1)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var m = Regex.Match(input, pattern, RegexOptions.Singleline);
            return m.Success ? m.Groups[group].Value : null;
        }
    }
}
