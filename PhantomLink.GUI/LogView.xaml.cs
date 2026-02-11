using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class LogView : UserControl
    {
        private readonly object _bufferLock = new object();
        private readonly List<string> _buffer = new List<string>(256);
        private DispatcherTimer _flushTimer;
        private DispatcherTimer _pollGameLogsTimer;
        private long _gameLogAfterSeq = 0;
        private bool _pollGameInProgress = false;
        private int _lineCount = 0;

        public LogView()
        {
            InitializeComponent();
            
            // Subscribe to logger events
            Logger.OnLogMessage += OnLogMessageReceived;
            Unloaded += LogView_Unloaded;
        }

        private void OnLogMessageReceived(string message)
        {
            lock (_bufferLock)
            {
                _buffer.Add(message);
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
            _gameLogAfterSeq = 0;
            LogStatus.Text = "Log cleared";
        }

        private void SaveToFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"PhantomLink_Log_{timestamp}.txt";
                
                File.WriteAllText(fileName, LogTextBox.Text);
                LogStatus.Text = $"Log saved to {fileName}";
            }
            catch (Exception ex)
            {
                LogStatus.Text = $"Error saving log: {ex.Message}";
            }
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            LogTextBox.Text = "PhantomLink Log" + Environment.NewLine;
            LogTextBox.Text += new string('=', 50) + Environment.NewLine;

            _flushTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _flushTimer.Tick += (s, args) => FlushBufferedLogs();
            _flushTimer.Start();

            _pollGameLogsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _pollGameLogsTimer.Tick += async (s, args) => await PollGameLogsAsync();
            _pollGameLogsTimer.Start();
        }

        private void LogView_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _flushTimer?.Stop();
                _pollGameLogsTimer?.Stop();
            }
            catch
            {
            }

            Logger.OnLogMessage -= OnLogMessageReceived;
        }

        private async Task PollGameLogsAsync()
        {
            if (_pollGameInProgress)
                return;
            if (GameLogsCheck?.IsChecked != true)
                return;
            if (!IPCMeloaderClient.IsConnected)
                return;

            try
            {
                _pollGameInProgress = true;
                var resp = await IPCMeloaderClient.SendCommandAsync($"LOG_PULL|after={_gameLogAfterSeq}|max=200", 4000);
                if (string.IsNullOrWhiteSpace(resp) || resp.StartsWith("ERROR|", StringComparison.Ordinal))
                    return;
                if (!resp.StartsWith("LOG|", StringComparison.Ordinal))
                    return;

                var parts = resp.Split('|');
                for (var i = 1; i < parts.Length; i++)
                {
                    var p = parts[i];
                    if (string.IsNullOrWhiteSpace(p))
                        continue;

                    if (p.StartsWith("last=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (p.StartsWith("count=", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!p.StartsWith("e=", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var payload = p.Substring(2);
                    var segs = payload.Split(';');
                    if (segs.Length < 5)
                        continue;

                    if (!long.TryParse(segs[0], out var seq))
                        continue;
                    if (!long.TryParse(segs[1], out var utcTicks))
                        utcTicks = 0;
                    if (!int.TryParse(segs[2], out var level))
                        level = 0;

                    var msg = FromBase64Utf8(segs[3]);
                    var stack = FromBase64Utf8(segs[4]);
                    _gameLogAfterSeq = Math.Max(_gameLogAfterSeq, seq);

                    var line = FormatGameLogLine(utcTicks, level, msg, stack);
                    lock (_bufferLock)
                    {
                        _buffer.Add(line);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _pollGameInProgress = false;
            }
        }

        private static string FormatGameLogLine(long utcTicks, int level, string message, string stack)
        {
            var ts = "";
            if (utcTicks > 0)
            {
                try { ts = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss.fff"); } catch { ts = ""; }
            }

            var tag = level switch
            {
                2 => "WARN",
                0 => "ERROR",
                1 => "ASSERT",
                4 => "EXC",
                _ => "LOG"
            };

            var prefix = string.IsNullOrEmpty(ts) ? $"[GAME][{tag}] " : $"[GAME][{tag}] {ts} - ";
            if (!string.IsNullOrWhiteSpace(stack) && (tag == "ERROR" || tag == "ASSERT" || tag == "EXC"))
                return prefix + (message ?? "") + " | " + stack;
            return prefix + (message ?? "");
        }

        private static string FromBase64Utf8(string b64)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(b64))
                    return "";
                var bytes = Convert.FromBase64String(b64);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return "";
            }
        }

        private void FlushBufferedLogs()
        {
            List<string> pending;
            lock (_bufferLock)
            {
                if (_buffer.Count == 0)
                    return;

                pending = new List<string>(_buffer);
                _buffer.Clear();
            }

            var showInfo = ShowInfoCheck?.IsChecked ?? true;
            var showWarnings = ShowWarningsCheck?.IsChecked ?? true;
            var showErrors = ShowErrorsCheck?.IsChecked ?? true;
            var autoScroll = AutoScrollCheck?.IsChecked ?? true;

            foreach (var message in pending)
            {
                if (message.Contains("[INFO]") && !showInfo)
                    continue;
                if (message.Contains("[WARN]") && !showWarnings)
                    continue;
                if (message.Contains("[ERROR]") && !showErrors)
                    continue;
                if (message.Contains("[GAME][LOG]") && !showInfo)
                    continue;
                if (message.Contains("[GAME][WARN]") && !showWarnings)
                    continue;
                if ((message.Contains("[GAME][ERROR]") || message.Contains("[GAME][ASSERT]") || message.Contains("[GAME][EXC]")) && !showErrors)
                    continue;

                LogTextBox.AppendText(message + Environment.NewLine);
                _lineCount++;
            }

            if (_lineCount > 8000)
            {
                LogTextBox.Clear();
                LogTextBox.AppendText("PhantomLink Log" + Environment.NewLine);
                LogTextBox.AppendText(new string('=', 50) + Environment.NewLine);
                _lineCount = 0;
            }

            if (autoScroll)
                LogTextBox.ScrollToEnd();

            LogStatus.Text = $"Log entries: {_lineCount}";
        }
    }
}
