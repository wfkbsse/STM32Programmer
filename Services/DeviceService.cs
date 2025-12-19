using STM32Programmer.Models;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace STM32Programmer.Services
{
    public class DeviceService
    {
        private string _stLinkUtilityPath = @"C:\Program Files\STMicroelectronics\STM32Cube\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe";
        
        // 自动刷新相关变量
        private System.Threading.Timer? _autoRefreshTimer;
        private bool _isRefreshEnabled = true; // 默认启用自动刷新
        private bool _isProgramming = false;
        private readonly object _lockObject = new object();
        private readonly object _timerLock = new object(); // 添加定时器专用锁对象
        private int _autoRefreshInterval = 3000; // 默认3秒刷新一次
        private CancellationTokenSource? _refreshCancellationSource;
        private long _isRefreshingFlag = 0;
        
        // STM32_Programmer_CLI.exe工具路径
        public string STLinkUtilityPath 
        {
            get => _stLinkUtilityPath;
            set 
            {
                if (File.Exists(value))
                {
                    _stLinkUtilityPath = value;
                    LogMessage?.Invoke(this, $"已更新STM32 Programmer CLI工具路径: {_stLinkUtilityPath}");
                }
                else
                {
                    LogMessage?.Invoke(this, $"错误: 无效的STM32 Programmer CLI工具路径: {value}");
                }
            }
        }

        public event EventHandler<string>? LogMessage;
        
        // 设备连接状态变化事件
        public event EventHandler<STLinkDevice>? DeviceStatusChanged;
        
        // 添加控制是否显示连接日志的标志
        public bool ShowConnectionLogs { get; set; } = false;
        
        // 控制是否启用自动刷新
        public bool AutoRefreshEnabled
        {
            get => _isRefreshEnabled;
            set
            {
                lock (_timerLock) // 使用专用锁
                {
                    if (_isRefreshEnabled != value)
                    {
                        _isRefreshEnabled = value;
                        if (value)
                        {
                            StartAutoRefresh();
                        }
                        else
                        {
                            StopAutoRefresh();
                        }
                    }
                }
            }
        }
        
        // 切换自动刷新状态
        public void ToggleAutoRefresh()
        {
            AutoRefreshEnabled = !AutoRefreshEnabled;
        }
        
        // 自动刷新间隔(毫秒)
        public int AutoRefreshInterval
        {
            get => _autoRefreshInterval;
            set
            {
                if (value >= 1000) // 最小1秒
                {
                    _autoRefreshInterval = value;
                    // 如果自动刷新已启用，重启定时器以应用新间隔
                    if (_isRefreshEnabled)
                    {
                        RestartAutoRefresh();
                    }
                }
            }
        }

        public DeviceService()
        {
            try
            {
                // 自动查找STM32 Programmer CLI工具
                AutoFindSTLinkTool();
                CheckSTLinkToolPath();
                
                // 默认启用自动刷新
                if (_isRefreshEnabled)
                {
                    StartAutoRefresh();
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"初始化设备服务时出错: {ex.Message}");
            }
        }
        
        // 启动自动刷新
        private void StartAutoRefresh()
        {
            try
            {
                lock (_timerLock) // 使用专用锁保护定时器操作
                {
                    if (_autoRefreshTimer == null)
                    {
                        _refreshCancellationSource?.Cancel();
                        _refreshCancellationSource = new CancellationTokenSource();
                        
                        _autoRefreshTimer = new System.Threading.Timer(
                            _ => ScheduleRefresh(_refreshCancellationSource.Token), 
                            null, 
                            _autoRefreshInterval, 
                            _autoRefreshInterval);
                        
                        ConnectionLog("后台自动刷新已启动");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"启动自动刷新时出错: {ex.Message}");
            }
        }
        
        // 停止自动刷新
        private void StopAutoRefresh()
        {
            try
            {
                lock (_timerLock) // 使用专用锁保护定时器操作
                {
                    if (_autoRefreshTimer != null)
                    {
                        _refreshCancellationSource?.Cancel();
                        _autoRefreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        _autoRefreshTimer.Dispose();
                        _autoRefreshTimer = null;
                        ConnectionLog("后台自动刷新已停止");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"停止自动刷新时出错: {ex.Message}");
            }
        }
        
        // 重启自动刷新
        private void RestartAutoRefresh()
        {
            lock (_timerLock) // 使用专用锁保护完整的重启流程
            {
                StopAutoRefresh();
                if (_isRefreshEnabled)
                {
                    StartAutoRefresh();
                }
            }
        }
        
        // 暂停自动刷新
        private void PauseAutoRefresh()
        {
            try
            {
                lock (_timerLock) // 使用专用锁保护定时器操作
                {
                    if (_autoRefreshTimer != null)
                    {
                        _autoRefreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        ConnectionLog("后台自动刷新已暂停");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"暂停自动刷新时出错: {ex.Message}");
            }
        }
        
        // 恢复自动刷新
        private void ResumeAutoRefresh(int delayMs = 0)
        {
            try
            {
                lock (_timerLock) // 使用专用锁保护定时器操作
                {
                    if (_autoRefreshTimer != null && _isRefreshEnabled)
                    {
                        _autoRefreshTimer.Change(delayMs, _autoRefreshInterval);
                        ConnectionLog($"后台自动刷新将在{delayMs}ms后恢复");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"恢复自动刷新时出错: {ex.Message}");
            }
        }
        
        // 调度一个刷新任务
        private async void ScheduleRefresh(CancellationToken token)
        {
            // 避免使用Monitor.TryEnter和Exit引起的线程同步问题
            if (_isProgramming || token.IsCancellationRequested)
            {
                ConnectionLog("自动刷新被暂停：正在进行烧录操作或已被取消");
                return;
            }

            try
            {
                // 使用原子操作检测是否已在刷新，而不是使用锁
                bool isAlreadyRefreshing = Interlocked.CompareExchange(ref _isRefreshingFlag, 1, 0) == 1;
                if (isAlreadyRefreshing)
                {
                    ConnectionLog("上一次刷新任务尚未完成，跳过本次刷新");
                    return;
                }

                try
                {
                    ConnectionLog("执行后台自动刷新...");
                    var device = await CheckConnectionAsync();
                    
                    // 再次检查是否被取消
                    if (token.IsCancellationRequested) return;
                    
                    // 触发状态变化事件以通知UI
                    try
                    {
                        if (DeviceStatusChanged != null)
                        {
                            ConnectionLog("触发设备状态变更事件...");
                            
                            // 使用SynchronizationContext确保事件在UI线程上触发
                            if (SynchronizationContext.Current != null)
                            {
                                SynchronizationContext.Current.Post(_ => DeviceStatusChanged?.Invoke(this, device), null);
                            }
                            else
                            {
                                DeviceStatusChanged?.Invoke(this, device);
                            }
                            
                            ConnectionLog($"设备状态更新完成: {device.Status}, 芯片连接: {device.IsChipConnected}");
                        }
                    }
                    catch (Exception eventEx)
                    {
                        // 即使事件处理失败也不中断自动刷新
                        ConnectionLog($"触发设备状态变更事件时出错: {eventEx.Message}");
                    }
                }
                finally
                {
                    // 完成刷新，重置标志
                    Interlocked.Exchange(ref _isRefreshingFlag, 0);
                    ConnectionLog("刷新任务完成，重置刷新标志");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"后台刷新调度时出错: {ex.Message}");
                // 确保重置标志
                Interlocked.Exchange(ref _isRefreshingFlag, 0);
            }
        }
        
        // 自动查找STM32 Programmer CLI工具
        public void AutoFindSTLinkTool()
        {
            try
            {
                // 可能的安装路径
                string[] possiblePaths = new[]
                {
                    @"C:\Program Files\STMicroelectronics\STM32Cube\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe",
                    @"C:\Program Files (x86)\STMicroelectronics\STM32Cube\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"STMicroelectronics\STM32Cube\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"STMicroelectronics\STM32Cube\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe")
                };
                
                // 检查每个可能的路径
                foreach (string path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        _stLinkUtilityPath = path;
                        LogMessage?.Invoke(this, $"自动找到STM32 Programmer CLI工具: {_stLinkUtilityPath}");
                        return;
                    }
                }
                
                // 尝试在系统PATH中查找
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = "STM32_Programmer_CLI.exe",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();
                        
                        if (!string.IsNullOrEmpty(output))
                        {
                            string[] lines = output.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                            if (lines.Length > 0 && File.Exists(lines[0]))
                            {
                                _stLinkUtilityPath = lines[0];
                                LogMessage?.Invoke(this, $"在系统PATH中找到STM32 Programmer CLI工具: {_stLinkUtilityPath}");
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, $"[LogLevel.Debug] 在PATH中查找STM32 Programmer CLI工具时出错: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"自动查找STM32 Programmer CLI工具时出错: {ex.Message}");
            }
        }

        private void CheckSTLinkToolPath()
        {
            if (!File.Exists(_stLinkUtilityPath))
            {
                LogMessage?.Invoke(this, $"[LogLevel.Warning] 警告: STM32 Programmer CLI工具未找到: {_stLinkUtilityPath}");
                LogMessage?.Invoke(this, "请确保已安装STM32CubeProgrammer并且路径正确");
                LogMessage?.Invoke(this, $"[LogLevel.Info] 您可以在以下位置下载STM32CubeProgrammer: https://www.st.com/en/development-tools/stm32cubeprog.html");
            }
            else
            {
                LogMessage?.Invoke(this, $"已确认STM32 Programmer CLI工具: {_stLinkUtilityPath}");
            }
        }

        public async Task<STLinkDevice> CheckConnectionAsync()
        {
            var device = new STLinkDevice();
            // 默认设置为未连接状态
            device.Status = ConnectionStatus.Disconnected;
            
            try
            {
                ConnectionLog("开始执行连接检查...");
                
                await Task.Run(() =>
                {
                    // 所有连接检查日志移至Debug级别，不在普通日志中显示
                    ConnectionLog("正在检查ST-LINK连接...");
                    
                    // 检查ST-LINK工具是否存在
                    if (!File.Exists(STLinkUtilityPath))
                    {
                        device.Status = ConnectionStatus.Error;
                        device.ErrorMessage = $"STM32 Programmer CLI工具未找到: {STLinkUtilityPath}";
                        LogMessage?.Invoke(this, device.ErrorMessage);
                        LogMessage?.Invoke(this, "提示: 您可以在以下位置下载STM32CubeProgrammer: https://www.st.com/en/development-tools/stm32cubeprog.html");
                        return;
                    }
                    
                    // 首先尝试使用最直接的方式检测ST-LINK是否存在
                    bool stlinkExists = false;
                    
                    // 使用简单命令检查ST-LINK是否物理连接
                    stlinkExists = CheckStLinkPhysicalConnection();
                    
                    if (!stlinkExists)
                    {
                        // ST-LINK设备不存在，无需尝试连接
                        LogMessage?.Invoke(this, "[LogLevel.Warning] 未检测到ST-LINK设备连接");
                        device.Status = ConnectionStatus.Disconnected;
                        ConnectionLog("未检测到物理设备，返回未连接状态");
                        return;
                    }
                    
                    // 如果ST-LINK物理存在，尝试多种连接方法
                    if (!TryConnect(device, "c port=SWD freq=4000") &&    // 尝试方法1 - SWD协议
                        !TryConnect(device, "c port=SDW freq=4000") &&    // 尝试方法2 - SDW协议
                        !TryConnect(device, "-c port=SWD freq=4000") &&   // 尝试方法3 - 带短横线
                        !TryConnect(device, "--connect port=SWD freq=4000") &&      // 尝试方法4 - 长命令格式
                        !TryConnect(device, "-l"))                        // 尝试方法5 - 只获取列表
                    {
                        // 所有连接方法都失败
                        device.Status = ConnectionStatus.Disconnected;
                        // 连接失败信息应该保留，因为用户需要知道
                        LogMessage?.Invoke(this, "[LogLevel.Error] 无法连接到ST-LINK设备，请检查硬件连接");
                        ConnectionLog("所有连接方法都失败，返回未连接状态");
                    }
                    else
                    {
                        ConnectionLog($"连接成功，设备状态: {device.Status}, 芯片连接: {device.IsChipConnected}");
                    }
                    // 连接成功信息完全移除，不再显示
                });
                
                ConnectionLog($"连接检查完成，返回状态: {device.Status}, 芯片连接: {device.IsChipConnected}");
                return device;
            }
            catch (Exception ex)
            {
                device.Status = ConnectionStatus.Error;
                device.ErrorMessage = ex.Message;
                LogMessage?.Invoke(this, $"[LogLevel.Error] 检查连接时出错: {ex.Message}");
                ConnectionLog($"连接检查异常: {ex.Message}");
                return device;
            }
        }

        // 新增方法：直接检查ST-LINK物理连接
        private bool CheckStLinkPhysicalConnection()
        {
            ConnectionLog("检查ST-LINK物理连接状态...");
            
            try
            {
                // 使用最简单的命令仅检测设备是否存在
                var startInfo = new ProcessStartInfo
                {
                    FileName = _stLinkUtilityPath,
                    Arguments = "-l",  // 仅列出已连接的ST-LINK
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    ConnectionLog("无法启动STM32 Programmer CLI工具");
                    return false;
                }

                // 短超时，仅检测设备存在情况
                if (!process.WaitForExit(3000))
                {
                    ConnectionLog("检测命令执行超时，正在终止进程");
                    try { process.Kill(); } catch { }
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                
                // 检查错误信息
                if (!string.IsNullOrEmpty(error))
                {
                    if (error.Contains("No ST-LINK detected") || 
                        error.Contains("Unable to connect") || 
                        error.Contains("No STM32 target found") ||
                        error.Contains("Connection Failed"))
                    {
                        ConnectionLog("ST-LINK设备未物理连接");
                        return false;
                    }
                }
                
                // 检查输出信息
                if (output.Contains("No ST-LINK detected") || 
                    output.Contains("未检测到ST-LINK") || 
                    output.Contains("No connected ST-LINK"))
                {
                    ConnectionLog("ST-LINK设备未物理连接");
                    return false;
                }
                
                // 检查是否确实有ST-LINK信息
                bool hasStLink = output.Contains("ST-LINK") && 
                                (output.Contains("Serial") || output.Contains("SN") || 
                                 output.Contains("Version") || output.Contains("V"));
                                    
                if (!hasStLink)
                {
                    ConnectionLog("输出中未发现ST-LINK设备信息");
                    return false;
                }
                
                ConnectionLog("检测到ST-LINK物理连接");
                return true;
            }
            catch (Exception ex)
            {
                ConnectionLog($"检查ST-LINK物理连接时出错: {ex.Message}");
                return false;
            }
        }

        // 辅助方法：尝试使用指定命令连接设备 - 修改其逻辑
        private bool TryConnect(STLinkDevice device, string connectCommand)
        {
            ConnectionLog($"尝试连接命令: {_stLinkUtilityPath} {connectCommand}");
            
            try
            {
                // 构建连接命令
                var startInfo = new ProcessStartInfo
                {
                    FileName = _stLinkUtilityPath,
                    Arguments = connectCommand,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    ConnectionLog("无法启动STM32 Programmer CLI工具");
                    device.Status = ConnectionStatus.Disconnected;
                    return false;
                }

                // 添加超时保护
                if (!process.WaitForExit(5000)) // 等待最多5秒
                {
                    ConnectionLog("命令执行超时，正在终止进程");
                    try { process.Kill(); } catch { }
                    device.Status = ConnectionStatus.Disconnected;
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                var exitCode = process.ExitCode;
                
                ConnectionLog($"命令退出代码: {exitCode}");
                
                // 检查错误信息，更严格地处理错误
                if (!string.IsNullOrEmpty(error))
                {
                    ConnectionLog($"CLI工具返回错误 ({error.Length} 字符)");
                    
                    // 对于明确表示未连接的错误，直接返回未连接状态
                    if (error.Contains("No ST-LINK detected") || 
                        error.Contains("Unable to connect") || 
                        error.Contains("Failed to connect") ||
                        error.Contains("Connection Failed") ||
                        error.Contains("No STM32 target found"))
                    {
                        ConnectionLog("ST-LINK设备未连接或无法访问");
                        device.Status = ConnectionStatus.Disconnected;
                        return false;
                    }
                    
                    // 如果是"too busy"错误，可能需要等待设备释放
                    if (error.Contains("too busy"))
                    {
                        ConnectionLog("设备似乎正忙，等待500ms后重试...");
                        Thread.Sleep(500);
                        device.Status = ConnectionStatus.Disconnected;
                        return false; // 允许尝试其他连接方法
                    }
                    
                    // 其他任何错误都视为连接问题
                    if (error.Contains("Error") || error.Contains("错误") || exitCode != 0)
                    {
                        device.Status = ConnectionStatus.Disconnected;
                        return false;
                    }
                }

                // 检查输出，更严格地要求连接信息
                if (string.IsNullOrEmpty(output))
                {
                    ConnectionLog("命令未返回任何输出，视为未连接");
                    device.Status = ConnectionStatus.Disconnected;
                    return false;
                }
                
                // 检查是否明确指出未连接的信息
                if (output.Contains("No ST-LINK detected") || 
                    output.Contains("未检测到ST-LINK") ||
                    output.Contains("Unable to connect") || 
                    output.Contains("Failed to connect") ||
                    output.Contains("Connection Failed") ||
                    output.Contains("No STM32 target found"))
                {
                    ConnectionLog("输出中明确指出ST-LINK未连接");
                    device.Status = ConnectionStatus.Disconnected;
                    return false;
                }
                
                // 检查是否有确定的ST-LINK信息
                bool hasStLink = output.Contains("ST-LINK") && 
                                (output.Contains("Serial") || output.Contains("SN") || 
                                 output.Contains("Version") || output.Contains("V"));
                
                if (!hasStLink && !connectCommand.Contains("-l"))
                {
                    ConnectionLog("输出中未检测到有效的ST-LINK相关信息");
                    device.Status = ConnectionStatus.Disconnected;
                    return false;
                }

                // 获取设备信息，同时检查连接状态
                bool deviceInfoSuccess = GetDeviceInfo(device, output);
                
                // 如果获取设备信息失败但有ST-LINK连接迹象，尝试获取更多信息
                if (!deviceInfoSuccess && hasStLink)
                {
                    // 连接成功但未获取到完整设备信息，尝试获取设备信息
                    ConnectionLog("ST-LINK连接成功，正在获取更多设备信息...");
                    device.Status = ConnectionStatus.Connected;
                    
                    // 尝试两种获取设备信息的命令
                    if (!TryGetDeviceInfo(device, "--get DeviceInfo") && 
                        !TryGetDeviceInfo(device, "-i"))
                    {
                        // 无法获取设备信息，但ST-LINK已连接
                        ConnectionLog("无法获取芯片信息，但ST-LINK已连接");
                        // 设置默认值以显示连接状态
                        if (string.IsNullOrEmpty(device.SerialNumber))
                            device.SerialNumber = "未知";
                        device.IsChipConnected = false;
                    }
                    
                    return true; // 连接成功
                }
                
                return deviceInfoSuccess;
            }
            catch (Exception ex)
            {
                ConnectionLog($"执行连接命令时出错: {ex.Message}");
                device.Status = ConnectionStatus.Disconnected;
                return false;
            }
        }

        // 从输出中提取设备信息 - 修改其逻辑，确保更严格的连接检测
        private bool GetDeviceInfo(STLinkDevice device, string output)
        {
            try
            {
                // 默认状态为未连接
                device.Status = ConnectionStatus.Disconnected;
                
                // 检查是否有明确的错误或未连接信息
                if (output.Contains("No ST-LINK detected") || 
                    output.Contains("Unable to connect") || 
                    output.Contains("Failed to connect") ||
                    output.Contains("Connection Failed") ||
                    output.Contains("No STM32 target found"))
                {
                    ConnectionLog("输出中明确指出ST-LINK未连接");
                    return false;
                }
                
                // 查找明确的ST-LINK信息
                bool hasStLinkInfo = output.Contains("ST-LINK") && 
                                   (output.Contains("Serial") || output.Contains("SN") || 
                                    output.Contains("Version") || output.Contains("V"));
                
                if (!hasStLinkInfo)
                {
                    // 输出中没有明确的ST-LINK信息，保持未连接状态
                    ConnectionLog("未检测到明确的ST-LINK设备信息");
                    return false;
                }
                
                // 找到ST-LINK信息，设置为已连接
                device.Status = ConnectionStatus.Connected;
                ConnectionLog("检测到ST-LINK连接");
                
                // 解析序列号
                var snMatch = Regex.Match(output, @"SN\s*:\s*(\w+)");
                if (snMatch.Success)
                {
                    device.SerialNumber = snMatch.Groups[1].Value;
                    ConnectionLog($"ST-LINK序列号: {device.SerialNumber}");
                }

                // 解析固件版本
                var fwMatch = Regex.Match(output, @"Firmware version\s*:\s*([\d\.]+)");
                if (fwMatch.Success)
                {
                    device.FirmwareVersion = fwMatch.Groups[1].Value;
                    ConnectionLog($"ST-LINK固件版本: {device.FirmwareVersion}");
                }
                
                // 检查芯片连接状态
                bool hasDeviceId = output.Contains("Device ID") || output.Contains("Device id") || 
                                   output.Contains("Device type") || output.Contains("Device name");
                bool connectedMessage = output.Contains("Connected") || output.Contains("connected") || 
                                       output.Contains("Connection");
                
                ConnectionLog($"芯片连接检查: 有设备标识={hasDeviceId}, 有连接信息={connectedMessage}");
                
                // 更新芯片连接状态
                device.IsChipConnected = hasDeviceId && device.Status == ConnectionStatus.Connected;
                
                if (device.IsChipConnected)
                {
                    // 尝试提取芯片ID
                    ExtractChipId(device, output);
                    
                    // 尝试提取芯片类型
                    ExtractChipType(device, output);
                    
                    ConnectionLog($"芯片连接成功: ID={device.ChipID}, 类型={device.ChipType}");
                }
                
                return device.Status == ConnectionStatus.Connected;
            }
            catch (Exception ex)
            {
                ConnectionLog($"解析设备信息时出错: {ex.Message}");
                device.Status = ConnectionStatus.Disconnected;
                return false;
            }
        }

        // 尝试获取设备信息
        private bool TryGetDeviceInfo(STLinkDevice device, string infoCommand)
        {
            ConnectionLog($"尝试获取设备信息: {_stLinkUtilityPath} {infoCommand}");
            
            try
            {
                // 构建命令
                var startInfo = new ProcessStartInfo
                {
                    FileName = _stLinkUtilityPath,
                    Arguments = infoCommand,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // 短暂延迟确保设备准备好
                Thread.Sleep(200);
                
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    ConnectionLog("无法启动获取设备信息命令");
                    return false;
                }

                // 添加超时保护
                if (!process.WaitForExit(3000)) // 等待最多3秒
                {
                    ConnectionLog("获取设备信息命令超时，正在终止进程");
                    try { process.Kill(); } catch { }
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                
                // 不显示详细输出内容，仅记录长度信息
                ConnectionLog($"收到设备信息响应 ({output.Length} 字符)");
                
                if (!string.IsNullOrEmpty(error) && error.Contains("Error"))
                {
                    ConnectionLog("获取设备信息时出错");
                    return false;
                }

                // 检查是否包含设备信息
                bool hasDeviceInfo = output.Contains("Device ID") || output.Contains("Device id") ||
                                    output.Contains("Device type") || output.Contains("Device name");
                
                if (!hasDeviceInfo)
                {
                    ConnectionLog("未在输出中找到设备信息");
                    return false;
                }
                
                // 提取芯片ID
                bool hasId = ExtractChipId(device, output);
                
                // 提取芯片类型
                bool hasType = ExtractChipType(device, output);
                
                // 只要有ID或类型，就认为芯片已连接
                device.IsChipConnected = hasId || hasType;
                
                // 如果ID或类型为空，设置默认值
                if (string.IsNullOrEmpty(device.ChipID)) device.ChipID = "未知ID";
                if (string.IsNullOrEmpty(device.ChipType)) device.ChipType = "STM32";
                
                ConnectionLog($"设备信息获取结果: 芯片连接={device.IsChipConnected}, ID={device.ChipID}, 类型={device.ChipType}");
                
                return device.IsChipConnected;
            }
            catch (Exception ex)
            {
                ConnectionLog($"获取设备信息时出错: {ex.Message}");
                return false;
            }
        }

        // 从输出中提取芯片ID
        private bool ExtractChipId(STLinkDevice device, string output)
        {
            // 尝试多种可能的ID模式
            var idPatterns = new[] {
                @"Device [Ii][Dd]\s*:\s*(0x\w+)",
                @"ID\s*:\s*(0x\w+)",
                @"芯片ID\s*:\s*(0x\w+)",
                @"Device\s*:\s*(0x\w+)"
            };
            
            foreach (var pattern in idPatterns)
            {
                var idMatch = Regex.Match(output, pattern);
                if (idMatch.Success)
                {
                    device.ChipID = idMatch.Groups[1].Value;
                    ConnectionLog($"提取到芯片ID: {device.ChipID} (使用模式: {pattern})");
                    return true;
                }
            }
            
            return false;
        }

        // 从输出中提取芯片类型
        private bool ExtractChipType(STLinkDevice device, string output)
        {
            // 尝试多种可能的类型模式
            var typePatterns = new[] {
                @"Device (?:name|type)\s*:\s*(\w+)",
                @"(?:Type|Device)\s*:\s*(\w+)",
                @"Name\s*:\s*(\w+)",
                @"芯片型号\s*:\s*(\w+)",
                @"ChipID\s*:\s*\w+ \((\w+)\)"
            };
            
            foreach (var pattern in typePatterns)
            {
                var typeMatch = Regex.Match(output, pattern);
                if (typeMatch.Success)
                {
                    device.ChipType = typeMatch.Groups[1].Value;
                    ConnectionLog($"提取到芯片类型: {device.ChipType} (使用模式: {pattern})");
                    return true;
                }
            }
            
            return false;
        }

        public async Task<bool> ProgramFirmwareAsync(FirmwareFile firmware, IProgress<int> progress)
        {
            // 记录烧录状态并暂停刷新
            _isProgramming = true;
            PauseAutoRefresh();
            
            try
            {
                return await Task.Run(() =>
                {
                    LogMessage?.Invoke(this, $"开始烧写固件: {firmware.FileName}");
                    progress?.Report(10);

                    // 检查STM32 Programmer CLI工具是否存在
                    if (!File.Exists(_stLinkUtilityPath))
                    {
                        LogMessage?.Invoke(this, $"错误: STM32 Programmer CLI工具未找到: {_stLinkUtilityPath}");
                        LogMessage?.Invoke(this, "烧写失败，请确保已安装STM32CubeProgrammer并且路径正确");
                        return false;
                    }

                    // 使用固件文件中配置的地址
                    string address = firmware.StartAddress;
                    LogMessage?.Invoke(this, $"[LogLevel.Info] 烧录地址: {address}");
                    
                    // BOOT固件需要擦除所有分区，APP固件只需要擦除APP区域
                    bool needErase = firmware.Type == FirmwareType.Boot;
                    
                    if (needErase)
                    {
                        // BOOT烧录，擦除全部
                        LogMessage?.Invoke(this, "[LogLevel.Warning] BOOT固件烧写将擦除所有分区");
                    }
                    else
                    {
                        // APP烧录，仅擦除APP区域
                        LogMessage?.Invoke(this, "[LogLevel.Info] APP固件烧写将只擦除必要区域");
                    }
                    
                    bool success = false;
                    
                    // APP固件需要特殊处理
                    if (firmware.Type == FirmwareType.App)
                    {
                        // 尝试特定的APP烧录命令组合
                        if (TryProgramFirmware(firmware, address, "-c port=SWD freq=4000 -w", needErase, progress) ||
                            TryProgramFirmware(firmware, address, "-c port=SWD freq=1000 -w", needErase, progress) ||
                            TryProgramFirmware(firmware, address, "--connect port=SWD freq=4000 --write", needErase, progress))
                        {
                            success = true;
                        }
                    }
                    else
                    {
                        // BOOT固件使用标准命令组合
                        if (TryProgramFirmware(firmware, address, "-c port=SWD freq=4000 -w", needErase, progress) ||
                            TryProgramFirmware(firmware, address, "-c port=SWD freq=1000 -w", needErase, progress) ||  // 尝试降低频率
                            TryProgramFirmware(firmware, address, "-c port=SWD -w", needErase, progress) ||  // 不指定频率
                            TryProgramFirmware(firmware, address, "-w -c port=SWD freq=4000", needErase, progress) ||  // 参数顺序变化
                            TryProgramFirmware(firmware, address, "--connect port=SWD freq=4000 --write", needErase, progress) ||
                            TryProgramFirmware(firmware, address, "--connect port=SWD freq=1000 --write", needErase, progress) ||  // 尝试降低频率
                            TryProgramFirmware(firmware, address, "--write --connect port=SWD freq=4000", needErase, progress))  // 参数顺序变化
                        {
                            success = true;
                        }
                    }
                    
                    if (!success)
                    {
                        LogMessage?.Invoke(this, "所有烧写尝试均失败");
                    }
                    
                    return success;
                });
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"[LogLevel.Error] 烧写固件时出错: {ex.Message}");
                LogMessage?.Invoke(this, $"错误详情: {ex}");
                return false;
            }
            finally
            {
                // 烧录完成后，重置烧录状态并延迟2秒后恢复自动刷新
                _isProgramming = false;
                ResumeAutoRefresh(2000); // 延迟2秒恢复刷新
            }
        }

        // 格式化ST-LINK工具命令，确保命令格式正确
        private string FormatSTLinkCommand(string baseCommand, string filePath, string address, string eraseParam, bool verify, bool reset)
        {
            // 参数设置
            string verifyParam = verify ? " -v" : "";
            string resetParam = reset ? " -rst" : "";
            
            string formattedCommand;
            
            // 根据命令格式调整参数顺序
            if (baseCommand.Contains("--write"))
            {
                // 长格式命令
                formattedCommand = $"{baseCommand} \"{filePath}\" {address}{verifyParam}{eraseParam}{resetParam}";
            }
            else if (baseCommand.Contains("-w"))
            {
                // 短格式命令
                formattedCommand = $"{baseCommand} \"{filePath}\" {address}{verifyParam}{eraseParam}{resetParam}";
            }
            else
            {
                // 其他格式，可能需要添加写入命令
                if (!baseCommand.Contains("-w") && !baseCommand.Contains("--write"))
                {
                    baseCommand += " -w";
                }
                formattedCommand = $"{baseCommand} \"{filePath}\" {address}{verifyParam}{eraseParam}{resetParam}";
            }
            
            // 确保不会有连续的空格
            return Regex.Replace(formattedCommand, @"\s+", " ").Trim();
        }

        // 尝试烧写固件
        private bool TryProgramFirmware(FirmwareFile firmware, string address, string programCommand, bool needErase, IProgress<int>? progress)
        {
            try
            {
                // 确定擦除参数
                string eraseParam = "";
                
                // 如果命令已经包含擦除指令，不再添加
                if (programCommand.Contains("-e") || programCommand.Contains("--erase"))
                {
                    eraseParam = "";
                    LogMessage?.Invoke(this, "[LogLevel.Debug] 命令已包含擦除指令");
                }
                else if (needErase)
                {
                    // BOOT固件需要擦除所有分区 - 先执行单独擦除
                    LogMessage?.Invoke(this, "[LogLevel.Warning] BOOT固件烧写，执行全片擦除...");
                    // 擦除前先确认连接
                    if (!VerifyConnection())
                    {
                        LogMessage?.Invoke(this, "[LogLevel.Error] 擦除前无法确认设备连接，请检查硬件连接");
                        return false;
                    }
                    
                    // 执行擦除命令
                    bool eraseResult = EraseChip();
                    if (!eraseResult)
                    {
                        LogMessage?.Invoke(this, "[LogLevel.Error] 芯片擦除失败，无法继续烧写");
                        return false;
                    }
                    
                    LogMessage?.Invoke(this, "[LogLevel.Info] 全片擦除成功，继续烧写过程");
                    progress?.Report(30);
                    
                    // 擦除后短暂延迟确保操作完成
                    Thread.Sleep(500);
                    
                    // 已单独擦除，命令中不再添加擦除
                    eraseParam = "";
                }
                else
                {
                    // APP固件只擦除必要的区域（由CLI工具自动处理）
                    eraseParam = "";
                    LogMessage?.Invoke(this, "[LogLevel.Info] APP固件烧写，仅擦除必要扇区");
                }
                
                // 根据固件类型决定是否自动复位
                bool shouldReset = firmware.Type == FirmwareType.App;
                if (shouldReset)
                {
                    LogMessage?.Invoke(this, "[LogLevel.Info] APP固件烧写后将执行硬件复位");
                }
                else
                {
                    LogMessage?.Invoke(this, "[LogLevel.Info] BOOT固件烧写后不执行复位");
                }
                
                // 使用新的命令格式化方法
                string fullCommand = FormatSTLinkCommand(
                    programCommand, 
                    firmware.FilePath, 
                    address, 
                    eraseParam, 
                    true,  // 始终验证
                    shouldReset  // 只有APP固件烧录时才复位
                );
                
                LogMessage?.Invoke(this, $"尝试烧写命令: {_stLinkUtilityPath} {fullCommand}");
                
                // 启动烧写进程
                var startInfo = new ProcessStartInfo
                {
                    FileName = _stLinkUtilityPath,
                    Arguments = fullCommand,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                progress?.Report(40);
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    LogMessage?.Invoke(this, "[LogLevel.Error] 无法启动STM32 Programmer CLI工具");
                    return false;
                }

                string output = "";
                string error = "";
                
                // 创建读取输出的任务
                var readOutputTask = Task.Run(() => {
                    output = process.StandardOutput.ReadToEnd();
                });
                
                var readErrorTask = Task.Run(() => {
                    error = process.StandardError.ReadToEnd();
                });
                
                // 等待进程结束，最多20秒(BOOT文件可能较大)
                bool exited = process.WaitForExit(20000);
                if (!exited)
                {
                    LogMessage?.Invoke(this, "[LogLevel.Error] 烧写操作超时，正在终止进程");
                    try { process.Kill(); } catch { }
                    return false;
                }
                
                // 等待读取输出完成
                Task.WaitAll(readOutputTask, readErrorTask);
                
                var exitCode = process.ExitCode;
                LogMessage?.Invoke(this, $"烧写命令退出代码: {exitCode}");
                
                // 不再在日志中显示CLI工具的详细输出
                // 仅在调试模式下记录输出长度信息
                if (!string.IsNullOrEmpty(output))
                {
                    LogMessage?.Invoke(this, $"[LogLevel.Debug] 收到CLI工具输出 ({output.Length} 字符)");
                }
                
                // 分析错误输出，但不在日志中显示完整错误内容
                if (!string.IsNullOrEmpty(error))
                {
                    // 仅在调试模式下记录错误长度信息
                    LogMessage?.Invoke(this, $"[LogLevel.Debug] CLI工具返回错误 ({error.Length} 字符)");
                    
                    // 尝试识别特定错误模式并给出简明提示
                    if (error.Contains("No ST-LINK detected"))
                    {
                        LogMessage?.Invoke(this, "[LogLevel.Error] 未检测到ST-LINK设备，请检查连接");
                        return false;
                    }
                    else if (error.Contains("failed to configure debug port"))
                    {
                        LogMessage?.Invoke(this, "[LogLevel.Error] 调试端口配置失败，请检查线路连接");
                        return false;
                    }
                    else if (error.Contains("Unknown device"))
                    {
                        LogMessage?.Invoke(this, "[LogLevel.Error] 未知设备，芯片型号可能不受支持");
                        return false;
                    }
                    else if (error.Contains("Error") && !error.Contains("Warning"))
                    {
                        return false;
                    }
                }
                
                progress?.Report(90);
                
                // 验证烧写结果
                bool success = output.Contains("Successfully") || 
                               output.Contains("成功") || 
                               output.Contains("Mass erase successfully achieved") ||
                               output.Contains("File download complete") ||
                               output.Contains("Download verified successfully") ||
                               (output.Contains("Download in Progress") && output.Contains("completed") && !error.Contains("Error")) ||
                               (exitCode == 0 && !error.Contains("Error"));
                
                // 检查是否为APP固件但烧写似乎成功了
                if (firmware.Type == FirmwareType.App && success)
                {
                    // 添加更严格的APP烧录成功判断
                    if (!output.Contains("Download verified successfully") || 
                        (!output.Contains("MCU Reset") && !output.Contains("Software reset is performed")))
                    {
                        LogMessage?.Invoke(this, "[LogLevel.Warning] APP固件烧写成功，但可能没有执行复位，尝试执行硬件复位...");
                        
                        // 尝试发送强制的硬件复位命令
                        if (TryHardwareReset())
                        {
                            LogMessage?.Invoke(this, "[LogLevel.Info] 已成功执行硬件复位");
                        }
                        else
                        {
                            LogMessage?.Invoke(this, "[LogLevel.Warning] 无法执行硬件复位，请手动复位设备");
                        }
                    }
                    else
                    {
                        LogMessage?.Invoke(this, "[LogLevel.Info] APP固件烧写成功并已执行复位");
                    }
                }
                
                if (success && !error.Contains("Error: Wrong verify command") && !error.Contains("missing the filePath"))
                {
                    progress?.Report(100);
                    LogMessage?.Invoke(this, "固件烧写成功");
                    return true;
                }
                else
                {
                    // 不显示详细错误信息，仅给出基本错误类型
                    string errorMessage = "固件烧写失败";
                    if (error.Contains("Wrong verify command"))
                    {
                        errorMessage += "，验证命令顺序不正确";
                    }
                    else if (error.Contains("missing the filePath"))
                    {
                        errorMessage += "，命令格式错误";
                    }
                    else if (output.Contains("Failed to init") || output.Contains("初始化失败"))
                    {
                        errorMessage += "，初始化失败";
                    }
                    else if (output.Contains("verify") && output.Contains("fail"))
                    {
                        errorMessage += "，验证失败";
                    }
                    
                    LogMessage?.Invoke(this, $"[LogLevel.Error] {errorMessage}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"[LogLevel.Error] 执行烧写命令时出错: {ex.Message}");
                return false;
            }
        }

        // 验证设备连接
        public bool VerifyConnection()
        {
            try
            {
                // 移除连接验证的日志显示
                ConnectionLog("验证设备连接状态...");
                
                // 构建连接检查命令
                var startInfo = new ProcessStartInfo
                {
                    FileName = _stLinkUtilityPath,
                    Arguments = "-c port=SWD freq=4000",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    LogMessage?.Invoke(this, "[LogLevel.Error] 无法启动STM32 Programmer CLI工具验证连接");
                    return false;
                }
                
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                
                bool connected = !process.WaitForExit(5000) || 
                                 output.Contains("ST-LINK") || 
                                 output.Contains("Connected");
                
                ConnectionLog(connected ? "设备连接状态正常" : "设备连接验证失败");
                return connected;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"[LogLevel.Error] 验证设备连接时出错: {ex.Message}");
                return false;
            }
        }

        // 执行芯片擦除
        private bool EraseChip()
        {
            try
            {
                LogMessage?.Invoke(this, "开始擦除芯片...");
                
                // 构建擦除命令
                var startInfo = new ProcessStartInfo
                {
                    FileName = _stLinkUtilityPath,
                    Arguments = "-c port=SWD freq=4000 -e all",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    LogMessage?.Invoke(this, "[LogLevel.Error] 无法启动STM32 Programmer CLI工具执行擦除");
                    return false;
                }
                
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                
                if (!process.WaitForExit(10000))
                {
                    LogMessage?.Invoke(this, "[LogLevel.Error] 擦除操作超时");
                    try { process.Kill(); } catch { }
                    return false;
                }
                
                bool success = output.Contains("Successfully") || 
                               output.Contains("成功") || 
                               (process.ExitCode == 0 && !error.Contains("Error"));
                
                if (success)
                {
                    LogMessage?.Invoke(this, "芯片擦除成功");
                    return true;
                }
                else
                {
                    // 不显示详细错误信息，只提供简要说明
                    LogMessage?.Invoke(this, "[LogLevel.Error] 芯片擦除失败");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"[LogLevel.Error] 执行芯片擦除时出错: {ex.Message}");
                return false;
            }
        }

        // 尝试复位MCU
        private bool TryResetMCU()
        {
            try
            {
                LogMessage?.Invoke(this, "尝试手动复位MCU...");
                
                // 构建复位命令
                var startInfo = new ProcessStartInfo
                {
                    FileName = _stLinkUtilityPath,
                    Arguments = "-c port=SWD freq=4000 -rst",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    LogMessage?.Invoke(this, "[LogLevel.Error] 无法启动STM32 Programmer CLI工具执行复位");
                    return false;
                }
                
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                
                if (!process.WaitForExit(5000))
                {
                    LogMessage?.Invoke(this, "[LogLevel.Error] 复位操作超时");
                    try { process.Kill(); } catch { }
                    return false;
                }
                
                bool success = output.Contains("Reset") || 
                               output.Contains("reset") || 
                               (process.ExitCode == 0 && !error.Contains("Error"));
                
                if (success)
                {
                    LogMessage?.Invoke(this, "MCU复位成功");
                    return true;
                }
                else
                {
                    // 不显示详细错误信息，只提供简要说明
                    LogMessage?.Invoke(this, "[LogLevel.Error] MCU复位失败");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"[LogLevel.Error] 执行MCU复位时出错: {ex.Message}");
                return false;
            }
        }

        // 尝试硬件复位
        private bool TryHardwareReset()
        {
            try
            {
                LogMessage?.Invoke(this, "尝试硬件复位...");
                
                // 构建硬件复位命令
                var startInfo = new ProcessStartInfo
                {
                    FileName = _stLinkUtilityPath,
                    Arguments = "-c port=SWD freq=4000 -rst",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    LogMessage?.Invoke(this, "[LogLevel.Error] 无法启动STM32 Programmer CLI工具执行硬件复位");
                    return false;
                }
                
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                
                if (!process.WaitForExit(5000))
                {
                    LogMessage?.Invoke(this, "[LogLevel.Error] 硬件复位超时");
                    try { process.Kill(); } catch { }
                    return false;
                }
                
                bool success = output.Contains("Reset") || 
                               output.Contains("reset") || 
                               (process.ExitCode == 0 && !error.Contains("Error"));
                
                if (success)
                {
                    LogMessage?.Invoke(this, "硬件复位成功");
                    return true;
                }
                else
                {
                    // 不显示详细错误信息，只提供简要说明
                    LogMessage?.Invoke(this, "[LogLevel.Error] 硬件复位失败");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"[LogLevel.Error] 执行硬件复位时出错: {ex.Message}");
                return false;
            }
        }

        // 只记录连接相关日志
        protected void ConnectionLog(string message)
        {
            try
            {
                // 只有当ShowConnectionLogs为true时才输出连接日志
                if (ShowConnectionLogs)
                {
                    LogMessage?.Invoke(this, $"[LogLevel.Debug][连接] {message}");
                }
            }
            catch
            {
                // 忽略日志记录失败，防止崩溃
            }
        }

        protected virtual void Log(string message)
        {
            try
            {
                LogMessage?.Invoke(this, $"[LogLevel.Info][设备] {message}");
            }
            catch
            {
                // 忽略日志记录失败，防止崩溃
            }
        }
        
        protected virtual void Log(string category, string message, LogLevel level = LogLevel.Info)
        {
            try
            {
                LogMessage?.Invoke(this, $"[{level}][{category}] {message}");
            }
            catch
            {
                // 忽略日志记录失败，防止崩溃
            }
        }
        
        #region 烧录后校验功能
        
        /// <summary>
        /// 烧录后校验 - 使用STM32_Programmer_CLI的-v命令校验
        /// </summary>
        /// <param name="firmware">原始固件文件</param>
        /// <param name="progress">进度回调</param>
        /// <returns>校验结果</returns>
        public async Task<VerifyResult> VerifyFirmwareAsync(FirmwareFile firmware, IProgress<int>? progress = null)
        {
            var result = new VerifyResult();
            
            try
            {
                return await Task.Run(() =>
                {
                    LogMessage?.Invoke(this, $"[LogLevel.Info] 开始校验固件: {firmware.FileName}");
                    progress?.Report(10);
                    
                    // 检查CLI工具
                    if (!File.Exists(_stLinkUtilityPath))
                    {
                        result.Success = false;
                        result.ErrorMessage = "STM32 Programmer CLI工具未找到";
                        return result;
                    }
                    
                    // 使用固件文件中配置的地址
                    string address = firmware.StartAddress;
                    LogMessage?.Invoke(this, $"[LogLevel.Info] 校验地址: {address}");
                    
                    progress?.Report(30);
                    
                    // 使用CLI工具的校验命令
                    string verifyCommand = $"-c port=SWD -v \"{firmware.FilePath}\" {address}";
                    LogMessage?.Invoke(this, $"[LogLevel.Debug] 校验命令: {verifyCommand}");
                    
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = _stLinkUtilityPath,
                        Arguments = verifyCommand,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    
                    progress?.Report(50);
                    
                    using var process = Process.Start(startInfo);
                    if (process == null)
                    {
                        result.Success = false;
                        result.ErrorMessage = "无法启动CLI工具";
                        return result;
                    }
                    
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(30000);
                    
                    progress?.Report(80);
                    
                    LogMessage?.Invoke(this, $"[LogLevel.Debug] 校验输出: {output}");
                    
                    // 分析校验结果
                    bool verified = output.Contains("Download verified successfully") ||
                                   output.Contains("verified successfully") ||
                                   output.Contains("Verify successfully") ||
                                   (process.ExitCode == 0 && !error.Contains("Error") && !output.Contains("failed"));
                    
                    if (verified)
                    {
                        result.Success = true;
                        result.Message = $"校验通过！固件数据一致 ({firmware.FileSize} 字节)";
                        result.OriginalSize = (int)firmware.FileSize;
                        result.ReadSize = (int)firmware.FileSize;
                        LogMessage?.Invoke(this, $"[LogLevel.Info] {result.Message}");
                    }
                    else
                    {
                        result.Success = false;
                        result.ErrorMessage = "校验失败，芯片数据与固件不一致";
                        if (!string.IsNullOrEmpty(error))
                        {
                            LogMessage?.Invoke(this, $"[LogLevel.Error] 校验错误: {error}");
                        }
                        LogMessage?.Invoke(this, $"[LogLevel.Error] {result.ErrorMessage}");
                    }
                    
                    progress?.Report(100);
                    return result;
                });
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"校验过程出错: {ex.Message}";
                LogMessage?.Invoke(this, $"[LogLevel.Error] {result.ErrorMessage}");
                return result;
            }
        }
        
        /// <summary>
        /// 快速校验 - 使用STM32_Programmer_CLI的内置校验功能
        /// </summary>
        public async Task<VerifyResult> QuickVerifyFirmwareAsync(FirmwareFile firmware, IProgress<int>? progress = null)
        {
            var result = new VerifyResult();
            
            try
            {
                return await Task.Run(() =>
                {
                    LogMessage?.Invoke(this, $"[LogLevel.Info] 开始快速校验: {firmware.FileName}");
                    progress?.Report(20);
                    
                    string address = firmware.Type == FirmwareType.Boot ? "0x08000000" : "0x08020000";
                    
                    // 使用-v fast进行快速校验
                    string verifyCommand = $"-c port=SWD -v fast \"{firmware.FilePath}\" {address}";
                    
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = _stLinkUtilityPath,
                        Arguments = verifyCommand,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    
                    progress?.Report(50);
                    
                    using var process = Process.Start(startInfo);
                    if (process == null)
                    {
                        result.Success = false;
                        result.ErrorMessage = "无法启动CLI工具";
                        return result;
                    }
                    
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(30000);
                    
                    progress?.Report(90);
                    
                    // 分析输出判断校验结果
                    if (output.Contains("verified successfully") || 
                        output.Contains("Verify successfully") ||
                        output.Contains("Download verified successfully"))
                    {
                        result.Success = true;
                        result.Message = "快速校验通过！";
                        LogMessage?.Invoke(this, "[LogLevel.Info] 快速校验通过");
                    }
                    else if (output.Contains("Verify failed") || error.Contains("Error"))
                    {
                        result.Success = false;
                        result.ErrorMessage = "快速校验失败，数据不一致";
                        LogMessage?.Invoke(this, "[LogLevel.Error] 快速校验失败");
                    }
                    else
                    {
                        result.Success = process.ExitCode == 0;
                        result.Message = result.Success ? "校验完成" : "校验结果未知";
                    }
                    
                    progress?.Report(100);
                    return result;
                });
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"校验出错: {ex.Message}";
                return result;
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// 校验结果
    /// </summary>
    public class VerifyResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
        public int OriginalSize { get; set; }
        public int ReadSize { get; set; }
        public int MismatchCount { get; set; }
        public int FirstMismatchOffset { get; set; } = -1;
    }
} 