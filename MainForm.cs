using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Forms;
using VideoDownloader.Downloaders;
using VideoDownloader.Models;
using VideoDownloader.Services;
using VideoDownloader.UI;

namespace VideoDownloader
{
    /// <summary>
    /// 主窗体。系统默认边框（Sizable），深色客户区，简洁现代风。
    /// 拖动/缩放/最小化/最大化由系统标题栏处理，不做自绘标题栏。
    /// </summary>
    public class MainForm : Form
    {
        private readonly DownloadManager _manager = new DownloadManager();
        private readonly Dictionary<string, DownloadTaskCard> _cardMap = new Dictionary<string, DownloadTaskCard>();

        // 顶部输入区
        private Panel _topPanel;
        private TextBox _txtUrl;
        private Label _lblPlaceholder;
        private Label _lblDetected;
        private Button _btnDownload;
        private Button _btnBiliAuth;
        private Button _btnSettings;
        private bool _downloadHover;

        // 任务列表
        private FlowLayoutPanel _flow;
        private Label _lblEmpty;

        // 底部状态栏
        private Panel _statusBar;
        private Label _lblStatusBar;

        private NotifyIcon _notify;

        // ====== DWM 深色标题栏 ======
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public MainForm()
        {
            ConfigService.Load();
            BilibiliAuth.ApplyCookies();

            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            Text = "视频下载器";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(780, 820);
            MinimumSize = new Size(640, 600);
            BackColor = Theme.Bg;
            ForeColor = Theme.TextMain;
            Font = new Font("微软雅黑", 9F);

            BuildUi();

            _manager.OnTaskChanged += OnTaskChanged;
            _manager.OnTaskRemoved += OnTaskRemoved;

            UpdateBiliBadge();
            RefreshEmpty();
            RefreshStatusBar();

            // 抖音解析改用 CDP：每次解析时按需启动系统浏览器，无需在此预初始化。
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 深色标题栏（Win10 20H1+），失败自动降级
            try
            {
                int v = 1;
                DwmSetWindowAttribute(this.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
            }
            catch { }
        }

        #region ====== UI 构建 ======

        private void BuildUi()
        {
            // 顺序：先 Fill，后 Top/Bottom
            BuildFlow();
            BuildTopPanel();
            BuildStatusBar();
            BuildNotifyIcon();
        }

        private void BuildFlow()
        {
            _flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                AutoScroll = true,
                WrapContents = false,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(12, 12, 12, 12)
            };
            Controls.Add(_flow);

            _lblEmpty = new Label
            {
                Dock = DockStyle.Top,
                Height = 200,
                Text = "🎬\n\n粘贴链接开始下载",
                Font = new Font("微软雅黑", 12F),
                ForeColor = Theme.TextSub,
                TextAlign = ContentAlignment.TopCenter,
                BackColor = Color.Transparent
            };
            _flow.Controls.Add(_lblEmpty);
        }

        private void BuildTopPanel()
        {
            _topPanel = new Panel { Dock = DockStyle.Top, Height = 150, BackColor = Theme.Bg, Padding = new Padding(12) };
            Controls.Add(_topPanel);
            _topPanel.BringToFront();

            // 输入框
            _txtUrl = new TextBox
            {
                Multiline = true,
                Location = new Point(12, 12),
                Size = new Size(600, 40),
                BackColor = Theme.InputBg,
                ForeColor = Theme.TextMain,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("微软雅黑", 10F)
            };
            _txtUrl.TextChanged += TxtUrl_TextChanged;
            _topPanel.Controls.Add(_txtUrl);

            _lblPlaceholder = new Label
            {
                AutoSize = false,
                Location = new Point(24, 22),
                Size = new Size(560, 20),
                ForeColor = Theme.TextSub,
                BackColor = Color.Transparent,
                Text = "粘贴抖音/B站/快手分享链接，支持带文案自动识别"
            };
            _topPanel.Controls.Add(_lblPlaceholder);

            _btnDownload = new Button
            {
                Text = "开始下载",
                Size = new Size(140, 40),
                Location = new Point(620, 12),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Accent,
                ForeColor = Color.White,
                Font = new Font("微软雅黑", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnDownload.FlatAppearance.BorderSize = 0;
            _btnDownload.Click += async (s, e) => await BtnDownload_Click();
            _btnDownload.MouseEnter += (s, e) => { _downloadHover = true; _btnDownload.Invalidate(); };
            _btnDownload.MouseLeave += (s, e) => { _downloadHover = false; _btnDownload.Invalidate(); };
            _btnDownload.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var c = _btnDownload.Enabled ? (_downloadHover ? Theme.AccentHover : Theme.Accent) : Color.FromArgb(0x55, 0x55, 0x55);
                using (var b = new SolidBrush(c)) g.FillRectangle(b, _btnDownload.ClientRectangle);
                TextRenderer.DrawText(g, _btnDownload.Text, _btnDownload.Font,
                    _btnDownload.ClientRectangle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            _topPanel.Controls.Add(_btnDownload);

            // 识别提示
            _lblDetected = new Label
            {
                AutoSize = false,
                Location = new Point(12, 58),
                Size = new Size(600, 18),
                ForeColor = Color.FromArgb(0x6C, 0xC0, 0xFF),
                BackColor = Color.Transparent,
                Font = new Font("微软雅黑", 8.5F),
                Text = ""
            };
            _topPanel.Controls.Add(_lblDetected);

            // 按钮行：登录B站 + 设置
            _btnBiliAuth = new Button
            {
                FlatStyle = FlatStyle.Flat,
                Size = new Size(120, 28),
                Location = new Point(12, 90),
                Cursor = Cursors.Hand,
                Font = new Font("微软雅黑", 9F),
                BackColor = Theme.InputBg,
                ForeColor = Theme.TextSub
            };
            _btnBiliAuth.FlatAppearance.BorderSize = 0;
            _btnBiliAuth.Click += BtnBiliAuth_Click;
            _topPanel.Controls.Add(_btnBiliAuth);

            _btnSettings = new Button
            {
                FlatStyle = FlatStyle.Flat,
                Size = new Size(90, 28),
                Location = new Point(140, 90),
                Cursor = Cursors.Hand,
                Text = "⚙ 设置",
                Font = new Font("微软雅黑", 9F),
                BackColor = Theme.InputBg,
                ForeColor = Theme.TextSub
            };
            _btnSettings.FlatAppearance.BorderSize = 0;
            _btnSettings.Click += (s, e) =>
            {
                using (var f = new SettingsForm())
                {
                    if (f.ShowDialog(this) == DialogResult.OK)
                        ShowBalloon("设置已保存", "下载目录已更新");
                }
            };
            _topPanel.Controls.Add(_btnSettings);
        }

        private void BuildStatusBar()
        {
            _statusBar = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = Color.FromArgb(0x14, 0x14, 0x14) };
            Controls.Add(_statusBar);
            _lblStatusBar = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Theme.TextSub,
                Font = new Font("微软雅黑", 8.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Text = "就绪"
            };
            _statusBar.Controls.Add(_lblStatusBar);
        }

        private void BuildNotifyIcon()
        {
            _notify = new NotifyIcon
            {
                Visible = true,
                Icon = SystemIcons.Application,
                Text = "视频下载器"
            };
        }

        #endregion

        #region ====== 输入区交互 ======

        private void TxtUrl_TextChanged(object sender, EventArgs e)
        {
            _lblPlaceholder.Visible = string.IsNullOrEmpty(_txtUrl.Text);
            string input = _txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(input)) { _lblDetected.Text = ""; return; }
            string url = DownloadManager.ExtractUrl(input);
            string plat = VideoDownloaderBase.DetectPlatform(url ?? input);
            if (plat != null)
            {
                _lblDetected.ForeColor = Theme.PlatformColor(plat);
                _lblDetected.Text = "✓ 识别到 " + plat + " 链接";
            }
            else
            {
                _lblDetected.ForeColor = Theme.TextSub;
                _lblDetected.Text = "未识别到支持的平台（抖音 / B站 / 快手）";
            }
        }

        private async System.Threading.Tasks.Task BtnDownload_Click()
        {
            string input = _txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(input))
            {
                ShowBalloon("请先粘贴视频链接", "");
                _lblDetected.ForeColor = Theme.Danger;
                _lblDetected.Text = "⚠ 请先粘贴视频链接";
                return;
            }
            _btnDownload.Enabled = false;
            _btnDownload.Text = "解析中...";
            _btnDownload.Invalidate();
            try
            {
                await System.Threading.Tasks.Task.Yield();
                _manager.Enqueue(input);
                _txtUrl.Clear();
                _lblDetected.Text = "";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法添加任务",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _btnDownload.Enabled = true;
                _btnDownload.Text = "开始下载";
                _btnDownload.Invalidate();
            }
        }

        #endregion

        #region ====== B站登录 ======

        private void UpdateBiliBadge()
        {
            if (ConfigService.IsBiliLoggedIn)
            {
                string name = string.IsNullOrEmpty(ConfigService.Current.BiliUserName) ? "已登录" : ConfigService.Current.BiliUserName;
                _btnBiliAuth.Text = "✓ B站：" + Truncate(name, 6);
                _btnBiliAuth.BackColor = Color.FromArgb(0x2E, 0x5D, 0x43);
                _btnBiliAuth.ForeColor = Color.FromArgb(0x7D, 0xE8, 0xA8);
                _btnBiliAuth.Tag = "in";
            }
            else
            {
                _btnBiliAuth.Text = "登录 B站";
                _btnBiliAuth.BackColor = Theme.InputBg;
                _btnBiliAuth.ForeColor = Theme.TextSub;
                _btnBiliAuth.Tag = "out";
            }
        }

        private async void BtnBiliAuth_Click(object sender, EventArgs e)
        {
            if ((_btnBiliAuth.Tag as string) == "in")
            {
                if (MessageBox.Show(this, "确定要退出 B站登录吗？", "提示",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    BilibiliAuth.Logout();
                    UpdateBiliBadge();
                    ShowBalloon("已退出 B站登录", "");
                }
                return;
            }
            try
            {
                using (var f = new LoginForm())
                {
                    if (f.ShowDialog(this) == DialogResult.OK)
                    {
                        BilibiliAuth.SaveCookies(f.SessData, f.BiliJct, f.DedeUserId, f.UserName);
                        UpdateBiliBadge();
                        ShowBalloon("B站登录成功", f.UserName);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "登录失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            await System.Threading.Tasks.Task.CompletedTask;
        }

        #endregion

        #region ====== 任务列表事件 ======

        private void OnTaskChanged(DownloadTask task)
        {
            if (IsDisposed) return;
            try { if (IsHandleCreated) BeginInvoke(new Action(() => { UpdateCard(task); RefreshStatusBar(); })); }
            catch { }
        }

        private void OnTaskRemoved(DownloadTask task)
        {
            if (IsDisposed) return;
            try { if (IsHandleCreated) BeginInvoke(new Action(() => { RemoveCard(task); RefreshStatusBar(); })); }
            catch { }
        }

        private void UpdateCard(DownloadTask task)
        {
            if (!_cardMap.TryGetValue(task.Id, out var card))
            {
                card = new DownloadTaskCard(task);
                card.Width = _flow.ClientSize.Width - 30;
                card.PlayClicked += OnPlay;
                card.FolderClicked += OnFolder;
                card.DeleteClicked += OnDelete;
                card.CancelClicked += OnCancel;
                card.RetryClicked += OnRetry;
                _cardMap[task.Id] = card;
                _flow.Controls.Add(card);
            }
            card.RefreshFromTask();
            RefreshEmpty();
            if (task.Status == DownloadStatus.Completed)
                ShowBalloon("下载完成：" + (task.Video?.Title ?? ""), task.FilePath);
        }

        private void RemoveCard(DownloadTask task)
        {
            if (_cardMap.TryGetValue(task.Id, out var card))
            {
                _flow.Controls.Remove(card);
                card.Dispose();
                _cardMap.Remove(task.Id);
            }
            RefreshEmpty();
        }

        private void RefreshEmpty()
        {
            _lblEmpty.Visible = _cardMap.Count == 0;
        }

        private void OnPlay(DownloadTask task)
        {
            try
            {
                if (!string.IsNullOrEmpty(task.FilePath) && File.Exists(task.FilePath))
                    Process.Start(new ProcessStartInfo(task.FilePath) { UseShellExecute = true });
                else
                    MessageBox.Show(this, "文件不存在。", "提示");
            }
            catch (Exception ex) { MessageBox.Show(this, "打开失败：" + ex.Message, "错误"); }
        }

        private void OnFolder(DownloadTask task)
        {
            try
            {
                if (!string.IsNullOrEmpty(task.FilePath) && File.Exists(task.FilePath))
                    Process.Start("explorer.exe", "/select,\"" + task.FilePath + "\"");
                else
                {
                    string dir = ConfigService.EffectiveDownloadDir;
                    Directory.CreateDirectory(dir);
                    Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "打开失败：" + ex.Message, "错误"); }
        }

        private void OnDelete(DownloadTask task)
        {
            bool busy = task.Status == DownloadStatus.Downloading ||
                        task.Status == DownloadStatus.Resolving ||
                        task.Status == DownloadStatus.Queued;
            if (busy)
            {
                _manager.Cancel(task);
                _manager.Remove(task, false);
                ShowBalloon("已取消并移除", "");
                return;
            }
            bool hasFile = !string.IsNullOrEmpty(task.FilePath) && File.Exists(task.FilePath);
            string msg = hasFile ? "确定删除这个视频吗？文件将被永久删除。" : "确定从列表移除这个任务吗？";
            if (MessageBox.Show(this, msg, "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                bool ok = _manager.Remove(task, hasFile);
                ShowBalloon(ok ? "已删除" : "删除失败", "");
            }
        }

        private void OnCancel(DownloadTask task) => _manager.Cancel(task);

        private void OnRetry(DownloadTask task)
        {
            try
            {
                _manager.Remove(task, false);
                _manager.Enqueue(task.RawInput);
                ShowBalloon("已重新添加", "");
            }
            catch (Exception ex) { MessageBox.Show(this, "重试失败：" + ex.Message, "错误"); }
        }

        #endregion

        #region ====== 状态栏 / 通知 ======

        private void RefreshStatusBar()
        {
            if (_lblStatusBar == null) return;
            var tasks = _manager.GetTasks();
            int total = tasks.Count, done = 0, fail = 0, busy = 0;
            foreach (var t in tasks)
            {
                switch (t.Status)
                {
                    case DownloadStatus.Completed: done++; break;
                    case DownloadStatus.Failed: fail++; break;
                    case DownloadStatus.Downloading:
                    case DownloadStatus.Resolving:
                    case DownloadStatus.Queued:
                    case DownloadStatus.Merging: busy++; break;
                }
            }
            _lblStatusBar.Text = string.Format(
                "共 {0} | 进行 {1} | 完成 {2} | 失败 {3}  ·  保存到：{4}",
                total, busy, done, fail, ConfigService.EffectiveDownloadDir);
        }

        private void ShowBalloon(string title, string text)
        {
            try
            {
                _notify.BalloonTipTitle = title;
                _notify.BalloonTipText = string.IsNullOrEmpty(text) ? " " : text;
                _notify.ShowBalloonTip(2500);
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { _notify.Visible = false; } catch { }
            base.OnFormClosing(e);
        }

        #endregion

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_flow == null) return;
            foreach (Control c in _flow.Controls)
                if (c is DownloadTaskCard) c.Width = _flow.ClientSize.Width - 30;
        }
    }
}
