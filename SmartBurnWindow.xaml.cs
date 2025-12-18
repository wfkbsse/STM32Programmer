using System;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace STM32Programmer
{
    public partial class SmartBurnWindow : Window
    {
        // 缓存常用画刷，避免频繁创建GC压力
        private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(76, 175, 80));
        private static readonly SolidColorBrush BlueBrush = new(Color.FromRgb(33, 150, 243));
        private static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(224, 224, 224));
        private static readonly SolidColorBrush GrayBorderBrush = new(Color.FromRgb(189, 189, 189));
        
        private int _completedCount = 0;
        private int _successCount = 0;
        private int _failedCount = 0;
        private DateTime _startTime;
        private DispatcherTimer _timer;
        private readonly StringBuilder _logBuilder = new();
        
        public event EventHandler? StopRequested;
        
        public SmartBurnWindow()
        {
            InitializeComponent();
            _startTime = DateTime.Now;
            
            // 启动计时器
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }
        
        private void Timer_Tick(object? sender, EventArgs e)
        {
            var elapsed = DateTime.Now - _startTime;
            ElapsedTimeText.Text = $"运行时间: {elapsed:hh\\:mm\\:ss}";
            
            // 计算平均时间
            if (_completedCount > 0)
            {
                var avgSeconds = elapsed.TotalSeconds / _completedCount;
                AverageTimeText.Text = $"平均: {avgSeconds:F1} 秒/块";
            }
        }
        
        public void SetFirmwareInfo(
            string bootFileName, long bootFileSize, string bootFilePath, DateTime bootModified, string bootAddress,
            string appFileName, long appFileSize, string appFilePath, DateTime appModified, string appAddress)
        {
            Dispatcher.Invoke(() =>
            {
                // BOOT固件信息
                BootFirmwareText.Text = bootFileName;
                BootFileSizeText.Text = FormatFileSize(bootFileSize);
                BootFilePathText.Text = bootFilePath;
                BootModifiedText.Text = bootModified.ToString("yyyy-MM-dd HH:mm:ss");
                BootAddressText.Text = bootAddress;
                
                // APP固件信息
                AppFirmwareText.Text = appFileName;
                AppFileSizeText.Text = FormatFileSize(appFileSize);
                AppFilePathText.Text = appFilePath;
                AppModifiedText.Text = appModified.ToString("yyyy-MM-dd HH:mm:ss");
                AppAddressText.Text = appAddress;
            });
        }
        
        // 简化版本，保持向后兼容
        public void SetFirmwareInfo(string bootFirmware, string appFirmware)
        {
            Dispatcher.Invoke(() =>
            {
                BootFirmwareText.Text = bootFirmware;
                AppFirmwareText.Text = appFirmware;
            });
        }
        
        private static string FormatFileSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / (1024.0 * 1024.0):F2} MB"
        };
        
        public void UpdateStatus(string status, string detail, PackIconKind icon, Color iconColor)
        {
            Dispatcher.Invoke(() =>
            {
                // 更新大面积状态指示
                BigStatusText.Text = status;
                BigStatusDetail.Text = detail;
                BigStatusIcon.Kind = icon;
                BigStatusIcon.Foreground = new SolidColorBrush(iconColor);
                
                // 更新状态指示区背景色
                StatusIndicatorBorder.Background = new SolidColorBrush(GetStatusBackgroundColor(iconColor));
                
                // 兼容旧控件
                CurrentStatusText.Text = status;
                CurrentDetailText.Text = detail;
                CurrentStatusIcon.Kind = icon;
                CurrentStatusIcon.Foreground = new SolidColorBrush(iconColor);
                
                // 更新成功率
                UpdateSuccessRate();
            });
        }
        
        private static Color GetStatusBackgroundColor(Color iconColor)
        {
            // 根据图标颜色返回对应的浅色背景
            var green = Color.FromRgb(76, 175, 80);
            var red = Color.FromRgb(244, 67, 54);
            var orange = Color.FromRgb(255, 152, 0);
            
            return iconColor switch
            {
                _ when iconColor == Colors.Green || iconColor == green => Color.FromRgb(232, 245, 233), // 浅绿
                _ when iconColor == red => Color.FromRgb(255, 235, 238), // 浅红
                _ when iconColor == orange => Color.FromRgb(255, 243, 224), // 浅橙
                _ => Color.FromRgb(227, 242, 253) // 浅蓝（默认）
            };
        }
        
        private void UpdateSuccessRate()
        {
            if (_completedCount > 0)
            {
                double rate = (double)_successCount / _completedCount * 100;
                SuccessRateText.Text = $"{rate:F1}%";
                SuccessRateText.Foreground = new SolidColorBrush(rate >= 95 ? Colors.Green : (rate >= 80 ? Colors.Orange : Colors.Red));
            }
        }
        
        public void UpdateProgress(int value)
        {
            Dispatcher.Invoke(() =>
            {
                CurrentProgressBar.Value = value;
                ProgressText.Text = $"{value}%";
                UpdateProgressFillWidth(value);
            });
        }
        
        // 更新立体进度条填充宽度
        private void UpdateProgressFillWidth(double percentage)
        {
            if (ProgressTrack != null && ProgressFill != null)
            {
                double trackWidth = ProgressTrack.ActualWidth;
                if (trackWidth > 0)
                {
                    ProgressFill.Width = trackWidth * (percentage / 100.0);
                }
            }
        }
        
        // 更新BOOT进度
        public void UpdateBootProgress(int value)
        {
            Dispatcher.Invoke(() =>
            {
                BootProgressBar.Value = value;
                BootProgressText.Text = $"{value}%";
                // 总进度 = BOOT进度的一半 (25-50%)
                int totalProgress = 25 + value / 4;
                CurrentProgressBar.Value = totalProgress;
                ProgressText.Text = $"{totalProgress}%";
                UpdateProgressFillWidth(totalProgress);
                
                // 更新步骤指示器
                UpdateStepIndicator(2, value == 100 ? StepState.Completed : StepState.InProgress);
                CurrentStepText.Text = $"正在烧录BOOT... {value}%";
            });
        }
        
        // 更新APP进度
        public void UpdateAppProgress(int value)
        {
            Dispatcher.Invoke(() =>
            {
                AppProgressBar.Value = value;
                AppProgressText.Text = $"{value}%";
                // 总进度 = 50% + APP进度的一半 (50-100%)
                int totalProgress = 50 + value / 2;
                CurrentProgressBar.Value = totalProgress;
                ProgressText.Text = $"{totalProgress}%";
                UpdateProgressFillWidth(totalProgress);
                
                // 更新步骤指示器
                UpdateStepIndicator(3, value == 100 ? StepState.Completed : StepState.InProgress);
                CurrentStepText.Text = $"正在烧录APP... {value}%";
            });
        }
        
        // 更新板子计数显示
        public void UpdateBoardCount(int current)
        {
            Dispatcher.Invoke(() =>
            {
                BoardCountText.Text = $"第 {current} 块";
                // 重置步骤指示器
                ResetStepIndicator();
                UpdateStepIndicator(1, StepState.InProgress);
                CurrentStepText.Text = "检测到连接，准备烧录...";
            });
        }
        
        // 步骤状态枚举
        private enum StepState { Pending, InProgress, Completed }
        
        // 更新步骤指示器
        private void UpdateStepIndicator(int step, StepState state)
        {
            Ellipse? circle = null;
            MaterialDesignThemes.Wpf.PackIcon? icon = null;
            Border? prevLine = null;
            
            (circle, icon, prevLine) = step switch
            {
                1 => (Step1Circle, Step1Icon, (Border?)null),
                2 => (Step2Circle, Step2Icon, Line1),
                3 => (Step3Circle, Step3Icon, Line2),
                4 => (Step4Circle, Step4Icon, Line3),
                _ => (null, null, null)
            };
            
            if (circle == null) return;
            
            var (fillBrush, strokeBrush, iconVisible, lineBrush) = state switch
            {
                StepState.Completed => (GreenBrush, GreenBrush, true, GreenBrush),
                StepState.InProgress => (BlueBrush, BlueBrush, false, GreenBrush),
                _ => (GrayBrush, GrayBorderBrush, false, (SolidColorBrush?)null)
            };
            
            circle.Fill = fillBrush;
            circle.Stroke = strokeBrush;
            if (icon != null) icon.Visibility = iconVisible ? Visibility.Visible : Visibility.Collapsed;
            if (prevLine != null && lineBrush != null) prevLine.Background = lineBrush;
        }
        
        // 重置步骤指示器
        private void ResetStepIndicator()
        {
            Step1Circle.Fill = GrayBrush;
            Step1Circle.Stroke = GrayBorderBrush;
            Step1Icon.Visibility = Visibility.Collapsed;
            
            Step2Circle.Fill = GrayBrush;
            Step2Circle.Stroke = GrayBorderBrush;
            Step2Icon.Visibility = Visibility.Collapsed;
            
            Step3Circle.Fill = GrayBrush;
            Step3Circle.Stroke = GrayBorderBrush;
            Step3Icon.Visibility = Visibility.Collapsed;
            
            Step4Circle.Fill = GrayBrush;
            Step4Circle.Stroke = GrayBorderBrush;
            Step4Icon.Visibility = Visibility.Collapsed;
            
            Line1.Background = GrayBrush;
            Line2.Background = GrayBrush;
            Line3.Background = GrayBrush;
            
            CurrentProgressBar.Value = 0;
            ProgressText.Text = "0%";
            UpdateProgressFillWidth(0);
        }
        
        // 设置烧录完成状态
        public void SetBurnComplete(bool success)
        {
            Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    UpdateStepIndicator(4, StepState.Completed);
                    CurrentProgressBar.Value = 100;
                    ProgressText.Text = "100%";
                    UpdateProgressFillWidth(100);
                    CurrentStepText.Text = "烧录完成！请移除电路板";
                }
                else
                {
                    CurrentStepText.Text = "烧录失败！请检查连接";
                }
            });
        }
        
        public void IncrementCompleted(bool success)
        {
            Dispatcher.Invoke(() =>
            {
                _completedCount++;
                CompletedCountText.Text = _completedCount.ToString();
                
                if (success)
                {
                    _successCount++;
                    SuccessCountText.Text = _successCount.ToString();
                }
                else
                {
                    _failedCount++;
                    FailedCountText.Text = _failedCount.ToString();
                }
                
                // 更新成功率
                UpdateSuccessRate();
            });
        }
        
        public void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                _logBuilder.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                LogTextBlock.Text = _logBuilder.ToString();
                LogScrollViewer.ScrollToEnd();
            });
        }
        
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "确定要停止智能自动烧录吗？",
                "确认停止",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                StopRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            _logBuilder.Clear();
            LogTextBlock.Text = "";
        }
        
        // 窗口拖动和双击最大化
        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 双击标题栏最大化/还原
                MaximizeButton_Click(sender, e);
            }
            else if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                // 如果是最大化状态，先还原再拖动
                if (this.WindowState == WindowState.Maximized)
                {
                    // 获取鼠标相对于窗口的位置比例
                    var point = e.GetPosition(this);
                    var ratioX = point.X / this.ActualWidth;
                    
                    this.WindowState = WindowState.Normal;
                    MaximizeIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.WindowMaximize;
                    
                    // 调整窗口位置使鼠标保持在相对位置
                    this.Left = System.Windows.Forms.Cursor.Position.X - (this.Width * ratioX);
                    this.Top = System.Windows.Forms.Cursor.Position.Y - 20;
                }
                this.DragMove();
            }
        }
        
        // 最大化/还原按钮
        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                MaximizeIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.WindowMaximize;
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                MaximizeIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.WindowRestore;
            }
        }
        
        // 基准窗口尺寸
        private const double BaseWidth = 950;
        private const double BaseHeight = 580;
        
        // 窗口大小改变 - 使用LayoutTransform实现清晰缩放
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 计算缩放比例，保持等比例
            double scaleX = this.ActualWidth / BaseWidth;
            double scaleY = this.ActualHeight / BaseHeight;
            double scale = Math.Min(scaleX, scaleY);
            
            // 限制最小缩放比例
            scale = Math.Max(0.5, scale);
            
            // 应用缩放
            ContentScale.ScaleX = scale;
            ContentScale.ScaleY = scale;
        }
        
        // 关闭按钮
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 如果正在运行，先确认停止
            if (_timer?.IsEnabled == true)
            {
                var result = MessageBox.Show(
                    "确定要停止智能自动烧录并关闭窗口吗？",
                    "确认关闭",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    StopRequested?.Invoke(this, EventArgs.Empty);
                    this.Close();
                }
            }
            else
            {
                this.Close();
            }
        }
        
        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            // 如果正在运行且不是通过CloseButton关闭的，阻止关闭
            if (_timer?.IsEnabled == true)
            {
                e.Cancel = true;
                CloseButton_Click(sender ?? this, new RoutedEventArgs());
            }
        }
        
        public void OnStopped()
        {
            Dispatcher.Invoke(() =>
            {
                _timer.Stop();
                StatusText.Text = "已停止";
                StopButton.IsEnabled = false;
                
                UpdateStatus(
                    "烧录已停止",
                    $"共完成 {_completedCount} 块 (成功 {_successCount} 块，失败 {_failedCount} 块)",
                    PackIconKind.CheckCircle,
                    Colors.Green
                );
                
                AddLog($"智能自动烧录已停止，共完成 {_completedCount} 块板子");
                
                // 允许关闭窗口
                Closing -= Window_Closing;
            });
        }
        
        // 资源释放
        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            base.OnClosed(e);
        }
    }
}
