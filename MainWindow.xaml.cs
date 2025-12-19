using Microsoft.Win32;
using STM32Programmer.Models;
using STM32Programmer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBlock = System.Windows.Controls.TextBlock;

namespace STM32Programmer;

// 简单的RelayCommand实现 (.NET 8 Primary Constructor)
public class RelayCommand(Action<object> execute, Predicate<object>? canExecute = null) : ICommand
{
    private readonly Action<object> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public bool CanExecute(object? parameter) => canExecute == null || canExecute(parameter!);

    public void Execute(object? parameter) => _execute(parameter!);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private DeviceService? _deviceService;
    private FirmwareService? _firmwareService;
    private LogService? _logService;
    private SerialBurnService? _serialBurnService;
    private DispatcherTimer? _connectionTimer;
    private STLinkDevice? _currentDevice;
    private FirmwareFile? _bootFirmware;
    private FirmwareFile? _appFirmware;
    private readonly SolidColorBrush _redBrush = new(Colors.Red);
    private readonly SolidColorBrush _greenBrush = new(Colors.Green);
    private readonly SolidColorBrush _yellowBrush = new(Colors.Orange);
    private bool _isBurning = false;
    private bool _isDesignMode;
    private bool _isSwipeHandled = false;
    private bool _isLogging = false; // 防止日志递归的标志

    public MainWindow()
    {
        InitializeComponent();
        
        // 启用触摸支持 - 必须在InitializeComponent之后立即调用
        EnableTouchSupport();
        
        _isDesignMode = System.ComponentModel.DesignerProperties.GetIsInDesignMode(new DependencyObject());
            
            if (!_isDesignMode)
            {
                try
                {
                    // 初始化日志服务
                    _logService = new LogService();
        
                    // 初始化服务
                    _deviceService = new DeviceService();
                    _firmwareService = new FirmwareService();
                    _serialBurnService = new SerialBurnService(_logService);
        
                    // 注册日志事件
                    if (_logService != null)
                    {
                        _logService.LogMessageAdded += OnExternalLogMessage;
                    }
        
                    if (_deviceService != null)
                    {
                        _deviceService.ShowConnectionLogs = false;  // 不显示连接刷新日志
                        _deviceService.LogMessage += OnExternalLogMessage;
                        _deviceService.DeviceStatusChanged += OnDeviceStatusChanged; // 订阅设备状态变化事件
                    }
                    
                    if (_firmwareService != null)
                    {
                        _firmwareService.LogMessage += OnExternalLogMessage;
                    }
                    
                    // 设置日志UI绑定
                    if (_logService != null && OperationLogTextBox != null)
                    {
                        // 只绑定操作日志
                        OperationLogTextBox.ItemsSource = _logService.OperationLogEntries;
                    }
        
                    // 初始化设备状态
                    _currentDevice = new STLinkDevice();
        
                    // 设置定时刷新连接状态
                    _connectionTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(5)
                    };
                    _connectionTimer.Tick += async (s, e) => await RefreshConnectionStatusAsync();
        
                // 在窗口加载完成后再初始化UI
                this.Loaded += MainWindow_Loaded;
                    
                    // 初始化日志过滤控件
                    InitializeLogFilters();
                    
                    // 启动时打印日志信息
                    _logService?.LogInfo("主窗口", "STM32固件烧写工具已启动");
                    _logService?.LogInfo("主窗口", "版本: 1.0.0");
                    _logService?.LogInfo("主窗口", "初始化中...");
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"初始化组件时出错: {ex.Message}", "初始化错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 窗口完全加载后初始化UI
            InitializeUI();
            
            // 设置默认烧录地址
            InitializeDefaultAddresses();
            
            // 确保连接状态正确显示
            UpdateConnectionStatus();
            
            // 启用自动刷新功能
            if (_deviceService != null)
            {
                _deviceService.AutoRefreshEnabled = true;
            }
            
            // 更新自动刷新状态
            UpdateAutoRefreshStatus(true);
            
            _logService?.LogInfo("主窗口", "窗口加载完成");
        }
        catch (Exception ex)
        {
            LogMessage($"窗口加载时出错: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// 初始化默认烧录地址
    /// </summary>
    private void InitializeDefaultAddresses()
    {
        if (BootStartAddressTextBox != null && string.IsNullOrWhiteSpace(BootStartAddressTextBox.Text))
        {
            BootStartAddressTextBox.Text = "0x08000000";
        }
        
        if (AppStartAddressTextBox != null && string.IsNullOrWhiteSpace(AppStartAddressTextBox.Text))
        {
            AppStartAddressTextBox.Text = "0x08010000";
        }
    }
    
    private async void RefreshConnection_Click(object sender, RoutedEventArgs e)
    {
        await RefreshConnectionStatusAsync();
        
        // 添加下面的通知
        ShowNotification("正在刷新连接状态...", PackIconKind.Refresh);
    }
    
    private async Task RefreshConnectionStatusAsync()
    {
        if (_isBurning) return;
        
        try
        {
            RefreshConnectionButton.IsEnabled = false;
            // 不在状态栏显示连接状态信息
            // StatusBarText.Text = "正在检查连接...";
            ShowStatus("正在检查连接...", PackIconKind.Refresh);
            
            if (_deviceService != null)
            {
            _currentDevice = await _deviceService.CheckConnectionAsync();
            }
            else
            {
                LogMessage("设备服务未初始化");
                ShowStatus("设备服务未初始化", PackIconKind.Error);
                return;
            }
            
            // 更新UI
            UpdateConnectionStatus();
            
            // 不在状态栏显示连接状态信息
            // StatusBarText.Text = "连接检查完成";
            ShowStatus("连接检查完成", PackIconKind.CheckCircle);
        }
        catch (Exception ex)
        {
            LogMessage($"刷新连接状态时出错: {ex.Message}");
            // 只显示错误信息
            ShowStatus("连接检查出错", PackIconKind.AlertCircle);
        }
        finally
        {
            RefreshConnectionButton.IsEnabled = true;
        }
    }
    
    private void UpdateConnectionStatus()
    {
        try
    {
        // 更新ST-LINK状态
        if (_currentDevice == null)
        {
            // 如果设备为空则显示未连接
                if (StLinkStatusIndicator != null)
                {
                    StLinkStatusIndicator.Foreground = _redBrush;
                    StLinkStatusIndicator.Kind = PackIconKind.Cancel;
                }
                
                if (StLinkStatusText != null)
                {
            StLinkStatusText.Text = "未连接";
                }
                
                if (ChipStatusIndicator != null)
                {
                    ChipStatusIndicator.Foreground = _redBrush;
                    ChipStatusIndicator.Kind = PackIconKind.Chip;
                }
                
                if (ChipStatusText != null)
                {
            ChipStatusText.Text = "未连接";
                }
            
            // 禁用烧写按钮
                if (BurnBootButton != null) BurnBootButton.IsEnabled = false;
                if (BurnAppButton != null) BurnAppButton.IsEnabled = false;
                if (BurnAllButton != null) BurnAllButton.IsEnabled = false;
            return;
        }
        
        if (_currentDevice.Status == ConnectionStatus.Connected)
        {
                if (StLinkStatusIndicator != null)
                {
                    StLinkStatusIndicator.Foreground = _greenBrush;
                    StLinkStatusIndicator.Kind = PackIconKind.CheckCircle;
                }
                
                if (StLinkStatusText != null)
                {
            StLinkStatusText.Text = "已连接";
                }
        }
        else if (_currentDevice.Status == ConnectionStatus.Error)
        {
                if (StLinkStatusIndicator != null)
                {
                    StLinkStatusIndicator.Foreground = _yellowBrush;
                    StLinkStatusIndicator.Kind = PackIconKind.AlertCircle;
                }
                
                if (StLinkStatusText != null)
                {
            StLinkStatusText.Text = $"错误: {_currentDevice.ErrorMessage}";
                }
        }
        else
        {
                if (StLinkStatusIndicator != null)
                {
                    StLinkStatusIndicator.Foreground = _redBrush;
                    StLinkStatusIndicator.Kind = PackIconKind.Cancel;
                }
                
                if (StLinkStatusText != null)
                {
            StLinkStatusText.Text = "未连接";
                }
        }
        
        // 更新芯片状态
        if (_currentDevice.Status == ConnectionStatus.Connected && _currentDevice.IsChipConnected)
        {
                if (ChipStatusIndicator != null)
                {
                    ChipStatusIndicator.Foreground = _greenBrush;
                    ChipStatusIndicator.Kind = PackIconKind.Chip;
                }
                
                if (ChipStatusText != null)
                {
            ChipStatusText.Text = string.IsNullOrEmpty(_currentDevice.ChipType) ? 
                "已连接" : $"已连接 ({_currentDevice.ChipType})";
                }
        }
        else
        {
                if (ChipStatusIndicator != null)
                {
                    ChipStatusIndicator.Foreground = _redBrush;
                    ChipStatusIndicator.Kind = PackIconKind.Chip;
                }
                
                if (ChipStatusText != null)
                {
            ChipStatusText.Text = "未连接";
                }
        }
        
        // 更新烧写按钮状态
        bool canBurn = _currentDevice.Status == ConnectionStatus.Connected && _currentDevice.IsChipConnected;
            if (BurnBootButton != null) BurnBootButton.IsEnabled = canBurn && _bootFirmware != null && _bootFirmware.IsValid;
            if (BurnAppButton != null) BurnAppButton.IsEnabled = canBurn && _appFirmware != null && _appFirmware.IsValid;
            if (BurnAllButton != null) BurnAllButton.IsEnabled = canBurn && _bootFirmware != null && _bootFirmware.IsValid 
                                && _appFirmware != null && _appFirmware.IsValid;
        }
        catch (Exception ex)
        {
            LogMessage($"更新连接状态时出错: {ex.Message}", LogLevel.Error);
        }
    }
    
    private void SelectBootFile_Click(object sender, RoutedEventArgs e)
    {
        if (_firmwareService == null)
        {
            LogMessage("固件服务未初始化", LogLevel.Error);
            return;
        }
        
        var firmware = _firmwareService.SelectFirmwareFile(FirmwareType.Boot);
        if (firmware != null)
        {
            _bootFirmware = firmware;
            BootFilePathTextBox.Text = firmware.FilePath;
            UpdateConnectionStatus();
            
            // 更新BOOT固件信息显示
            UpdateBootFirmwareInfo(firmware);
            
            // 创建包含单一固件的列表并绑定到ListView
            BootFirmwareListView.ItemsSource = new List<FirmwareFile> { firmware };
            BootFirmwareListView.SelectedIndex = 0;
            
            LogMessage($"已手动选择BOOT固件: {firmware.FileName}");
            
            // 尝试查找目录中的其他类似固件
            Task.Run(async () => 
            {
                try 
                {
                    // 获取所在目录
                    string directory = Path.GetDirectoryName(firmware.FilePath) ?? "";
                    if (!string.IsNullOrEmpty(directory))
                    {
                        var allFirmwareList = await _firmwareService.FindAllFirmwareFilesAsync(FirmwareType.Boot, directory);
                        
                        if (allFirmwareList.Count > 1)
                        {
                            // 查找到多个固件，显示在UI上
                            await Dispatcher.InvokeAsync(() => 
                            {
                                BootFirmwareListView.ItemsSource = allFirmwareList;
                                
                                // 选中当前固件
                                int selectedIndex = allFirmwareList.FindIndex(f => f.FilePath == firmware.FilePath);
                                BootFirmwareListView.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                                
                                LogMessage($"在同一目录中找到{allFirmwareList.Count}个BOOT固件");
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"搜索目录中的其他BOOT固件时出错: {ex.Message}", LogLevel.Warning);
                }
            });
        }
    }
    
    private void SelectAppFile_Click(object sender, RoutedEventArgs e)
    {
        if (_firmwareService == null)
        {
            LogMessage("固件服务未初始化", LogLevel.Error);
            return;
        }
        
        var firmware = _firmwareService.SelectFirmwareFile(FirmwareType.App);
        if (firmware != null)
        {
            _appFirmware = firmware;
            AppFilePathTextBox.Text = firmware.FilePath;
            UpdateConnectionStatus();
            
            // 更新APP固件信息显示
            UpdateAppFirmwareInfo(firmware);
            
            // 创建包含单一固件的列表并绑定到ListView
            AppFirmwareListView.ItemsSource = new List<FirmwareFile> { firmware };
            AppFirmwareListView.SelectedIndex = 0;
            
            LogMessage($"已手动选择APP固件: {firmware.FileName}");
            
            // 尝试查找目录中的其他类似固件
            Task.Run(async () => 
            {
                try 
                {
                    // 获取所在目录
                    string directory = Path.GetDirectoryName(firmware.FilePath) ?? "";
                    if (!string.IsNullOrEmpty(directory))
                    {
                        var allFirmwareList = await _firmwareService.FindAllFirmwareFilesAsync(FirmwareType.App, directory);
                        
                        if (allFirmwareList.Count > 1)
                        {
                            // 查找到多个固件，显示在UI上
                            await Dispatcher.InvokeAsync(() => 
                            {
                                AppFirmwareListView.ItemsSource = allFirmwareList;
                                
                                // 选中当前固件
                                int selectedIndex = allFirmwareList.FindIndex(f => f.FilePath == firmware.FilePath);
                                AppFirmwareListView.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                                
                                LogMessage($"在同一目录中找到{allFirmwareList.Count}个APP固件");
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"搜索目录中的其他APP固件时出错: {ex.Message}", LogLevel.Warning);
                }
            });
        }
    }
    
    /// <summary>
    /// 智能扫描固件 - 选择目录后自动识别BOOT和APP固件
    /// </summary>
    private async void SmartScanFirmware_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 选择扫描目录
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择要扫描的固件目录",
                ShowNewFolderButton = false,
                UseDescriptionForTitle = true
            };
            
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;
            
            string scanPath = dialog.SelectedPath;
            
            SmartScanButton.IsEnabled = false;
            ShowStatus("正在智能扫描固件...", PackIconKind.FolderSearchOutline);
            LogMessage($"开始智能扫描目录: {scanPath}");
            
            if (_firmwareService == null)
            {
                LogMessage("固件服务未初始化", LogLevel.Error);
                ShowStatus("无法扫描固件：服务未初始化", PackIconKind.Error);
                return;
            }
            
            // 执行智能扫描
            var (bootFiles, appFiles) = await _firmwareService.SmartScanFirmwareAsync(scanPath);
            
            // 更新BOOT固件列表
            if (bootFiles.Count > 0)
            {
                BootFirmwareListView.ItemsSource = bootFiles;
                BootFirmwareListView.SelectedIndex = 0;
                LogMessage($"智能识别到 {bootFiles.Count} 个BOOT固件，已选择最新的: {bootFiles[0].FileName}");
            }
            else
            {
                BootFirmwareListView.ItemsSource = null;
                ClearBootFirmware();
                LogMessage("未识别到BOOT固件", LogLevel.Warning);
            }
            
            // 更新APP固件列表
            if (appFiles.Count > 0)
            {
                AppFirmwareListView.ItemsSource = appFiles;
                AppFirmwareListView.SelectedIndex = 0;
                LogMessage($"智能识别到 {appFiles.Count} 个APP固件，已选择最新的: {appFiles[0].FileName}");
            }
            else
            {
                AppFirmwareListView.ItemsSource = null;
                ClearAppFirmware();
                LogMessage("未识别到APP固件", LogLevel.Warning);
            }
            
            // 显示扫描结果
            string resultMsg = $"扫描完成: BOOT {bootFiles.Count}个, APP {appFiles.Count}个";
            ShowStatus(resultMsg, bootFiles.Count > 0 || appFiles.Count > 0 ? PackIconKind.CheckCircle : PackIconKind.AlertCircle);
            
            if (bootFiles.Count == 0 && appFiles.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "未找到符合命名规则的固件文件。\n\n" +
                    "BOOT文件关键词: BOOT, BOOTLOADER, BL_, BOOTSTRAP, UBOOT, LOADER, STARTUP\n" +
                    "APP文件关键词: APP, APPLICATION, MAIN, FIRMWARE, FW_, PROGRAM, USER, APPL\n\n" +
                    "请确保固件文件名包含上述关键词。",
                    "扫描结果", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"智能扫描固件时出错: {ex.Message}", LogLevel.Error);
            ShowStatus("扫描固件出错", PackIconKind.AlertCircle);
        }
        finally
        {
            SmartScanButton.IsEnabled = true;
        }
    }
    
    private async void BurnBoot_Click(object sender, RoutedEventArgs e)
    {
        if (_bootFirmware == null || !_bootFirmware.IsValid)
        {
            System.Windows.MessageBox.Show("请先选择有效的BOOT固件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        // 同步UI中的地址到固件对象
        SyncFirmwareAddresses();
        
        await BurnFirmwareAsync(_bootFirmware);
    }
    
    private async void BurnApp_Click(object sender, RoutedEventArgs e)
    {
        if (_appFirmware == null || !_appFirmware.IsValid)
        {
            System.Windows.MessageBox.Show("请先选择有效的APP固件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        // 同步UI中的地址到固件对象
        SyncFirmwareAddresses();
        
        await BurnFirmwareAsync(_appFirmware);
    }
    
    private async void BurnAll_Click(object sender, RoutedEventArgs e)
    {
        if (_bootFirmware == null || !_bootFirmware.IsValid)
        {
            System.Windows.MessageBox.Show("请先选择有效的BOOT固件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        if (_appFirmware == null || !_appFirmware.IsValid)
        {
            System.Windows.MessageBox.Show("请先选择有效的APP固件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        // 同步UI中的地址到固件对象
        SyncFirmwareAddresses();
        
        LogMessage("开始一键烧录过程...");
        
        // 先烧BOOT，再烧APP
        bool bootSuccess = await BurnFirmwareAsync(_bootFirmware);
        if (bootSuccess)
        {
            await BurnFirmwareAsync(_appFirmware);
        }
    }
    
    /// <summary>
    /// 同步UI中的地址设置到固件对象
    /// </summary>
    private void SyncFirmwareAddresses()
    {
        if (_bootFirmware != null)
        {
            string bootAddr = BootStartAddressTextBox?.Text ?? "";
            // 如果UI中有地址则使用，否则保持固件对象中的默认值
            if (!string.IsNullOrWhiteSpace(bootAddr))
            {
                _bootFirmware.StartAddress = bootAddr;
            }
            LogMessage($"BOOT烧录地址: {_bootFirmware.StartAddress}");
        }
        
        if (_appFirmware != null)
        {
            string appAddr = AppStartAddressTextBox?.Text ?? "";
            // 如果UI中有地址则使用，否则保持固件对象中的默认值
            if (!string.IsNullOrWhiteSpace(appAddr))
            {
                _appFirmware.StartAddress = appAddr;
            }
            LogMessage($"APP烧录地址: {_appFirmware.StartAddress}");
        }
    }
    
    // ==================== 智能自动烧录功能 ====================
    
    private bool _isSmartBurnRunning = false;
    private CancellationTokenSource? _smartBurnCancellation;
    private int _smartBurnCount = 0;
    private int _smartBurnSuccessCount = 0;
    private int _smartBurnFailedCount = 0;
    private SmartBurnWindow? _smartBurnWindow;
    
    private async void SmartBurn_Click(object sender, RoutedEventArgs e)
    {
        if (_isSmartBurnRunning)
        {
            // 停止智能烧录
            StopSmartBurn();
        }
        else
        {
            // 启动智能烧录
            await StartSmartBurnAsync();
        }
    }
    
    private async Task StartSmartBurnAsync()
    {
        // APP固件必须选择
        if (_appFirmware == null || !_appFirmware.IsValid)
        {
            System.Windows.MessageBox.Show("请先选择有效的APP固件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        // 同步UI中的地址到固件对象
        SyncFirmwareAddresses();
        
        _isSmartBurnRunning = true;
        _smartBurnCount = 0;
        _smartBurnSuccessCount = 0;
        _smartBurnFailedCount = 0;
        _smartBurnCancellation = new CancellationTokenSource();
        
        // 创建并显示智能烧录窗口
        _smartBurnWindow = new SmartBurnWindow();
        
        // 根据是否有BOOT固件设置信息
        bool hasBootFirmware = _bootFirmware != null && _bootFirmware.IsValid;
        
        if (hasBootFirmware)
        {
            _smartBurnWindow.SetFirmwareInfo(
                _bootFirmware!.FileName,
                _bootFirmware.FileSize,
                _bootFirmware.FilePath,
                _bootFirmware.LastModified,
                _bootFirmware.StartAddress,  // 使用固件对象中的地址
                _appFirmware.FileName,
                _appFirmware.FileSize,
                _appFirmware.FilePath,
                _appFirmware.LastModified,
                _appFirmware.StartAddress    // 使用固件对象中的地址
            );
        }
        else
        {
            // 仅APP模式
            _smartBurnWindow.SetFirmwareInfo(
                "-",
                0,
                "-",
                DateTime.MinValue,
                "-",
                _appFirmware.FileName,
                _appFirmware.FileSize,
                _appFirmware.FilePath,
                _appFirmware.LastModified,
                _appFirmware.StartAddress    // 使用固件对象中的地址
            );
        }
        
        _smartBurnWindow.StopRequested += (s, e) => StopSmartBurn();
        _smartBurnWindow.AddLog("智能自动烧录已启动");
        
        if (hasBootFirmware)
        {
            _smartBurnWindow.AddLog($"烧录模式: BOOT + APP");
            _smartBurnWindow.AddLog($"BOOT固件: {_bootFirmware!.FileName} ({_bootFirmware.FileSize / 1024} KB)");
        }
        else
        {
            _smartBurnWindow.AddLog($"烧录模式: 仅 APP");
        }
        _smartBurnWindow.AddLog($"APP固件: {_appFirmware.FileName} ({_appFirmware.FileSize / 1024} KB)");
        
        _smartBurnWindow.Show();
        
        // 更新步骤标签
        _smartBurnWindow.UpdateStepLabels();
        
        // 更新主窗口UI
        UpdateSmartBurnUI(true);
        
        // 禁用手动烧录按钮
        BurnBootButton.IsEnabled = false;
        BurnAppButton.IsEnabled = false;
        BurnAllButton.IsEnabled = false;
        
        _logService?.LogInfo("智能烧录", "智能自动烧录已启动");
        ShowStatus("智能自动烧录已启动，等待芯片连接...", PackIconKind.AutoFix);
        
        // 启动监测循环
        await SmartBurnLoopAsync(_smartBurnCancellation.Token);
    }
    
    private void StopSmartBurn()
    {
        _smartBurnCancellation?.Cancel();
        _isSmartBurnRunning = false;
        
        // 更新智能烧录窗口
        _smartBurnWindow?.OnStopped();
        
        // 更新主窗口UI
        UpdateSmartBurnUI(false);
        
        // 恢复手动烧录按钮
        UpdateConnectionStatus();
        
        _logService?.LogInfo("智能烧录", $"智能自动烧录已停止，共完成 {_smartBurnCount} 块板子 (成功 {_smartBurnSuccessCount} 块，失败 {_smartBurnFailedCount} 块)");
        ShowStatus($"智能自动烧录已停止，共完成 {_smartBurnCount} 块", PackIconKind.CheckCircle);
        
        ShowNotification($"智能烧录已停止\n共完成 {_smartBurnCount} 块板子", PackIconKind.CheckCircle);
    }
    
    private void UpdateSmartBurnUI(bool isRunning)
    {
        if (SmartBurnButton == null || SmartBurnText == null || SmartBurnIcon == null)
            return;
        
        if (isRunning)
        {
            SmartBurnButton.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // 红色
            SmartBurnText.Text = "停止自动烧录";
            SmartBurnIcon.Kind = PackIconKind.Stop;
        }
        else
        {
            SmartBurnButton.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
            SmartBurnText.Text = "智能自动烧录";
            SmartBurnIcon.Kind = PackIconKind.AutoFix;
        }
    }
    
    private async Task SmartBurnLoopAsync(CancellationToken cancellationToken)
    {
        bool wasConnected = false;
        bool isBurning = false;
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 检查连接状态
                var device = await _deviceService!.CheckConnectionAsync();
                bool isConnected = device.Status == ConnectionStatus.Connected && device.IsChipConnected;
                
                // 状态变化：从未连接到已连接
                if (isConnected && !wasConnected && !isBurning)
                {
                    _logService?.LogInfo("智能烧录", $"检测到芯片连接 (第 {_smartBurnCount + 1} 块)");
                    ShowStatus($"检测到芯片连接，开始烧录第 {_smartBurnCount + 1} 块...", PackIconKind.Chip);
                    
                    // 更新窗口状态
                    _smartBurnWindow?.UpdateBoardCount(_smartBurnCount + 1);
                    _smartBurnWindow?.UpdateStatus(
                        "检测到芯片连接",
                        $"开始烧录第 {_smartBurnCount + 1} 块电路板",
                        PackIconKind.Chip,
                        Color.FromRgb(26, 115, 232)
                    );
                    _smartBurnWindow?.AddLog($"检测到芯片连接 (第 {_smartBurnCount + 1} 块)");
                    
                    // 短暂延迟确保连接稳定
                    await Task.Delay(500, cancellationToken);
                    
                    // 开始烧录
                    isBurning = true;
                    bool success = await SmartBurnSingleBoardAsync(cancellationToken);
                    isBurning = false;
                    
                    if (success)
                    {
                        _smartBurnCount++;
                        _smartBurnSuccessCount++;
                        _logService?.LogInfo("智能烧录", $"第 {_smartBurnCount} 块板子烧录完成");
                        ShowStatus($"第 {_smartBurnCount} 块烧录完成，请移除电路板", PackIconKind.CheckCircle, 0);
                        
                        // 更新窗口
                        _smartBurnWindow?.IncrementCompleted(true);
                        _smartBurnWindow?.SetBurnComplete(true);
                        _smartBurnWindow?.UpdateStatus(
                            "烧录完成",
                            $"第 {_smartBurnCount} 块烧录成功，请移除电路板",
                            PackIconKind.CheckCircle,
                            Color.FromRgb(76, 175, 80)
                        );
                        _smartBurnWindow?.AddLog($"第 {_smartBurnCount} 块板子烧录成功");
                        
                        // 播放提示音
                        System.Media.SystemSounds.Asterisk.Play();
                        
                        // 显示通知
                        ShowNotification($"第 {_smartBurnCount} 块烧录完成\n请移除电路板", PackIconKind.CheckCircle);
                    }
                    else
                    {
                        _smartBurnCount++;
                        _smartBurnFailedCount++;
                        _logService?.LogError("智能烧录", "烧录失败，等待重试或移除板子");
                        ShowStatus("烧录失败，请检查连接或移除板子", PackIconKind.AlertCircle, 0);
                        
                        // 更新窗口
                        _smartBurnWindow?.IncrementCompleted(false);
                        _smartBurnWindow?.SetBurnComplete(false);
                        _smartBurnWindow?.UpdateStatus(
                            "烧录失败",
                            "请检查连接或移除板子",
                            PackIconKind.AlertCircle,
                            Color.FromRgb(244, 67, 54)
                        );
                        _smartBurnWindow?.AddLog($"第 {_smartBurnCount} 块板子烧录失败");
                        
                        // 播放错误提示音
                        System.Media.SystemSounds.Hand.Play();
                    }
                }
                // 状态变化：从已连接到未连接
                else if (!isConnected && wasConnected)
                {
                    _logService?.LogInfo("智能烧录", "检测到芯片断开，等待下一块板子...");
                    ShowStatus($"等待下一块板子... (已完成 {_smartBurnCount} 块)", PackIconKind.Sync);
                    
                    // 更新窗口
                    _smartBurnWindow?.UpdateStatus(
                        "等待芯片连接",
                        $"已完成 {_smartBurnCount} 块，等待下一块电路板",
                        PackIconKind.Sync,
                        Color.FromRgb(158, 158, 158)
                    );
                    _smartBurnWindow?.UpdateProgress(0);
                    _smartBurnWindow?.AddLog("检测到芯片断开，等待下一块板子");
                }
                
                wasConnected = isConnected;
                
                // 等待一段时间再检查（避免频繁检测）
                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
            _logService?.LogInfo("智能烧录", "智能烧录循环已取消");
        }
        catch (Exception ex)
        {
            _logService?.LogError("智能烧录", $"智能烧录循环出错: {ex.Message}");
            ShowStatus("智能烧录出错，已自动停止", PackIconKind.AlertCircle);
        }
        finally
        {
            if (_isSmartBurnRunning)
            {
                StopSmartBurn();
            }
        }
    }
    
    private async Task<bool> SmartBurnSingleBoardAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 检查APP固件是否为空（必须）
            if (_appFirmware == null || _deviceService == null)
            {
                _logService?.LogError("智能烧录", "APP固件或设备服务未初始化");
                return false;
            }
            
            // 获取烧录模式
            bool burnBootAndApp = _smartBurnWindow?.IsBurnBootAndApp ?? true;
            bool hasBootFirmware = _bootFirmware != null && _bootFirmware.IsValid;
            
            // 如果选择了BOOT+APP模式但没有BOOT固件，自动切换到仅APP模式
            if (burnBootAndApp && !hasBootFirmware)
            {
                burnBootAndApp = false;
                _smartBurnWindow?.AddLog("未选择BOOT固件，自动切换到仅APP模式");
            }
            
            // 烧录BOOT（如果需要）
            if (burnBootAndApp && hasBootFirmware)
            {
                _logService?.LogInfo("智能烧录", "开始烧录BOOT固件...");
                ShowStatus("正在烧录BOOT固件...", PackIconKind.Flash);
                _smartBurnWindow?.AddLog("开始烧录BOOT固件");
                _smartBurnWindow?.UpdateStatus(
                    "烧录BOOT固件",
                    "正在写入BOOT固件到芯片",
                    PackIconKind.Flash,
                    Color.FromRgb(255, 152, 0)
                );
                
                var bootProgress = new Progress<int>(value =>
                {
                    _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        OperationProgressBar.Value = value / 2; // BOOT占50%
                        ShowStatus($"烧录BOOT: {value}%", PackIconKind.Flash, 0);
                        _smartBurnWindow?.UpdateBootProgress(value);
                    }));
                });
                
                bool bootSuccess = await _deviceService.ProgramFirmwareAsync(_bootFirmware!, bootProgress);
                
                if (!bootSuccess || cancellationToken.IsCancellationRequested)
                {
                    _logService?.LogError("智能烧录", "BOOT固件烧录失败");
                    return false;
                }
                
                _logService?.LogInfo("智能烧录", "BOOT固件烧录成功");
                _smartBurnWindow?.AddLog("BOOT固件烧录成功");
                
                // 短暂延迟
                await Task.Delay(500, cancellationToken);
            }
            
            // 烧录APP
            _logService?.LogInfo("智能烧录", "开始烧录APP固件...");
            ShowStatus("正在烧录APP固件...", PackIconKind.Flash);
            _smartBurnWindow?.AddLog("开始烧录APP固件");
            _smartBurnWindow?.UpdateStatus(
                "烧录APP固件",
                "正在写入APP固件到芯片",
                PackIconKind.Flash,
                Color.FromRgb(3, 169, 244)
            );
            
            var appProgress = new Progress<int>(value =>
            {
                _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 根据模式计算进度
                    int progressValue = burnBootAndApp ? 50 + (value / 2) : value;
                    OperationProgressBar.Value = progressValue;
                    ShowStatus($"烧录APP: {value}%", PackIconKind.Flash, 0);
                    _smartBurnWindow?.UpdateAppProgress(value);
                }));
            });
            
            bool appSuccess = await _deviceService.ProgramFirmwareAsync(_appFirmware, appProgress);
            
            if (!appSuccess || cancellationToken.IsCancellationRequested)
            {
                _logService?.LogError("智能烧录", "APP固件烧录失败");
                return false;
            }
            
            // 烧录成功（CLI工具的-v参数已在烧录过程中完成校验）
            _logService?.LogInfo("智能烧录", "APP固件烧录并校验成功");
            _smartBurnWindow?.AddLog("APP固件烧录并校验成功（烧录过程中已完成数据校验）");
            
            // 重置进度条
            _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                OperationProgressBar.Value = 100;
            }));
            
            return true;
        }
        catch (Exception ex)
        {
            _logService?.LogError("智能烧录", $"烧录过程出错: {ex.Message}");
            _smartBurnWindow?.AddLog($"烧录出错: {ex.Message}");
            return false;
        }
    }
    
    // ==================== 智能自动烧录功能结束 ====================
    
    private async Task<bool> BurnFirmwareAsync(FirmwareFile firmware)
    {
        if (_isBurning || _deviceService == null)
            return false;
        
        try
        {
            _isBurning = true;
            SetControlsEnabled(false);
            
            // 显示进度条并添加进度文本标签
            OperationProgressBar.Visibility = Visibility.Visible;
            OperationProgressBar.Value = 0;
            
            string prepareMessage = $"准备烧写{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件...";
            ShowStatus(prepareMessage, PackIconKind.Flash, 5000);
            
            // 验证固件信息
            if (firmware.Type == FirmwareType.Boot)
            {
                UpdateBootFirmwareInfo(firmware);
            }
            else if (firmware.Type == FirmwareType.App)
            {
                UpdateAppFirmwareInfo(firmware);
            }
            
            // 创建统一的烧录对话框内容
            var confirmAndProgressContent = CreateCombinedBurnSheet(firmware);
            bool? burnConfirmed = false;
                
                // 确保设备已连接
                if (_currentDevice?.Status != ConnectionStatus.Connected || !_currentDevice.IsChipConnected)
                {
                LogMessage($"{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}烧写前检测到设备未连接，正在尝试重新连接...");
                    await RefreshConnectionStatusAsync();
                    
                    if (_currentDevice?.Status != ConnectionStatus.Connected || !_currentDevice.IsChipConnected)
                    {
                    LogMessage($"设备未连接，无法烧写{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件", LogLevel.Error);
                    
                    // 使用统一的对话框显示连接错误
                    confirmAndProgressContent.Title = "连接错误";
                    confirmAndProgressContent.Message = "设备未连接或无法识别芯片，请检查硬件连接后重试。";
                    confirmAndProgressContent.Icon = PackIconKind.Error;
                    confirmAndProgressContent.PrimaryButtonText = "确定";
                    confirmAndProgressContent.IsError = true;
                    confirmAndProgressContent.ShowProgress = false;
                    
                    await ShowBottomSheetAsync(confirmAndProgressContent);
                        return false;
                    }
                }
            
            // 添加BOOT固件的额外验证
            if (firmware.Type == FirmwareType.Boot)
            {
                // 验证BOOT固件大小是否合理
                if (firmware.FileSize > 131072) // 128KB
                {
                    // 更新确认内容为警告
                    confirmAndProgressContent.Title = "固件大小警告";
                    confirmAndProgressContent.Message = $"警告: BOOT固件大小为{firmware.FileSize / 1024}KB，可能超出Boot区大小限制。\n\n是否要继续烧录？";
                    confirmAndProgressContent.Icon = PackIconKind.AlertOutline;
                    confirmAndProgressContent.IsWarning = true;
                    confirmAndProgressContent.ShowProgress = false;
                    
                    burnConfirmed = await ShowBottomSheetAsync(confirmAndProgressContent);
                    if (burnConfirmed != true)
                    {
                        LogMessage("用户取消了BOOT固件烧写操作");
                    return false;
                    }
                }
                
                // 更新确认内容为BOOT烧录确认
                confirmAndProgressContent.Title = "BOOT固件烧录确认";
                confirmAndProgressContent.Message = "烧写BOOT固件将擦除芯片上的所有数据，包括应用程序。\n\n此操作不可逆，请确保您已备份重要数据。\n\n是否确定要继续？";
                confirmAndProgressContent.Icon = PackIconKind.Eraser;
                confirmAndProgressContent.IsWarning = true;
                confirmAndProgressContent.ShowProgress = false;
            }
            // 添加APP固件的确认内容
            else if (firmware.Type == FirmwareType.App)
            {
                // 更新确认内容为APP烧录确认
                confirmAndProgressContent.Title = "APP固件烧录确认";
                confirmAndProgressContent.Message = "确定要将应用程序固件烧写到设备吗？\n\n注意：这将覆盖设备上当前的应用程序。";
                confirmAndProgressContent.Icon = PackIconKind.AlertCircle;
                confirmAndProgressContent.IsWarning = true;
                confirmAndProgressContent.ShowProgress = false;
            }
            
            // 显示确认对话框
            if (burnConfirmed != true) // 如果之前没有确认过（BOOT大小警告）
            {
                burnConfirmed = await ShowBottomSheetAsync(confirmAndProgressContent);
                if (burnConfirmed != true)
                {
                    LogMessage($"用户取消了{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件烧写操作");
                        return false;
                }
            }
            
            // 用户已确认，更新对话框为进度显示状态
            confirmAndProgressContent.Title = $"烧写{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件";
            confirmAndProgressContent.Message = $"正在烧写{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件到设备...";
            confirmAndProgressContent.Icon = PackIconKind.FlashAuto;
            confirmAndProgressContent.PrimaryButtonText = ""; // 移除按钮
            confirmAndProgressContent.SecondaryButtonText = ""; // 移除按钮
            confirmAndProgressContent.IsWarning = false;
            confirmAndProgressContent.IsSuccess = false;
            confirmAndProgressContent.ShowProgress = true; // 显示进度条
            confirmAndProgressContent.ProgressValue = 0;
            confirmAndProgressContent.ProgressStage = "正在准备...";
            
            // 使用同一个对话框Host ID
            string dialogHostId = "BurnDialogHost";
            
            // 异步显示进度对话框（不等待对话框关闭）
            var dialogTask = ShowBottomSheetWithoutWaitingAsync(confirmAndProgressContent, dialogHostId);
            
            string burningMessage = $"正在烧写{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件...";
            ShowStatus(burningMessage, PackIconKind.FlashAuto, 0); // 持续显示，直到操作完成
            
            // 定义进度报告回调
            var progress = new Progress<int>(value =>
            {
                // 直接在UI线程上更新进度条和状态文本
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 更新主界面进度条
                    OperationProgressBar.Value = value;
                    
                    // 更新状态文本，显示当前阶段和百分比
                    string stage = "";
                    if (value < 30) stage = "正在准备...";
                    else if (value < 50) stage = "正在擦除...";
                    else if (value < 70) stage = "正在写入...";
                    else if (value < 90) stage = "正在校验...";
                    else stage = "即将完成...";
                    
                    string progressMessage = $"{stage} ({value}%)";
                    ShowStatus(progressMessage, PackIconKind.FlashAuto, 0); // 持续显示最新进度
                    
                    // 更新对话框中的进度
                    UpdateDialogProgress(value, stage, dialogHostId);
                }), System.Windows.Threading.DispatcherPriority.Send); // 提高优先级到Send
            });
            
            // 开始BOOT烧录前的特殊准备(如强制擦除)
            if (firmware.Type == FirmwareType.Boot)
            {
                LogMessage("开始BOOT固件烧写前准备...");
                // 设置更长超时
                _deviceService.ShowConnectionLogs = true;
            }
            
            // 执行烧写
            bool success = await _deviceService.ProgramFirmwareAsync(firmware, progress);
            
            // 关闭进度对话框
            CloseDialogHost(dialogHostId);
            
            // 完成后恢复设置
            _deviceService.ShowConnectionLogs = false;
            
            if (success)
            {
                // 烧录成功（CLI工具的-v参数已在烧录过程中完成校验）
                string successMessage = $"{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件烧写并校验成功";
                ShowStatus(successMessage, PackIconKind.CheckCircle, 5000);
                LogMessage($"固件烧写并校验成功: {firmware.FileName}");
                
                // 更新结果对话框内容
                confirmAndProgressContent.Title = $"{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件烧录成功";
                confirmAndProgressContent.Message = firmware.Type == FirmwareType.Boot 
                    ? "BOOT固件已成功烧写并校验通过！\n\n烧录过程中已完成数据校验。\n\n建议重新上电设备以确保固件正常运行。"
                    : "应用程序固件已成功烧写并校验通过！\n\n烧录过程中已完成数据校验。\n\n设备现在可以正常使用新的应用程序。";
                confirmAndProgressContent.Icon = PackIconKind.ShieldCheckOutline;
                confirmAndProgressContent.PrimaryButtonText = "确定";
                confirmAndProgressContent.IsSuccess = true;
                confirmAndProgressContent.ShowProgress = false;
                
                await ShowBottomSheetAsync(confirmAndProgressContent);
            }
            else
            {
                string failMessage = $"{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件烧写失败";
                ShowStatus(failMessage, PackIconKind.Close, 5000);
                LogMessage($"固件烧写失败: {firmware.FileName}", LogLevel.Error);
                
                // 错误指南文本
                string errorGuide = firmware.Type == FirmwareType.Boot
                    ? "BOOT固件烧写失败，可能的原因和解决方法：\n\n" +
                        "1. 检查连接：确保ST-LINK与电脑和芯片连接良好\n" +
                        "2. 芯片供电：确保芯片有足够的电源供应\n" +
                        "3. 芯片锁定：芯片可能已锁定，尝试完全断电重启\n" +
                        "4. 固件文件：确认BOOT固件文件适用于当前芯片型号\n" +
                        "5. 烧录工具：尝试重新安装STM32CubeProgrammer\n\n" +
                      "是否尝试恢复连接并重试？"
                    : "APP固件烧写失败，可能的原因和解决方法：\n\n" +
                        "1. 检查连接：确保ST-LINK与电脑和芯片连接良好\n" +
                        "2. 芯片供电：确保芯片有足够的电源供应\n" +
                        "3. 固件文件：确认APP固件文件适用于当前芯片型号\n" +
                        "4. 地址冲突：APP固件可能与BOOT固件地址存在冲突\n\n" +
                        "是否尝试恢复连接并重试？";
                    
                // 更新失败对话框内容
                confirmAndProgressContent.Title = $"{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}烧录失败";
                confirmAndProgressContent.Message = errorGuide;
                confirmAndProgressContent.Icon = PackIconKind.AlertCircleOutline;
                confirmAndProgressContent.PrimaryButtonText = "重试";
                confirmAndProgressContent.SecondaryButtonText = "取消";
                confirmAndProgressContent.IsError = true;
                confirmAndProgressContent.ShowProgress = false;
                
                bool? retryResult = await ShowBottomSheetAsync(confirmAndProgressContent);
                if (retryResult == true)
                    {
                        // 尝试重新连接
                        await RefreshConnectionStatusAsync();
                        
                        // 如果设备已连接，再次尝试烧录
                        if (_currentDevice?.Status == ConnectionStatus.Connected && _currentDevice.IsChipConnected)
                        {
                            LogMessage("重新连接成功，将再次尝试烧写...");
                            return await BurnFirmwareAsync(firmware);
                    }
                }
            }
            
            return success;
        }
        catch (Exception ex)
        {
            LogMessage($"烧写固件时出错: {ex.Message}", LogLevel.Error);
            ShowStatus("烧写固件出错", PackIconKind.Alert, 5000);
            
            // 使用Material Design 3底部弹出框显示异常错误
            var exceptionSheet = new CombinedDialogContent
            {
                Title = "烧写过程中发生错误",
                Message = $"烧写过程中发生错误: {ex.Message}",
                Icon = PackIconKind.AlertOctagonOutline,
                PrimaryButtonText = "确定",
                IsError = true,
                ShowProgress = false
            };
            
            await ShowBottomSheetAsync(exceptionSheet);
            return false;
        }
        finally
        {
            _isBurning = false;
            OperationProgressBar.Visibility = Visibility.Collapsed;
            SetControlsEnabled(true);
            
            // 烧录完成后刷新连接状态
            await RefreshConnectionStatusAsync();
        }
    }
    
    // 组合对话框内容类
    public class CombinedDialogContent
    {
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public PackIconKind Icon { get; set; } = PackIconKind.Information;
        public string PrimaryButtonText { get; set; } = "";
        public string SecondaryButtonText { get; set; } = "";
        public bool IsWarning { get; set; } = false;
        public bool IsError { get; set; } = false;
        public bool IsSuccess { get; set; } = false;
        public SolidColorBrush IconColor { get; set; } = new SolidColorBrush(Colors.Blue);
        public UIElement? CustomContent { get; set; } = default;
        // 新增进度条相关属性
        public bool ShowProgress { get; set; } = false;
        public int ProgressValue { get; set; } = 0;
        public string ProgressStage { get; set; } = "";
        // 固件信息
        public FirmwareFile? Firmware { get; set; } = default;
    }

    // 创建组合烧录对话框内容
    private CombinedDialogContent CreateCombinedBurnSheet(FirmwareFile firmware)
    {
        return new CombinedDialogContent
        {
            Title = $"{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件烧录",
            Message = $"准备烧写{(firmware.Type == FirmwareType.Boot ? "BOOT" : "APP")}固件到设备...",
            Icon = PackIconKind.Flash,
            PrimaryButtonText = "开始烧录",
            SecondaryButtonText = "取消",
            IsWarning = false,
            ShowProgress = false,
            Firmware = firmware
        };
    }

    // 显示底部弹出框但不等待结果（用于进度显示）
    private async Task ShowBottomSheetWithoutWaitingAsync(CombinedDialogContent content, string dialogHostId)
    {
        // 创建底部弹出框内容
        var bottomSheetContent = new StackPanel { Margin = new Thickness(24, 0, 24, 24) };
        
        // 添加拖动条
        var dragHandle = new Border { Style = FindResource("BottomSheetDragHandleStyle") as Style };
        bottomSheetContent.Children.Add(dragHandle);
        
        // 标题和图标区域
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        // 设置图标颜色
        if (content.IsWarning) content.IconColor = new SolidColorBrush(Colors.Orange);
        else if (content.IsError) content.IconColor = new SolidColorBrush(Colors.Red);
        else if (content.IsSuccess) content.IconColor = new SolidColorBrush(Colors.Green);
        
        // 创建图标
        var icon = new PackIcon
        {
            Kind = content.Icon,
            Width = 24,
            Height = 24,
            Foreground = content.IconColor,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        
        Grid.SetColumn(icon, 0);
        headerGrid.Children.Add(icon);
        
        // 创建标题
        var title = new TextBlock
        {
            Text = content.Title,
            FontSize = 20,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        
        Grid.SetColumn(title, 1);
        headerGrid.Children.Add(title);
        
        // 添加标题区域
        bottomSheetContent.Children.Add(headerGrid);
        
        // 创建消息文本
        if (!string.IsNullOrEmpty(content.Message))
        {
        var message = new TextBlock
        {
                Text = content.Message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 16, 0, 24),
                LineHeight = 20
            };
            
            bottomSheetContent.Children.Add(message);
        }
        
        // 添加固件信息（如果有）
        if (content.Firmware != null)
        {
            // 固件信息
            var infoPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            
            // 固件类型
            var firmwareTypeText = new TextBlock
            {
                Text = $"固件类型: {(content.Firmware.Type == FirmwareType.Boot ? "BOOT固件" : "APP固件")}",
                Margin = new Thickness(0, 4, 0, 4)
            };
            infoPanel.Children.Add(firmwareTypeText);
            
            // 固件名称
            var firmwareNameText = new TextBlock
            {
                Text = $"文件名称: {content.Firmware.FileName}",
                Margin = new Thickness(0, 4, 0, 4)
            };
            infoPanel.Children.Add(firmwareNameText);
            
            // 固件大小
            var fileSizeText = new TextBlock
            {
                Text = $"文件大小: {content.Firmware.FileSize / 1024} KB",
                Margin = new Thickness(0, 4, 0, 4)
            };
            infoPanel.Children.Add(fileSizeText);
            
            bottomSheetContent.Children.Add(infoPanel);
        }
        
        // 添加自定义内容（如果有）
        if (content.CustomContent != null)
        {
            bottomSheetContent.Children.Add(content.CustomContent);
        }
        
        // 添加进度条（如果启用）
        if (content.ShowProgress)
        {
            // 添加进度条
            var progressBar = new System.Windows.Controls.ProgressBar
            {
                Name = "DialogProgressBar",
                Value = content.ProgressValue,
                Height = 8,
                Margin = new Thickness(0, 8, 0, 8),
                Style = Application.Current.Resources["MaterialDesignLinearProgressBar"] as Style
            };
            bottomSheetContent.Children.Add(progressBar);
            
            // 添加进度文本
            var progressText = new TextBlock
            {
                Name = "DialogProgressText",
                Text = content.ProgressStage,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 16)
            };
            bottomSheetContent.Children.Add(progressText);
        }
        
        // 创建按钮面板
        if (!string.IsNullOrEmpty(content.PrimaryButtonText) || !string.IsNullOrEmpty(content.SecondaryButtonText))
        {
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
        };
        
        // 创建取消按钮
            if (!string.IsNullOrEmpty(content.SecondaryButtonText))
        {
            var secondaryButton = new Button
            {
                    Content = content.SecondaryButtonText,
                Style = Application.Current.Resources["MaterialDesignOutlinedButton"] as Style,
                Margin = new Thickness(8),
                IsCancel = true,
                Tag = false
            };
            
            secondaryButton.Click += (s, e) =>
            {
                    DialogHost.Close(dialogHostId, false);
            };
            
            buttonPanel.Children.Add(secondaryButton);
        }
        
        // 创建确认按钮
            if (!string.IsNullOrEmpty(content.PrimaryButtonText))
            {
        var primaryButton = new Button
        {
                    Content = content.PrimaryButtonText,
            Style = Application.Current.Resources["MaterialDesignRaisedButton"] as Style,
            Margin = new Thickness(8),
            IsDefault = true,
            Tag = true
        };
                
                // 根据类型设置按钮颜色
                if (content.IsWarning)
                {
                    primaryButton.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // 橙色
                }
                else if (content.IsError)
                {
                    primaryButton.Background = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // 红色
                }
                else if (content.IsSuccess)
                {
                    primaryButton.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
                }
        
        primaryButton.Click += (s, e) =>
        {
                    DialogHost.Close(dialogHostId, true);
        };
        
        buttonPanel.Children.Add(primaryButton);
            }
            
            bottomSheetContent.Children.Add(buttonPanel);
        }
        
        // 创建底部弹出框容器
        var bottomSheetContainer = new Border
        {
            Style = FindResource("BottomSheetStyle") as Style,
            Child = bottomSheetContent,
            MinHeight = 200,
            MaxHeight = 600,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0)
        };
        
        // 异步显示底部弹出框
        try
        {
            // 确保对话框宿主已存在且可用
            if(dialogHostId == "BurnDialogHost")
            {
                // 总是使用RootDialog作为备用
                await DialogHost.Show(bottomSheetContainer, "RootDialog");
            }
            else
            {
                await DialogHost.Show(bottomSheetContainer, dialogHostId);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"显示底部弹出框时出错: {ex.Message}", LogLevel.Error);
            // 如果出错，尝试使用默认对话框宿主
            try
            {
                await DialogHost.Show(bottomSheetContainer, "RootDialog");
            }
            catch
            {
                LogMessage("无法显示对话框，可能UI组件尚未初始化", LogLevel.Error);
            }
        }
    }

    // 显示底部弹出框并等待结果
    private async Task<bool?> ShowBottomSheetAsync(CombinedDialogContent content)
    {
        // 创建底部弹出框内容
        var bottomSheetContent = new StackPanel { Margin = new Thickness(24, 0, 24, 24) };
        
        // 添加拖动条
        var dragHandle = new Border { Style = FindResource("BottomSheetDragHandleStyle") as Style };
        bottomSheetContent.Children.Add(dragHandle);
        
        // 标题和图标区域
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        // 设置图标颜色
        if (content.IsWarning) content.IconColor = new SolidColorBrush(Colors.Orange);
        else if (content.IsError) content.IconColor = new SolidColorBrush(Colors.Red);
        else if (content.IsSuccess) content.IconColor = new SolidColorBrush(Colors.Green);
        
        // 创建图标
        var icon = new PackIcon
        {
            Kind = content.Icon,
            Width = 24,
            Height = 24,
            Foreground = content.IconColor,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        
        Grid.SetColumn(icon, 0);
        headerGrid.Children.Add(icon);
        
        // 创建标题
        var title = new TextBlock
        {
            Text = content.Title,
            FontSize = 20,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        
        Grid.SetColumn(title, 1);
        headerGrid.Children.Add(title);
        
        // 添加标题区域
        bottomSheetContent.Children.Add(headerGrid);
        
        // 创建消息文本
        if (!string.IsNullOrEmpty(content.Message))
        {
            var message = new TextBlock
            {
                Text = content.Message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 16, 0, 24),
                LineHeight = 20
            };
            
            bottomSheetContent.Children.Add(message);
        }
        
        // 添加固件信息（如果有）
        if (content.Firmware != null)
        {
            // 固件信息
            var infoPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            
            // 固件类型
            var firmwareTypeText = new TextBlock
            {
                Text = $"固件类型: {(content.Firmware.Type == FirmwareType.Boot ? "BOOT固件" : "APP固件")}",
                Margin = new Thickness(0, 4, 0, 4)
            };
            infoPanel.Children.Add(firmwareTypeText);
            
            // 固件名称
            var firmwareNameText = new TextBlock
            {
                Text = $"文件名称: {content.Firmware.FileName}",
                Margin = new Thickness(0, 4, 0, 4)
            };
            infoPanel.Children.Add(firmwareNameText);
            
            // 固件大小
            var fileSizeText = new TextBlock
            {
                Text = $"文件大小: {content.Firmware.FileSize / 1024} KB",
                Margin = new Thickness(0, 4, 0, 4)
            };
            infoPanel.Children.Add(fileSizeText);
            
            bottomSheetContent.Children.Add(infoPanel);
        }
        
        // 添加自定义内容（如果有）
        if (content.CustomContent != null)
        {
            bottomSheetContent.Children.Add(content.CustomContent);
        }
        
        // 添加进度条（如果启用）
        if (content.ShowProgress)
        {
            // 添加进度条
            var progressBar = new System.Windows.Controls.ProgressBar
            {
                Name = "DialogProgressBar",
                Value = content.ProgressValue,
                Height = 8,
                Margin = new Thickness(0, 8, 0, 8),
                Style = Application.Current.Resources["MaterialDesignLinearProgressBar"] as Style
            };
            bottomSheetContent.Children.Add(progressBar);
            
            // 添加进度文本
            var progressText = new TextBlock
            {
                Name = "DialogProgressText",
                Text = content.ProgressStage,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 16)
            };
            bottomSheetContent.Children.Add(progressText);
        }
        
        // 创建按钮面板
        if (!string.IsNullOrEmpty(content.PrimaryButtonText) || !string.IsNullOrEmpty(content.SecondaryButtonText))
        {
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            
            // 创建取消按钮
            if (!string.IsNullOrEmpty(content.SecondaryButtonText))
            {
                var secondaryButton = new Button
                {
                    Content = content.SecondaryButtonText,
                    Style = Application.Current.Resources["MaterialDesignOutlinedButton"] as Style,
                    Margin = new Thickness(8),
                    IsCancel = true,
                    Tag = false
                };
                
                secondaryButton.Click += (s, e) =>
                {
                    DialogHost.Close("RootDialog", false);
                };
                
                buttonPanel.Children.Add(secondaryButton);
            }
            
            // 创建确认按钮
            if (!string.IsNullOrEmpty(content.PrimaryButtonText))
            {
                var primaryButton = new Button
                {
                    Content = content.PrimaryButtonText,
                    Style = FindResource("MaterialDesignRaisedButton") as Style,
                    Margin = new Thickness(8),
                    IsDefault = true,
                    Tag = true
                };
                
                // 根据类型设置按钮颜色
                if (content.IsWarning)
                {
                    primaryButton.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // 橙色
                }
                else if (content.IsError)
                {
                    primaryButton.Background = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // 红色
                }
                else if (content.IsSuccess)
                {
                    primaryButton.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
                }
        
        primaryButton.Click += (s, e) =>
        {
                    DialogHost.Close("RootDialog", true);
        };
        
        buttonPanel.Children.Add(primaryButton);
            }
            
            bottomSheetContent.Children.Add(buttonPanel);
        }
        
        // 创建底部弹出框容器
        var bottomSheetContainer = new Border
        {
            Style = FindResource("BottomSheetStyle") as Style,
            Child = bottomSheetContent,
            MinHeight = 200,
            MaxHeight = 600,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0)
        };
        
        // 异步显示底部弹出框并等待结果
        try
        {
            return await DialogHost.Show(bottomSheetContainer, "RootDialog") as bool?;
        }
        catch (Exception ex)
        {
            LogMessage($"显示底部弹出框时出错: {ex.Message}", LogLevel.Error);
            return null;
        }
    }

    // 更新对话框中的进度
    private void UpdateDialogProgress(int value, string stage, string dialogHostId)
    {
        try
        {
            // 由于MaterialDesignThemes.Wpf版本可能不同，直接找到对话框内的控件会比较困难
            // 使用最简单的方式：直接找到名为DialogProgressBar和DialogProgressText的控件
            foreach (Window window in Application.Current.Windows)
            {
                var progressBar = FindChild<ProgressBar>(window, "DialogProgressBar");
                var progressText = FindChild<TextBlock>(window, "DialogProgressText");
                
                if (progressBar != null)
                {
                    progressBar.Value = value;
                }
                
                if (progressText != null)
                {
                    progressText.Text = $"{stage} ({value}%)";
                }
                
                // 如果找到了这些控件，就不需要继续搜索了
                if (progressBar != null || progressText != null)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"更新对话框进度时出错: {ex.Message}", LogLevel.Error);
        }
    }

    // 通用查找子控件方法
    private T FindChild<T>(DependencyObject parent, string? childName = null) where T : DependencyObject
    {
        if (parent == null) return default!;
        
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            
            // 如果是要查找的类型
            if (child is T typedChild)
            {
                // 如果指定了名称且匹配，或者没有指定名称
                if (string.IsNullOrEmpty(childName) || 
                    (child is FrameworkElement fe && fe.Name == childName))
                {
                    return typedChild;
                }
            }
            
            // 递归查找
            var result = FindChild<T>(child, childName);
            if (result != null)
                return result;
        }
        
        return default!;
    }

    // 关闭指定的对话框
    private void CloseDialogHost(string dialogHostId)
    {
        try
        {
            // 直接使用DialogHost.Close关闭对话框，无需先查找
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // 如果是BurnDialogHost，始终使用RootDialog作为备用
                    if (dialogHostId == "BurnDialogHost")
                    {
                        DialogHost.Close("RootDialog");
                    }
                    else
                    {
                        DialogHost.Close(dialogHostId);
                    }
                }
                catch
                {
                    // 如果指定的对话框不存在，尝试关闭所有打开的对话框
                    try
                    {
                        DialogHost.Close("RootDialog");
                    }
                    catch
                    {
                        LogMessage("无法关闭对话框，可能对话框已关闭或不存在", LogLevel.Debug);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            LogMessage($"关闭对话框时出错: {ex.Message}", LogLevel.Error);
        }
    }

    // 处理从外部服务接收到的日志
    private void OnExternalLogMessage(object? sender, string message)
    {
        // 避免递归调用，直接在UI中更新日志而不是再次调用LogMessage
        if (sender is LogService)
        {
            // 已经从LogService接收到的消息，不需要再次记录
            return;
        }
        
        // 只处理从其他服务（如DeviceService、FirmwareService等）发来的消息
        LogMessage(message);
    }
    
    private void LogMessage(string message)
    {
        // 防止递归调用
        if (_isLogging) return;
        
        try
        {
            _isLogging = true;
            
            // 检查是否包含敏感烧写命令内容
            if (message.Contains("尝试烧写命令:") || message.Contains("尝试烧写命令："))
            {
                message = "正在尝试烧录...";
            }
            
            _logService?.LogInfo("主窗口", message);
            
            // 添加日志后滚动到底部
            ScrollToBottom();
        }
        finally
        {
            _isLogging = false;
        }
    }
    
    private void LogMessage(string message, LogLevel level)
    {
        // 防止递归调用
        if (_isLogging) return;
        
        try
        {
            _isLogging = true;
            
            // 检查是否包含敏感烧写命令内容
            if (message.Contains("尝试烧写命令:") || message.Contains("尝试烧写命令："))
            {
                message = "正在尝试烧录...";
            }
            
            switch (level)
            {
                case LogLevel.Debug:
                    _logService?.LogDebug("主窗口", message);
                    break;
                case LogLevel.Info:
                    _logService?.LogInfo("主窗口", message);
                    break;
                case LogLevel.Warning:
                    _logService?.LogWarning("主窗口", message);
                    break;
                case LogLevel.Error:
                    _logService?.LogError("主窗口", message);
                    break;
                case LogLevel.Critical:
                    _logService?.LogCritical("主窗口", message);
                    break;
                default:
                    _logService?.LogInfo("主窗口", message);
                    break;
            }
            
            // 添加日志后滚动到底部
            ScrollToBottom();
        }
        finally
        {
            _isLogging = false;
        }
    }
    
    private void OperationLogMessage(string message)
    {
        // 防止递归调用
        if (_isLogging) return;
        
        try
        {
            _isLogging = true;
            
            // 检查是否包含敏感烧写命令内容
            if (message.Contains("尝试烧写命令:") || message.Contains("尝试烧写命令："))
            {
                message = "正在尝试烧录...";
            }
            
            _logService?.LogInfo("操作", message);
            
            // 添加日志后滚动到底部
            ScrollToBottom();
        }
        finally
        {
            _isLogging = false;
        }
    }
    
    // 添加ScrollToBottom方法，滚动日志到底部
    private void ScrollToBottom()
    {
        // 由于ItemsControl的ScrollViewer是在Template中定义的，需要通过查找获取
        if (OperationLogTextBox != null)
        {
            // 等待UI更新
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // 找到ItemsControl中的ScrollViewer
                var scrollViewer = FindScrollViewer(OperationLogTextBox);
                if (scrollViewer != null)
                {
                    // 滚动到底部
                    scrollViewer.ScrollToEnd();
                }
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }
    
    // 查找控件中的ScrollViewer
    private ScrollViewer? FindScrollViewer(DependencyObject control)
    {
        if (control == null) return default;
        
        // 如果控件本身就是ScrollViewer
        if (control is ScrollViewer scrollViewer)
            return scrollViewer;
        
        // 遍历子元素
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(control); i++)
        {
            var child = VisualTreeHelper.GetChild(control, i);
            
            // 递归查找
            var result = FindScrollViewer(child);
            if (result != null)
                return result;
        }
        
        return default;
    }
    
    // 初始化日志过滤控件
    private void InitializeLogFilters()
    {
        // 初始化日志级别下拉框
        OperationLogLevelComboBox.ItemsSource = Enum.GetValues(typeof(LogLevel));
        
        // 默认选择Info级别
        OperationLogLevelComboBox.SelectedItem = LogLevel.Info;
        
        // 应用初始过滤
        ApplyOperationLogFilter();
    }
    
    // 操作日志级别过滤器变更事件
    private void OperationLogLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyOperationLogFilter();
    }
    
    // 操作日志文本过滤器变更事件
    private void OperationLogFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyOperationLogFilter();
    }
    
    // 应用操作日志过滤器
    private void ApplyOperationLogFilter()
    {
        if (OperationLogLevelComboBox.SelectedItem == null || _logService == null) return;
        
        // 获取过滤参数
        LogLevel selectedLevel = (LogLevel)OperationLogLevelComboBox.SelectedItem;
        string textFilter = OperationLogFilterTextBox.Text.Trim().ToLower();
        
        // 创建过滤后的集合视图
        var view = CollectionViewSource.GetDefaultView(_logService.OperationLogEntries);
        view.Filter = log => 
        {
            if (log is LogEntry entry)
            {
                // 级别过滤
                if (entry.Level < selectedLevel) return false;
                
                // 文本过滤
                if (!string.IsNullOrEmpty(textFilter))
                {
                    return entry.Category.ToLower().Contains(textFilter) ||
                           entry.Message.ToLower().Contains(textFilter);
                }
                
                return true;
            }
            return false;
        };
        
        // 应用过滤
        OperationLogTextBox.ItemsSource = view;
        
        // 过滤完成后滚动到底部
        ScrollToBottom();
    }
    
    // 清除操作日志
    private void ClearOperationLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logService == null) return;
        
        _logService.OperationLogEntries.Clear();
        _logService.LogInfo("主窗口", "操作日志已清除");
    }

    // 设备状态变化事件处理
    private void OnDeviceStatusChanged(object? sender, STLinkDevice e)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                // 更新当前设备状态
                _currentDevice = e;
                
                // 更新UI显示
                UpdateConnectionStatus();
                
                // 更新自动刷新状态显示（设备可能在回调中修改了自动刷新状态）
                UpdateAutoRefreshStatus(_deviceService?.AutoRefreshEnabled ?? false);
                
                // 注意：不在状态栏显示设备连接状态信息
                // 状态栏仅用于显示烧录等操作信息
            });
        }
        catch (Exception ex)
        {
            LogMessage($"处理设备状态变化时出错: {ex.Message}", LogLevel.Error);
        }
    }
    
    // 更新自动刷新状态显示
    private void UpdateAutoRefreshStatus(bool isEnabled)
    {
        try
        {
            // 确保UI线程更新
            Dispatcher.Invoke(() =>
    {
        if (isEnabled)
        {
                    // 自动刷新开启
                    if (ClockIcon != null)
                    {
                        ClockIcon.Kind = PackIconKind.Clock;
                        ClockIcon.Foreground = _greenBrush;
                    }
                    
                    if (AutoRefreshStatusText != null)
                    {
                        AutoRefreshStatusText.Text = "自动刷新已开启";
            AutoRefreshStatusText.Foreground = _greenBrush;
                    }
                    
                    if (AutoRefreshToggle != null)
                    {
                        AutoRefreshToggle.Content = "关闭自动刷新";
                    }
        }
        else
        {
                    // 自动刷新关闭
                    if (ClockIcon != null)
                    {
                        ClockIcon.Kind = PackIconKind.ClockOutline;
                        ClockIcon.Foreground = _yellowBrush;
                    }
                    
                    if (AutoRefreshStatusText != null)
                    {
                        AutoRefreshStatusText.Text = "自动刷新已关闭";
                        AutoRefreshStatusText.Foreground = _redBrush;
                    }
                    
                    if (AutoRefreshToggle != null)
                    {
                        AutoRefreshToggle.Content = "开启自动刷新";
                    }
                }
            });
        }
        catch (Exception ex)
        {
            LogMessage($"更新自动刷新状态时出错: {ex.Message}", LogLevel.Error);
        }
    }
    
    // 切换自动刷新状态的按钮点击事件
    private void ToggleAutoRefresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_deviceService == null) return;
            
            // 切换自动刷新
            _deviceService.ToggleAutoRefresh();
            
            // 更新自动刷新状态显示
            UpdateAutoRefreshStatus(_deviceService.AutoRefreshEnabled);
            
            LogMessage($"自动刷新已{(_deviceService.AutoRefreshEnabled ? "启用" : "禁用")}");
            
            // 添加下面的通知
            if (_deviceService.AutoRefreshEnabled)
            {
                ShowNotification("已启用自动刷新", PackIconKind.CheckCircle);
            }
            else
            {
                ShowNotification("已禁用自动刷新", PackIconKind.ClockOutline);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"切换自动刷新状态时出错: {ex.Message}", LogLevel.Error);
        }
    }
    
    // 打开日志查看器
    private void OpenLogViewer()
    {
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_logs");
        if (Directory.Exists(logPath))
        {
            Process.Start("explorer.exe", logPath);
        }
        else
        {
            ShowNotification("日志目录不存在", PackIconKind.AlertCircle);
        }
    }
    
    // 显示关于对话框
    private void ShowAboutDialog()
    {
        System.Windows.MessageBox.Show(
            "STM32Programmer\n版本：1.0\n\n用于STM32芯片的固件烧录工具\n使用STM32CubeProgrammer实现烧录功能", 
            "关于", 
            MessageBoxButton.OK, 
            MessageBoxImage.Information);
    }
    
    // 显示通知消息
    public void ShowNotification(string message, PackIconKind iconKind = PackIconKind.Information, int durationMs = 3000)
    {
        var messageQueue = MainSnackbar.MessageQueue;
        
        if (messageQueue != null)
        {
            // 直接检查是否在UI线程上
            if (Application.Current.Dispatcher.CheckAccess())
            {
                // 当前已在UI线程上，直接执行
                var icon = new PackIcon { Kind = iconKind, Margin = new Thickness(0, 0, 8, 0) };
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(icon);
                panel.Children.Add(new TextBlock { Text = message, FontSize = 14 });
                
                // 使用清除队列后直接添加消息的方式实现最新消息立即显示
                messageQueue.Clear(); // 清除现有队列
                
                // 使用正确的参数顺序调用Enqueue方法
                messageQueue.Enqueue(
                    panel,              // 内容
                    null,               // 操作回调
                    null,               // 操作内容
                    TimeSpan.FromMilliseconds(durationMs) // 显示持续时间
                );
            }
            else
            {
                // 不在UI线程上，使用BeginInvoke异步切换到UI线程，优先级提高到Send
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var icon = new PackIcon { Kind = iconKind, Margin = new Thickness(0, 0, 8, 0) };
                    var panel = new StackPanel { Orientation = Orientation.Horizontal };
                    panel.Children.Add(icon);
                    panel.Children.Add(new TextBlock { Text = message, FontSize = 14 });
                    
                    // 使用清除队列后直接添加消息的方式实现最新消息立即显示
                    messageQueue.Clear(); // 清除现有队列
                    
                    // 使用正确的参数顺序调用Enqueue方法
                    messageQueue.Enqueue(
                        panel,              // 内容
                        null,               // 操作回调
                        null,               // 操作内容
                        TimeSpan.FromMilliseconds(durationMs) // 显示持续时间
                    );
                }), System.Windows.Threading.DispatcherPriority.Send); // 提高优先级到Send
            }
        }
    }

    // 显示状态信息（替代StatusBarText）
    private void ShowStatus(string message, PackIconKind iconKind = PackIconKind.Information, int durationMs = 2000)
    {
        // 在调试日志中记录状态信息
        LogMessage($"状态: {message}");
        
        // 使用Snackbar显示
        ShowNotification(message, iconKind, durationMs);
    }

    // 关闭应用
    private void CloseApp_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void InitializeUI()
    {
        try
    {
        // 初始化状态指示器 - 现在它们已经是PackIcon类型
        UpdateConnectionStatus(); // 调用此方法将设置正确的初始状态
        
        // 添加窗口关闭处理
        Closing += (s, e) =>
        {
            _connectionTimer?.Stop();
            if (_deviceService != null)
            {
                _deviceService.AutoRefreshEnabled = false; // 确保关闭自动刷新
            }
            _logService?.LogInfo("主窗口", "应用程序正在关闭");
        };
        
        // 初始化Snackbar消息队列设置
        if (MainSnackbar != null && MainSnackbar.MessageQueue != null)
        {
            var messageQueue = MainSnackbar.MessageQueue as MaterialDesignThemes.Wpf.SnackbarMessageQueue;
            if (messageQueue != null)
            {
                messageQueue.DiscardDuplicates = true;
                
                // 调整Snackbar显示位置，确保消息能够正确显示
                MainSnackbar.Margin = new Thickness(0, 0, 0, 24);
            }
        }
        
        // 确保日志显示区域自动滚动到底部的初始设置
        if (OperationLogTextBox != null)
        {
            // 初始运行一次滚动到底部
            Dispatcher.BeginInvoke(new Action(() => {
                ScrollToBottom();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        
        // 初始化自动刷新状态显示
        UpdateAutoRefreshStatus(_deviceService?.AutoRefreshEnabled ?? false);
        
        // 设置初始页面
        SwitchToPage("Firmware");
        
        // 初始化固件信息显示
        ClearBootFirmwareInfo();
        ClearAppFirmwareInfo();
        
        // 记录初始化完成
        _logService?.LogInfo("主窗口", "界面初始化完成");
    }
    catch (Exception ex)
    {
        LogMessage($"初始化UI时出错: {ex.Message}", LogLevel.Error);
        }
    }
    
    private async void SaveLogs()
    {
        if (_logService == null)
        {
            System.Windows.MessageBox.Show("日志服务未初始化，无法保存日志", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存日志文件",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".log",
            FileName = $"STM32Programmer_{DateTime.Now:yyyyMMdd_HHmmss}.log"
        };
        
        if (dialog.ShowDialog() == true)
        {
            // 只保存操作日志，注释掉第二个参数
            await _logService.SaveLogsToFileAsync(dialog.FileName);
            _logService.LogInfo("系统", $"日志已保存到: {dialog.FileName}");
            
            // 显示提示
            if (MainSnackbar.MessageQueue != null)
            {
                MainSnackbar.MessageQueue.Enqueue($"日志已成功保存到: {dialog.FileName}");
            }
        }
    }

    // 配置STM32CubeProgrammer
    private void ConfigProgrammer_Click(object sender, RoutedEventArgs e)
    {
        // 显示配置对话框
        if (_logService != null)
        {
            _logService.LogInfo("配置", "正在打开STM32CubeProgrammer配置页面");
        }
        System.Windows.MessageBox.Show("此功能暂未实现", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // 处理ESC键退出全屏
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 移除退出全屏功能
    }

    // 添加触摸手势支持
    private void Window_ManipulationStarted(object sender, ManipulationStartedEventArgs e)
    {
        // 记录触摸开始
        _logService?.LogDebug("触摸操作", "开始触摸交互");
    }

    private void Window_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        // 处理滑动手势
        var translation = e.DeltaManipulation.Translation;
        
        // 左右滑动切换页面
        if (Math.Abs(translation.X) > 100 && Math.Abs(translation.X) > Math.Abs(translation.Y) * 2)
        {
            e.Handled = true;
            
            // 如果已经处理了滑动事件，则不再响应
            if (_isSwipeHandled) return;
            _isSwipeHandled = true;
            
            if (translation.X > 0)
            {
                // 向右滑动 - 切换到上一页
                SwitchToPreviousPage();
            }
            else
            {
                // 向左滑动 - 切换到下一页
                SwitchToNextPage();
            }
        }
    }

    private void Window_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
    {
        // 重置滑动处理标志
        _isSwipeHandled = false;
        _logService?.LogDebug("触摸操作", "触摸交互结束");
    }

    private void Window_ManipulationBoundaryFeedback(object sender, ManipulationBoundaryFeedbackEventArgs e)
    {
        // 防止系统边界反馈
        e.Handled = true;
    }

    // 切换到下一个页面
    private void SwitchToNextPage()
    {
        string currentPage = GetCurrentPage();
        switch (currentPage)
        {
            case "Firmware": SwitchToPage("Settings"); break;
            case "Settings": SwitchToPage("LogView"); break;
            case "LogView": SwitchToPage("About"); break;
            case "About": SwitchToPage("Firmware"); break;
            default: SwitchToPage("Firmware"); break;
        }
    }

    // 切换到上一个页面
    private void SwitchToPreviousPage()
    {
        string currentPage = GetCurrentPage();
        switch (currentPage)
        {
            case "Firmware": SwitchToPage("About"); break;
            case "Settings": SwitchToPage("Firmware"); break;
            case "LogView": SwitchToPage("Settings"); break;
            case "About": SwitchToPage("LogView"); break;
            default: SwitchToPage("Firmware"); break;
        }
    }

    // 获取当前页面
    private string GetCurrentPage()
    {
        if (FirmwareContent.Visibility == Visibility.Visible) return "Firmware";
        if (SettingsContent.Visibility == Visibility.Visible) return "Settings";
        if (LogViewContent.Visibility == Visibility.Visible) return "LogView";
        if (AboutContent.Visibility == Visibility.Visible) return "About";
        return "Firmware";
    }
    
    // ==================== 导航栏收起/展开功能 ====================
    private bool _isNavCollapsed = false;
    
    private void ToggleNavigation_Click(object sender, RoutedEventArgs e)
    {
        _isNavCollapsed = !_isNavCollapsed;
        
        if (_isNavCollapsed)
        {
            // 收起导航栏
            NavigationRail.Width = 80;
            ToggleNavIcon.Kind = PackIconKind.ChevronRight;
            NavLogo.Visibility = Visibility.Collapsed;
            
            // 隐藏导航按钮文字
            FirmwareNavText.Visibility = Visibility.Collapsed;
            SettingsNavText.Visibility = Visibility.Collapsed;
            LogViewNavText.Visibility = Visibility.Collapsed;
            AboutNavText.Visibility = Visibility.Collapsed;
            
            // 调整按钮宽度和图标居中
            FirmwareNavButton.Width = 60;
            SettingsNavButton.Width = 60;
            LogViewNavButton.Width = 60;
            AboutNavButton.Width = 60;
            
            // 调整图标Margin使其居中
            FirmwareNavIcon.Margin = new Thickness(0);
            SettingsNavIcon.Margin = new Thickness(0);
            LogViewNavIcon.Margin = new Thickness(0);
            AboutNavIcon.Margin = new Thickness(0);
        }
        else
        {
            // 展开导航栏
            NavigationRail.Width = 160;
            ToggleNavIcon.Kind = PackIconKind.ChevronLeft;
            NavLogo.Visibility = Visibility.Visible;
            
            // 显示导航按钮文字
            FirmwareNavText.Visibility = Visibility.Visible;
            SettingsNavText.Visibility = Visibility.Visible;
            LogViewNavText.Visibility = Visibility.Visible;
            AboutNavText.Visibility = Visibility.Visible;
            
            // 恢复按钮宽度
            FirmwareNavButton.Width = 140;
            SettingsNavButton.Width = 140;
            LogViewNavButton.Width = 140;
            AboutNavButton.Width = 140;
            
            // 恢复图标Margin
            FirmwareNavIcon.Margin = new Thickness(8, 0, 12, 0);
            SettingsNavIcon.Margin = new Thickness(8, 0, 12, 0);
            LogViewNavIcon.Margin = new Thickness(8, 0, 12, 0);
            AboutNavIcon.Margin = new Thickness(8, 0, 12, 0);
        }
    }
    
    // 添加顶部导航按钮相关的事件处理函数
    private void FirmwareNav_Click(object sender, RoutedEventArgs e)
    {
        SwitchToPage("Firmware");
    }
    
    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        SwitchToPage("Settings");
    }
    
    private void LogViewNav_Click(object sender, RoutedEventArgs e)
    {
        SwitchToPage("LogView");
    }
    
    // SaveLogNav_Click 已移除 - 保存日志功能已集成到日志查看页面
    
    private void AboutNav_Click(object sender, RoutedEventArgs e)
    {
        SwitchToPage("About");
    }
    
    // 页面切换函数
    private void SwitchToPage(string pageName)
    {
        // 重置所有导航按钮样式
        FirmwareNavButton.Style = FindResource("NavRailButtonStyle") as Style;
        SettingsNavButton.Style = FindResource("NavRailButtonStyle") as Style;
        LogViewNavButton.Style = FindResource("NavRailButtonStyle") as Style;
        AboutNavButton.Style = FindResource("NavRailButtonStyle") as Style;
        
        // 隐藏所有内容页
        FirmwareContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Collapsed;
        LogViewContent.Visibility = Visibility.Collapsed;
        AboutContent.Visibility = Visibility.Collapsed;
        
        // 根据选择的页面名称显示对应页面并高亮对应按钮
        switch (pageName)
        {
            case "Firmware":
                FirmwareContent.Visibility = Visibility.Visible;
                FirmwareNavButton.Style = FindResource("NavRailButtonSelectedStyle") as Style;
                ShowStatus("固件烧写页面", PackIconKind.Flash);
                break;
            case "Settings":
                SettingsContent.Visibility = Visibility.Visible;
                SettingsNavButton.Style = FindResource("NavRailButtonSelectedStyle") as Style;
                ShowStatus("配置设置页面", PackIconKind.Cog);
                break;
            case "LogView":
                LogViewContent.Visibility = Visibility.Visible;
                LogViewNavButton.Style = FindResource("NavRailButtonSelectedStyle") as Style;
                ShowStatus("日志查看页面", PackIconKind.TextBoxOutline);
                break;
            case "About":
                AboutContent.Visibility = Visibility.Visible;
                AboutNavButton.Style = FindResource("NavRailButtonSelectedStyle") as Style;
                ShowStatus("关于页面", PackIconKind.Information);
                break;
        }
        
        // 记录导航事件
        _logService?.LogInfo("导航", $"切换到{GetPageChineseName(pageName)}页面");
    }
    
    // 获取页面中文名称
    private string GetPageChineseName(string pageName)
    {
        switch (pageName)
        {
            case "Firmware": return "固件烧写";
            case "Settings": return "配置设置";
            case "LogView": return "日志查看";
            case "About": return "关于";
            default: return pageName;
        }
    }

    // 更新BOOT固件信息显示
    private void UpdateBootFirmwareInfo(FirmwareFile firmware)
    {
        if (firmware == null) return;
        
        try
        {
            var fileInfo = new FileInfo(firmware.FilePath);
            if (fileInfo.Exists)
            {
                // 更新文件名
                BootFileNameText.Text = fileInfo.Name;
                
                // 更新文件大小（转换为KB、MB或GB）
                BootFileSizeText.Text = FormatFileSize(fileInfo.Length);
                
                // 更新最后修改日期
                BootFileModifiedText.Text = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                
                // 更新烧录地址（从固件文件解析的地址）
                if (!string.IsNullOrEmpty(firmware.StartAddress) && BootStartAddressTextBox != null)
                {
                    BootStartAddressTextBox.Text = firmware.StartAddress;
                    LogMessage($"BOOT固件烧录地址: {firmware.StartAddress}");
                }
                
                LogMessage($"已加载BOOT固件信息: {fileInfo.Name}");
            }
            else
            {
                LogMessage("BOOT固件文件不存在", LogLevel.Warning);
                ClearBootFirmwareInfo();
            }
        }
        catch (Exception ex)
        {
            LogMessage($"更新BOOT固件信息时出错: {ex.Message}", LogLevel.Error);
            ClearBootFirmwareInfo();
        }
    }
    
    // 更新APP固件信息显示
    private void UpdateAppFirmwareInfo(FirmwareFile firmware)
    {
        if (firmware == null) return;
        
        try
        {
            var fileInfo = new FileInfo(firmware.FilePath);
            if (fileInfo.Exists)
            {
                // 更新文件名
                AppFileNameText.Text = fileInfo.Name;
                
                // 更新文件大小（转换为KB、MB或GB）
                AppFileSizeText.Text = FormatFileSize(fileInfo.Length);
                
                // 更新最后修改日期
                AppFileModifiedText.Text = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                
                // 更新烧录地址（从固件文件解析的地址）
                if (!string.IsNullOrEmpty(firmware.StartAddress) && AppStartAddressTextBox != null)
                {
                    AppStartAddressTextBox.Text = firmware.StartAddress;
                    LogMessage($"APP固件烧录地址: {firmware.StartAddress}");
                }
                
                LogMessage($"已加载APP固件信息: {fileInfo.Name}");
            }
            else
            {
                LogMessage("APP固件文件不存在", LogLevel.Warning);
                ClearAppFirmwareInfo();
            }
        }
        catch (Exception ex)
        {
            LogMessage($"更新APP固件信息时出错: {ex.Message}", LogLevel.Error);
            ClearAppFirmwareInfo();
        }
    }
    
    // 清除BOOT固件信息显示
    private void ClearBootFirmwareInfo()
    {
        BootFileNameText.Text = "-";
        BootFileSizeText.Text = "-";
        BootFileModifiedText.Text = "-";
    }
    
    // 清除APP固件信息显示
    private void ClearAppFirmwareInfo()
    {
        AppFileNameText.Text = "-";
        AppFileSizeText.Text = "-";
        AppFileModifiedText.Text = "-";
    }
    
    // 格式化文件大小显示
    private string FormatFileSize(long byteCount)
    {
        if (byteCount < 1024)
            return $"{byteCount} B";
        else if (byteCount < 1024 * 1024)
            return $"{byteCount / 1024.0:F2} KB";
        else if (byteCount < 1024 * 1024 * 1024)
            return $"{byteCount / (1024.0 * 1024.0):F2} MB";
        else
            return $"{byteCount / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    // 在清除BOOT固件时清空显示
    private void ClearBootFirmware()
    {
        _bootFirmware = null;
        BootFilePathTextBox.Text = string.Empty;
        BootFirmwareListView.ItemsSource = null;
        ClearBootFirmwareInfo();
        UpdateConnectionStatus();
    }
    
    // 在清除APP固件时清空显示
    private void ClearAppFirmware()
    {
        _appFirmware = null;
        AppFilePathTextBox.Text = string.Empty;
        AppFirmwareListView.ItemsSource = null;
        ClearAppFirmwareInfo();
        UpdateConnectionStatus();
    }

    // 清理项目文件
    private void CleanProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = System.Windows.MessageBox.Show(
                "确定要清理项目文件吗？这将清除所有已选择的固件文件。",
                "清理确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                ShowStatus("正在清理项目文件...", PackIconKind.Broom);
                LogMessage("清理项目文件");
                
                // 清除固件选择
                ClearBootFirmware();
                ClearAppFirmware();
                
                ShowStatus("项目文件已清理", PackIconKind.CheckCircle);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"清理项目文件时出错: {ex.Message}", LogLevel.Error);
            ShowStatus("清理项目文件出错", PackIconKind.AlertCircle);
        }
    }

    // 设置控件启用/禁用状态
    private void SetControlsEnabled(bool enabled)
    {
        RefreshConnectionButton.IsEnabled = enabled;
        SelectBootButton.IsEnabled = enabled;
        SelectAppButton.IsEnabled = enabled;
        SmartScanButton.IsEnabled = enabled;
        BurnBootButton.IsEnabled = enabled && _bootFirmware != null && _bootFirmware.IsValid;
        BurnAppButton.IsEnabled = enabled && _appFirmware != null && _appFirmware.IsValid;
        BurnAllButton.IsEnabled = enabled && _bootFirmware != null && _bootFirmware.IsValid 
                                && _appFirmware != null && _appFirmware.IsValid;
        CleanProjectButton.IsEnabled = enabled;
    }

    // BOOT固件选择ListView改变事件
    private void BootFirmwareListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BootFirmwareListView.SelectedItem is FirmwareFile firmware)
        {
            _bootFirmware = firmware;
            BootFilePathTextBox.Text = firmware.FilePath;
            UpdateBootFirmwareInfo(firmware);
            UpdateConnectionStatus();
            
            LogMessage($"已选择BOOT固件: {firmware.FileName}");
        }
    }
    
    // APP固件选择ListView改变事件
    private void AppFirmwareListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppFirmwareListView.SelectedItem is FirmwareFile firmware)
        {
            _appFirmware = firmware;
            AppFilePathTextBox.Text = firmware.FilePath;
            UpdateAppFirmwareInfo(firmware);
            UpdateConnectionStatus();
            
            LogMessage($"已选择APP固件: {firmware.FileName}");
        }
    }

    #region 配置管理界面事件

    private void BrowseCubeProgrammer_Click(object sender, RoutedEventArgs e)
    {
        // 打开文件选择对话框选择STM32_Programmer_CLI.exe
        Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe",
            Title = "选择STM32_Programmer_CLI.exe文件"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            CubeProgrammerPathTextBox.Text = openFileDialog.FileName;
            // 可以在这里验证文件的有效性
        }
    }

    private void AutoDetectCubeProgrammer_Click(object sender, RoutedEventArgs e)
    {
        // 自动检测STM32CubeProgrammer的安装位置
        // 这里只是示例代码，实际需要实现自动搜索逻辑
        string[] possiblePaths = new string[]
        {
            @"C:\Program Files\STMicroelectronics\STM32Cube\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe",
            @"C:\Program Files (x86)\STMicroelectronics\STM32Cube\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe"
        };

        foreach (string path in possiblePaths)
        {
            if (System.IO.File.Exists(path))
            {
                CubeProgrammerPathTextBox.Text = path;
                CubeProgrammerStatusText.Text = "已找到STM32CubeProgrammer";
                CubeProgrammerStatusText.Foreground = new SolidColorBrush(Colors.Green);
                return;
            }
        }

        // 未找到程序
        CubeProgrammerStatusText.Text = "未检测到STM32CubeProgrammer";
        CubeProgrammerStatusText.Foreground = new SolidColorBrush(Colors.Red);
    }

    private void DownloadCubeProgrammer_Click(object sender, RoutedEventArgs e)
    {
        // 打开ST官网下载链接
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://www.st.com/en/development-tools/stm32cubeprog.html",
            UseShellExecute = true
        });
    }

    private void BrowseSearchPath_Click(object sender, RoutedEventArgs e)
    {
        // 打开文件夹选择对话框
        var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DefaultSearchPathTextBox.Text = dialog.SelectedPath;
        }
    }

    private void ConnectionMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 根据连接方式更新界面
        if (ConnectionModeComboBox?.SelectedItem != null)
        {
            var selectedItem = ConnectionModeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                var item = selectedItem.Content.ToString();
                // 可以根据选择的连接方式更新UI元素的可见性或状态
            }
        }
    }

    private void EraseType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 根据擦除类型更新界面
        if (EraseTypeComboBox?.SelectedItem != null)
        {
            var selectedItem = EraseTypeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                var item = selectedItem.Content.ToString();
                // 可以进行相应的操作
            }
        }
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 根据主题选择更新界面
        if (ThemeComboBox?.SelectedItem != null)
        {
            var selectedItem = ThemeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                var item = selectedItem.Content.ToString();
                // 可以在这里实现主题切换
            }
        }
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        // 导出设置到文件
        Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "配置文件 (*.json)|*.json",
            Title = "导出设置"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            // 这里实现设置导出逻辑
            ShowMessage("设置已成功导出到: " + saveFileDialog.FileName);
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        // 从文件导入设置
        Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "配置文件 (*.json)|*.json",
            Title = "导入设置"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            // 这里实现设置导入逻辑
            ShowMessage("设置已成功导入");
        }
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        // 重置所有设置到默认值
        var result = System.Windows.MessageBox.Show("确定要重置所有设置到默认值吗？此操作无法撤销。", "重置确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        
        if (result == MessageBoxResult.Yes)
        {
            // 重置设置的代码
            CubeProgrammerPathTextBox.Text = "";
            DefaultSearchPathTextBox.Text = "";
            BootFileExtensionsTextBox.Text = "*.bin;*.hex";
            AppFileExtensionsTextBox.Text = "*.bin;*.hex";
            RememberLastPathCheckBox.IsChecked = true;
            AutoRefreshIntervalSlider.Value = 5;
            ConnectionModeComboBox.SelectedIndex = 0;
            AutoConnectCheckBox.IsChecked = true;
            RetryConnectionCheckBox.IsChecked = true;
            EraseTypeComboBox.SelectedIndex = 0;
            VerifyAfterProgramCheckBox.IsChecked = true;
            ResetAfterProgramCheckBox.IsChecked = true;
            BackupBeforeProgramCheckBox.IsChecked = false;
            ThemeComboBox.SelectedIndex = 0;
            FullscreenStartupCheckBox.IsChecked = true;
            TouchOptimizationCheckBox.IsChecked = true;
            
            ShowMessage("所有设置已重置为默认值");
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        // 保存当前设置
        // 实际应用中需要将这些设置保存到配置文件或数据库中
        ShowMessage("设置已保存");
    }

    #endregion

    private void AutoRefreshInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AutoRefreshIntervalText != null)
        {
            int value = (int)e.NewValue;
            AutoRefreshIntervalText.Text = $"{value} 秒";
        }
    }

    private void ShowMessage(string message)
    {
        // 使用Snackbar显示消息
        MainSnackbar?.MessageQueue?.Enqueue(message);
    }

    #region 烧录设置事件处理
    
    private void ProgramMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProgramModeComboBox?.SelectedItem != null)
        {
            var selectedItem = ProgramModeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                var mode = selectedItem.Content.ToString();
                LogMessage($"烧录模式已设置为: {mode}", LogLevel.Info);
            }
        }
    }

    private void VerifyMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VerifyModeComboBox?.SelectedItem != null)
        {
            var selectedItem = VerifyModeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                var mode = selectedItem.Content.ToString();
                LogMessage($"校验模式已设置为: {mode}", LogLevel.Info);
                
                // 同步更新校验复选框
                if (mode == "不校验")
                {
                    VerifyAfterProgramCheckBox.IsChecked = false;
                }
                else
                {
                    VerifyAfterProgramCheckBox.IsChecked = true;
                }
            }
        }
    }

    private void ResetMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResetModeComboBox?.SelectedItem != null)
        {
            var selectedItem = ResetModeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                var mode = selectedItem.Content.ToString();
                LogMessage($"复位模式已设置为: {mode}", LogLevel.Info);
                
                // 同步更新复位复选框
                if (mode == "不复位")
                {
                    ResetAfterProgramCheckBox.IsChecked = false;
                }
                else
                {
                    ResetAfterProgramCheckBox.IsChecked = true;
                }
            }
        }
    }

    private void Timeout_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TimeoutValueText != null)
        {
            int value = (int)e.NewValue;
            TimeoutValueText.Text = $"{value} 秒";
        }
    }

    private void RetryCount_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RetryCountText != null)
        {
            int value = (int)e.NewValue;
            RetryCountText.Text = $"{value} 次";
        }
    }
    
    private void ChipProtection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChipProtectionComboBox?.SelectedItem != null)
        {
            var selectedItem = ChipProtectionComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                var protection = selectedItem.Content.ToString();
                LogMessage($"芯片保护设置已更改为: {protection}", LogLevel.Info);
                
                // 如果选择读写保护或仅读保护，显示警告
                if (protection == "读写保护" || protection == "仅读保护")
                {
                    ShowMessage("警告：设置保护后可能会导致无法再次烧写，请确保了解此操作的影响");
                }
            }
        }
    }
    
    private void ChipFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChipFamilyComboBox?.SelectedItem != null)
        {
            var selectedItem = ChipFamilyComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                var family = selectedItem.Content.ToString();
                LogMessage($"已选择芯片系列: {family}", LogLevel.Info);
                
                // 根据芯片系列预设特定参数
                switch (family)
                {
                    case "STM32F0系列":
                        BootStartAddressTextBox.Text = "0x08000000";
                        AppStartAddressTextBox.Text = "0x08004000";
                        break;
                    case "STM32F1系列":
                        BootStartAddressTextBox.Text = "0x08000000";
                        AppStartAddressTextBox.Text = "0x08008000";
                        break;
                    case "STM32F4系列":
                        BootStartAddressTextBox.Text = "0x08000000";
                        AppStartAddressTextBox.Text = "0x08020000";
                        break;
                    case "STM32H7系列":
                        BootStartAddressTextBox.Text = "0x08000000";
                        AppStartAddressTextBox.Text = "0x08040000";
                        DualBankModeCheckBox.IsEnabled = true;
                        break;
                    default:
                        BootStartAddressTextBox.Text = "0x08000000";
                        AppStartAddressTextBox.Text = "0x08010000";
                        break;
                }
            }
        }
    }
    
    private void CommandTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommandTemplateComboBox?.SelectedItem != null)
        {
            var selectedItem = CommandTemplateComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                var template = selectedItem.Content.ToString();
                
                switch (template)
                {
                    case "标准烧写":
                        CustomCommandArgsTextBox.Text = "--verify\r\n--go";
                        break;
                    case "带读保护的烧写":
                        CustomCommandArgsTextBox.Text = "--verify\r\n--go\r\n--readout-protect 1";
                        break;
                    case "仅擦除芯片":
                        CustomCommandArgsTextBox.Text = "--erase all";
                        break;
                    case "解锁芯片":
                        CustomCommandArgsTextBox.Text = "--readout-unprotect\r\n--option-bytes RDP=0xAA";
                        break;
                    case "选项字节配置":
                        CustomCommandArgsTextBox.Text = "--option-bytes nBOOT0=1,nSWBOOT0=1";
                        break;
                }
                
                LogMessage($"已应用命令模板: {template}", LogLevel.Info);
            }
        }
    }
    
    private void SaveAsTemplate_Click(object sender, RoutedEventArgs e)
    {
        // 弹出对话框让用户输入模板名称
        var inputDialog = new InputDialogContent
        {
            Title = "保存命令模板",
            Message = "请输入此命令模板的名称:",
            InputLabel = "模板名称",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "取消"
        };
        
        ShowInputDialog(inputDialog, result =>
        {
            if (result != null && !string.IsNullOrEmpty(result))
            {
                // 保存当前命令为模板
                SaveCommandTemplate(result, CustomCommandArgsTextBox.Text);
                ShowMessage($"命令模板 '{result}' 已保存");
            }
        });
    }
    
    private void SaveCommandTemplate(string templateName, string commandArgs)
    {
        // 实际应用中，这里应该将模板保存到配置文件或数据库中
        // 此处仅为示例，实际上没有真正保存
        LogMessage($"已保存命令模板: {templateName}", LogLevel.Info);
        
        // 如果需要，可以将新模板添加到ComboBox中
        bool templateExists = false;
        foreach (ComboBoxItem item in CommandTemplateComboBox.Items)
        {
            if (item.Content.ToString() == templateName)
            {
                templateExists = true;
                break;
            }
        }
        
        if (!templateExists)
        {
            ComboBoxItem newItem = new ComboBoxItem { Content = templateName };
            CommandTemplateComboBox.Items.Add(newItem);
        }
    }
    
    #endregion
    
    #region 对话框辅助类和方法
    
    public class InputDialogContent
    {
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string InputLabel { get; set; } = "";
        public string InputValue { get; set; } = "";
        public string PrimaryButtonText { get; set; } = "确定";
        public string SecondaryButtonText { get; set; } = "取消";
        public PackIconKind Icon { get; set; } = PackIconKind.ContentSave;
        public bool IsPassword { get; set; } = false;
    }
    
    private async void ShowInputDialog(InputDialogContent content, Action<string> callback)
    {
        // 创建输入框对话框
        var dialog = new StackPanel { Margin = new Thickness(16) };
        
        var title = new TextBlock
        {
            Text = content.Title,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 16)
        };
        dialog.Children.Add(title);
        
        if (!string.IsNullOrEmpty(content.Message))
        {
            var message = new TextBlock
            {
                Text = content.Message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };
            dialog.Children.Add(message);
        }
        
        var inputBox = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(0, 0, 0, 24),
            Text = content.InputValue
        };
        
        if (!string.IsNullOrEmpty(content.InputLabel))
        {
            inputBox.SetValue(MaterialDesignThemes.Wpf.HintAssist.HintProperty, content.InputLabel);
        }
        
        if (content.IsPassword)
        {
            var passwordBox = new System.Windows.Controls.PasswordBox
            {
                Margin = new Thickness(0, 0, 0, 24),
                Password = content.InputValue
            };
            
            if (!string.IsNullOrEmpty(content.InputLabel))
            {
                passwordBox.SetValue(MaterialDesignThemes.Wpf.HintAssist.HintProperty, content.InputLabel);
            }
            
            dialog.Children.Add(passwordBox);
        }
        else
        {
            dialog.Children.Add(inputBox);
        }
        
        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        
        var cancelButton = new Button
        {
            Content = content.SecondaryButtonText,
            Style = FindResource("MaterialDesignFlatButton") as Style,
            Margin = new Thickness(8, 0, 8, 0),
            Command = MaterialDesignThemes.Wpf.DialogHost.CloseDialogCommand,
            CommandParameter = ""
        };
        buttonsPanel.Children.Add(cancelButton);
        
        var saveButton = new Button
        {
            Content = content.PrimaryButtonText,
            Style = FindResource("MaterialDesignRaisedButton") as Style,
            Margin = new Thickness(8, 0, 0, 0)
        };
        
        // 设置确定按钮的点击事件
        saveButton.Click += (s, e) =>
        {
            string result = content.IsPassword 
                ? (dialog.Children[2] as System.Windows.Controls.PasswordBox)?.Password ?? "" 
                : (dialog.Children[2] as System.Windows.Controls.TextBox)?.Text ?? "";
            
            MaterialDesignThemes.Wpf.DialogHost.Close("RootDialog", result);
        };
        
        buttonsPanel.Children.Add(saveButton);
        dialog.Children.Add(buttonsPanel);
        
        // 显示对话框并获取结果
        var result = await MaterialDesignThemes.Wpf.DialogHost.Show(dialog, "RootDialog");
        if (callback != null)
        {
            callback(result as string ?? string.Empty);
        }
    }
    
    #endregion

    // 添加刷新串口列表的方法
    private void RefreshSerialPorts()
    {
        try
        {
            if (ComPortComboBox == null)
            {
                LogMessage("无法刷新串口列表：控件未初始化", LogLevel.Debug);
                return;
            }
            
            ComPortComboBox.Items.Clear();
            
            // 获取系统中所有可用的串口
            string[]? ports = null;
            try
            {
                ports = System.IO.Ports.SerialPort.GetPortNames();
            }
            catch (System.IO.IOException ioEx)
            {
                // 串口访问异常（可能是权限问题或驱动问题）
                LogMessage($"访问串口时出现IO异常: {ioEx.Message}", LogLevel.Debug);
                ports = new string[0]; // 返回空数组
            }
            catch (UnauthorizedAccessException uaEx)
            {
                // 权限不足
                LogMessage($"访问串口权限不足: {uaEx.Message}", LogLevel.Warning);
                ports = new string[0];
            }
            
            if (ports.Length > 0)
            {
                foreach (string port in ports)
                {
                    ComPortComboBox.Items.Add(port);
                }
                ComPortComboBox.SelectedIndex = 0;
                
                // 确保控件已初始化后再设置文本
                if (SerialStatusText != null)
                {
                    SerialStatusText.Text = "未连接";
                }
                
                if (SerialStatusIcon != null)
                {
                    SerialStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
                }
            }
            else
            {
                if (SerialStatusText != null)
                {
                    SerialStatusText.Text = "无可用串口";
                }
                
                if (SerialStatusIcon != null)
                {
                    SerialStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(255, 0, 0));
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"刷新串口列表时出错: {ex.Message}", LogLevel.Error);
        }
    }

    // 刷新串口列表按钮点击事件
    private void RefreshPortsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSerialPorts();
        ShowNotification("已刷新串口列表", PackIconKind.Refresh);
    }
    
    // 连接串口按钮点击事件
    private void ConnectSerialButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 检查是否已选择串口
            if (ComPortComboBox?.SelectedItem == null)
            {
                ShowNotification("请选择串口", PackIconKind.Alert);
                return;
            }
            
            // 检查是否已连接
            if (ConnectSerialButton?.Content?.ToString() == "断开")
            {
                // 断开连接
                DisconnectSerial();
                return;
            }
            
            // 获取串口设置
            string portName = ComPortComboBox.SelectedItem.ToString() ?? "未知端口";
            
            // 获取波特率、数据位、停止位、校验位，加入空值检查
            if (BaudRateComboBox?.SelectedItem is not ComboBoxItem baudRateItem || 
                DataBitsComboBox?.SelectedItem is not ComboBoxItem dataBitsItem ||
                StopBitsComboBox?.SelectedItem is not ComboBoxItem stopBitsItem ||
                ParityComboBox?.SelectedItem is not ComboBoxItem parityItem)
            {
                ShowNotification("请设置正确的串口参数", PackIconKind.Alert);
                return;
            }
            
            int baudRate = int.Parse(baudRateItem.Content?.ToString() ?? "9600");
            int dataBits = int.Parse(dataBitsItem.Content?.ToString() ?? "8");
            
            // 获取停止位和校验位
            string stopBitsStr = stopBitsItem.Content?.ToString() ?? "1";
            StopBits stopBits = StopBits.One;
            if (stopBitsStr == "1.5")
                stopBits = StopBits.OnePointFive;
            else if (stopBitsStr == "2")
                stopBits = StopBits.Two;
            
            string parityStr = parityItem.Content?.ToString() ?? "无";
            Parity parity = Parity.None;
            if (parityStr == "奇校验")
                parity = Parity.Odd;
            else if (parityStr == "偶校验")
                parity = Parity.Even;
            
            // 尝试连接串口
            if (_serialBurnService != null)
            {
                bool connected = _serialBurnService.Connect(portName, baudRate, dataBits, stopBits, parity);
                if (connected)
                {
                    // 更新UI显示
                    if (ConnectSerialButton != null)
                        ConnectSerialButton.Content = "断开";
                        
                    if (SerialStatusText != null)
                        SerialStatusText.Text = $"已连接 ({portName})";
                        
                    if (SerialStatusIcon != null)
                        SerialStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0, 150, 0));
                    
                    // 启用串口发送按钮
                    if (SerialSendButton != null)
                        SerialSendButton.IsEnabled = true;
                    
                    ShowNotification($"已连接到串口 {portName}", PackIconKind.Console);
                    
                    LogMessage($"已连接串口 {portName}，波特率: {baudRate}，数据位: {dataBits}，停止位: {stopBitsStr}，校验位: {parityStr}");
                }
                else
                {
                    ShowNotification($"无法连接到串口 {portName}", PackIconKind.Alert);
                }
            }
            else
            {
                LogMessage("串口烧录服务未初始化", LogLevel.Error);
                ShowNotification("串口烧录服务未初始化", PackIconKind.Alert);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"连接串口时出错: {ex.Message}", LogLevel.Error);
            ShowNotification($"连接出错: {ex.Message}", PackIconKind.Alert);
        }
    }
    
    // 断开串口连接
    private void DisconnectSerial()
    {
        try
        {
            // 这里是示例代码，实际项目需要关闭串口连接
            if (ConnectSerialButton != null)
                ConnectSerialButton.Content = "连接";
                
            if (SerialStatusText != null)
                SerialStatusText.Text = "未连接";
                
            if (SerialStatusIcon != null)
                SerialStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            
            // 禁用串口发送按钮
            if (SerialSendButton != null)
                SerialSendButton.IsEnabled = false;
            
            ShowNotification("已断开串口连接", PackIconKind.Console);
            LogMessage("已断开串口连接");
        }
        catch (Exception ex)
        {
            LogMessage($"断开串口时出错: {ex.Message}", LogLevel.Error);
        }
    }

    #region 串口日志功能
    
    // 清除串口日志
    private void ClearSerialLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SerialLogTextBlock != null)
            {
                SerialLogTextBlock.Text = string.Empty;
            }
            LogMessage("已清除串口日志");
        }
        catch (Exception ex)
        {
            LogMessage($"清除串口日志时出错: {ex.Message}", LogLevel.Error);
        }
    }
    
    // 保存串口日志
    private void SaveSerialLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SerialLogTextBlock == null || string.IsNullOrEmpty(SerialLogTextBlock.Text))
            {
                ShowNotification("没有可保存的日志", PackIconKind.Alert);
                return;
            }
            
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存串口日志",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".txt",
                FileName = $"SerialLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };
            
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, SerialLogTextBlock.Text);
                ShowNotification($"日志已保存到 {Path.GetFileName(dialog.FileName)}", PackIconKind.CheckCircle);
                LogMessage($"串口日志已保存到: {dialog.FileName}");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"保存串口日志时出错: {ex.Message}", LogLevel.Error);
            ShowNotification($"保存失败: {ex.Message}", PackIconKind.Alert);
        }
    }
    
    // 串口发送文本框按键事件
    private void SerialSendTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SerialSendButton_Click(sender, e);
        }
    }
    
    // 发送串口数据
    private void SerialSendButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_serialBurnService == null || !_serialBurnService.IsConnected)
            {
                ShowNotification("请先连接串口", PackIconKind.Alert);
                return;
            }
            
            if (SerialSendTextBox == null || string.IsNullOrEmpty(SerialSendTextBox.Text))
            {
                ShowNotification("请输入要发送的数据", PackIconKind.Alert);
                return;
            }
            
            string dataToSend = SerialSendTextBox.Text;
            
            // 发送数据
            _serialBurnService.SendData(dataToSend);
            
            // 添加到日志
            AppendSerialLog($"[TX] {dataToSend}");
            
            // 清空输入框
            SerialSendTextBox.Text = string.Empty;
            
            LogMessage($"已发送串口数据: {dataToSend}");
        }
        catch (Exception ex)
        {
            LogMessage($"发送串口数据时出错: {ex.Message}", LogLevel.Error);
            ShowNotification($"发送失败: {ex.Message}", PackIconKind.Alert);
        }
    }
    
    // 添加串口日志
    private void AppendSerialLog(string message)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (SerialLogTextBlock != null)
                {
                    string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    SerialLogTextBlock.Text += $"[{timestamp}] {message}\n";
                    
                    // 自动滚动到底部
                    if (SerialLogScrollViewer != null)
                    {
                        SerialLogScrollViewer.ScrollToEnd();
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"添加串口日志时出错: {ex.Message}");
        }
    }
    
    #endregion
    
    #region 触摸支持
    
    private Button? _touchedButton;
    
    /// <summary>
    /// 启用触摸支持 - 解决触摸屏按钮点击无响应问题
    /// </summary>
    private void EnableTouchSupport()
    {
        // 禁用WPF默认的触摸行为
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);
        
        // 使用PreviewTouch事件，在路由事件隧道阶段处理
        this.PreviewTouchDown += MainWindow_PreviewTouchDown;
        this.PreviewTouchUp += MainWindow_PreviewTouchUp;
    }
    
    /// <summary>
    /// 触摸按下事件处理
    /// </summary>
    private void MainWindow_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        // 获取触摸点下的元素
        var touchPoint = e.GetTouchPoint(this);
        var element = InputHitTest(touchPoint.Position) as DependencyObject;
        
        // 向上查找Button或其他可点击控件
        _touchedButton = FindParent<Button>(element);
        
        if (_touchedButton != null && _touchedButton.IsEnabled)
        {
            // 捕获触摸设备
            e.TouchDevice.Capture(_touchedButton);
        }
    }
    
    /// <summary>
    /// 触摸抬起事件处理 - 触发按钮点击
    /// </summary>
    private void MainWindow_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        try
        {
            if (_touchedButton != null && _touchedButton.IsEnabled)
            {
                // 检查触摸点是否仍在按钮范围内
                var touchPoint = e.GetTouchPoint(_touchedButton);
                var bounds = new Rect(0, 0, _touchedButton.ActualWidth, _touchedButton.ActualHeight);
                
                if (bounds.Contains(touchPoint.Position))
                {
                    // 使用Dispatcher确保在UI线程上执行
                    var buttonToClick = _touchedButton;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            // 方法1: 使用AutomationPeer触发点击
                            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(buttonToClick);
                            var invokeProvider = peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) 
                                as System.Windows.Automation.Provider.IInvokeProvider;
                            invokeProvider?.Invoke();
                        }
                        catch
                        {
                            // 方法2: 备用方案 - 直接触发RoutedEvent
                            buttonToClick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                    }), DispatcherPriority.Input);
                }
            }
        }
        finally
        {
            // 释放捕获和清理
            e.TouchDevice.Capture(null);
            _touchedButton = null;
            e.Handled = true;
        }
    }
    
    /// <summary>
    /// 向上查找指定类型的父元素
    /// </summary>
    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent)
                return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
    
    #endregion
}

// 扩展方法类
public static class ListExtensions
{
    // 为List<T>添加FindIndex方法
    public static int FindIndex<T>(this List<T> list, Predicate<T> match)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (match(list[i]))
                return i;
        }
        return -1;
    }
}