using System;

namespace STM32Programmer.Models
{
    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        Debug,      // 调试信息，最详细
        Info,       // 一般信息
        Warning,    // 警告信息
        Error,      // 错误信息
        Critical    // 严重错误
    }
    
    /// <summary>
    /// 日志条目
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; }
        public string Category { get; set; }
        public LogLevel Level { get; set; }
        
        public LogEntry(string message, string category = "", LogLevel level = LogLevel.Info)
        {
            Timestamp = DateTime.Now;
            Message = message;
            Category = category;
            Level = level;
        }
        
        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}][{Level}][{Category}] {Message}";
        }
        
        // 获取应该用于显示此日志的颜色
        public string GetColor
        {
            get
            {
                return Level switch
                {
                    LogLevel.Debug => "#808080",    // 灰色
                    LogLevel.Info => "#000000",     // 黑色
                    LogLevel.Warning => "#FF8C00",  // 橙色
                    LogLevel.Error => "#FF0000",    // 红色
                    LogLevel.Critical => "#8B0000", // 深红色
                    _ => "#000000"                  // 默认黑色
                };
            }
        }
    }
} 