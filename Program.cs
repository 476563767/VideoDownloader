using System;
using System.IO;
using System.Windows.Forms;

namespace VideoDownloader
{
    internal static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 全局异常日志文件
            string logFile = null;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VideoDownloader");
                Directory.CreateDirectory(dir);
                logFile = Path.Combine(dir, "crash.log");
            }
            catch { }

            // UI 线程未捕获异常：弹框 + 写日志文件
            Application.ThreadException += (s, e) =>
            {
                string msg = "发生未处理的错误：\n\n" + e.Exception;
                WriteLog(logFile, msg);
                MessageBox.Show(msg, "视频下载器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            // 非 UI 线程未捕获异常
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                string msg = "发生致命错误：\n\n" + (ex?.ToString() ?? e.ExceptionObject.ToString());
                WriteLog(logFile, msg);
                MessageBox.Show(msg, "视频下载器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new MainForm());
        }

        private static void WriteLog(string path, string content)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.AppendAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "]\n" + content + "\n\n"); }
            catch { }
        }
    }
}
