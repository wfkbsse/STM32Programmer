using STM32Programmer.Models;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace STM32Programmer.Services
{
    public class LogService
    {
        private readonly object _lockObject = new object();
        private readonly string _logFilePath;
        
        // 用于UI绑定的可观察集合
        public ObservableCollection<LogEntry> LogEntries { get; } = new ObservableCollection<LogEntry>();
        public ObservableCollection<LogEntry> OperationLogEntries { get; } = new ObservableCollection<LogEntry>();
        
        // 当前设置
        public LogLevel MinLogLevel { get; set; } = LogLevel.Debug;
        public bool EnableFileLogging { get; set; } = true;
        public bool ShowTimestamps { get; set; } = true;
        public int MaxDisplayedEntries { get; set; } = 1000;
        
        // 事件
        public event EventHandler<LogEntry>? NewLogEntry;
        public event EventHandler<string>? LogMessageAdded;
        
        // 防止递归的标志
        private bool _isAddingLog = false;
        
        public LogService()
        {
            // 创建日志文件路径
            string appDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "STM32Programmer");
                
            if (!Directory.Exists(appDataFolder))
            {
                Directory.CreateDirectory(appDataFolder);
            }
            
            _logFilePath = Path.Combine(appDataFolder, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            
            // 日志启动信息
            LogInfo("日志系统", "日志服务已启动");
            LogInfo("日志系统", $"日志文件路径: {_logFilePath}");
        }
        
        // 添加一条新日志
        public void AddLog(LogEntry entry)
        {
            if (entry.Level < MinLogLevel) return;
            
            // 防止递归调用
            if (_isAddingLog) return;
            
            try
            {
                _isAddingLog = true;
                
                lock (_lockObject)
                {
                    // 更新UI集合
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // 确定添加到哪个日志集合
                        var targetCollection = IsOperationLog(entry) ? OperationLogEntries : LogEntries;
                        
                        // 添加新日志
                        targetCollection.Add(entry);
                        
                        // 如果超过最大数量，移除最旧的
                        while (targetCollection.Count > MaxDisplayedEntries)
                        {
                            targetCollection.RemoveAt(0);
                        }
                    });
                    
                    // 写入文件
                    if (EnableFileLogging)
                    {
                        Task.Run(() => WriteLogToFile(entry));
                    }
                    
                    // 触发事件
                    NewLogEntry?.Invoke(this, entry);
                    // 同时触发旧的字符串格式事件，保持兼容性
                    LogMessageAdded?.Invoke(this, entry.ToString());
                }
            }
            finally
            {
                _isAddingLog = false;
            }
        }
        
        // 添加不同级别的日志的便捷方法
        public void LogDebug(string category, string message) => AddLog(new LogEntry(message, category, LogLevel.Debug));
        public void LogInfo(string category, string message) => AddLog(new LogEntry(message, category, LogLevel.Info));
        public void LogWarning(string category, string message) => AddLog(new LogEntry(message, category, LogLevel.Warning));
        public void LogError(string category, string message) => AddLog(new LogEntry(message, category, LogLevel.Error));
        public void LogCritical(string category, string message) => AddLog(new LogEntry(message, category, LogLevel.Critical));
        
        // 清除所有日志
        public void ClearLogs()
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LogEntries.Clear();
                OperationLogEntries.Clear();
            });
            
            LogInfo("日志系统", "日志已清除");
        }
        
        // 保存日志到文件
        public async Task SaveLogsToFileAsync(string filePath)
        {
            try
            {
                LogInfo("日志系统", $"正在保存日志到文件: {filePath}");
                
                using (var writer = new StreamWriter(filePath))
                {
                    await writer.WriteLineAsync($"--- STM32Programmer 日志导出 ---");
                    await writer.WriteLineAsync($"--- 导出时间: {DateTime.Now} ---");
                    await writer.WriteLineAsync($"--- 系统日志 ---");
                    
                    foreach (var entry in LogEntries)
                    {
                        await writer.WriteLineAsync(entry.ToString());
                    }
                    
                    await writer.WriteLineAsync($"--- 操作日志 ---");
                    foreach (var entry in OperationLogEntries)
                    {
                        await writer.WriteLineAsync(entry.ToString());
                    }
                }
                
                LogInfo("日志系统", "日志已成功保存");
            }
            catch (Exception ex)
            {
                LogError("日志系统", $"保存日志时出错: {ex.Message}");
            }
        }
        
        // 写入日志到文件
        private void WriteLogToFile(LogEntry entry)
        {
            try
            {
                File.AppendAllText(_logFilePath, entry.ToString() + Environment.NewLine);
            }
            catch (Exception)
            {
                // 文件写入错误时不做额外处理，避免递归错误日志
            }
        }
        
        // 判断是否为操作日志
        private bool IsOperationLog(LogEntry entry)
        {
            // 如果类别中包含这些关键词，认为是操作日志
            string[] operationCategories = { "操作", "命令", "设备", "烧写", "连接", "执行" };
            
            foreach (var keyword in operationCategories)
            {
                if (entry.Category.Contains(keyword)) return true;
                if (entry.Message.Contains("命令") || entry.Message.Contains("烧写")) return true;
            }
            
            return false;
        }
    }
} 