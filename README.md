# 视频下载器 (Video Downloader) - Windows 版

支持 **抖音、B站、快手** 三个平台的 Windows 桌面视频下载器，免登录自动选最高码率下载。

## 功能特性

- 支持平台：抖音 / B站 / 快手
- 自动识别分享链接（支持带文案的分享文本）
- 自动选择最高码率下载
- B站 DASH 音视频分离下载 + 合并
- 实时下载速度 / 剩余时间显示
- B站扫码登录解锁 1080P
- 下载完成后文件定位 / 删除 / 重试
- 深色主题界面

## 技术栈

- C# / .NET 8 (`net8.0-windows`)
- WinForms
- Newtonsoft.Json
- Microsoft.Web.WebView2（B站登录）
- CDP（Chrome DevTools Protocol）启动系统 Edge/Chrome 解析抖音

## 抖音解析原理

使用 CDP 启动系统真实 Edge/Chrome（无头模式），在视频页上下文执行 fetch detail API，抖音 SDK 自动生成 `a_bogus` 签名。

```
detail API: /aweme/v1/web/aweme/detail/?aweme_id=<id>&aid=6383
成功响应: aweme_detail.video.bit_rate[]（选最高 bit_rate）
```

纯 HTTP 请求会被风控拦截（TLS 指纹 + a_bogus 环境绑定），必须用真实浏览器。

## 构建

```bash
cd VideoDownloader
dotnet publish VideoDownloader.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

产物：`bin/Release/net8.0-windows/win-x64/publish/VideoDownloader.exe`（单文件自包含，约 69MB）

## 项目结构

```
VideoDownloader/
├── Program.cs                  # 入口
├── VideoDownloader.csproj      # 工程文件
├── MainForm.cs                 # 主窗口
├── Resources/
│   └── stealth.js              # 反检测脚本（嵌入资源）
├── Models/
│   ├── VideoInfo.cs            # 视频信息模型
│   └── DownloadTask.cs         # 下载任务模型
├── Downloaders/
│   ├── VideoDownloaderBase.cs  # 下载器基类
│   ├── DouyinDownloader.cs     # 抖音（多策略兜底）
│   ├── BilibiliDownloader.cs   # B站 DASH
│   └── KuaishouDownloader.cs   # 快手
├── Services/
│   ├── DownloadManager.cs      # 任务队列/并发/速度
│   ├── DouyinCdpResolver.cs    # 抖音 CDP 主方案
│   ├── DouyinWebView2Resolver.cs # 抖音 WebView2 备用
│   ├── CdpBrowser.cs           # CDP 协议封装
│   ├── BrowserLocator.cs       # 查找系统 Edge/Chrome
│   ├── BilibiliAuth.cs         # B站 Cookie 持久化
│   └── ConfigService.cs        # 配置
├── UI/
│   ├── DownloadTaskCard.cs     # 任务卡片
│   ├── LoginForm.cs            # B站扫码登录
│   ├── SettingsForm.cs         # 设置
│   └── Theme.cs                # 配色主题
└── Utils/
    └── HttpHelper.cs           # HTTP 工具
```

## 已知问题

- 抖音 CDP 无头浏览器可能被风控返回 403（抖音对 CDP 协议有检测）
- 首次粘贴任务卡片可能空白（CDP 浏览器冷启动时序问题）

## 免责声明

仅供学习交流使用，请遵守各平台用户协议和相关法律法规。
