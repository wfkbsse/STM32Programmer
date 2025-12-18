using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using System.IO;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace STM32Programmer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private readonly object _logLock = new object();
    
    public App()
    {
        // 记录启动日志
        LogStartup("应用程序构造函数初始化");
        
        // 注册全局异常处理
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += Application_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        
        // 注册应用程序生命周期事件
        Startup += App_Startup;
        Exit += App_Exit;
        
        // 设置启动参数
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        
        LogStartup("应用程序构造函数完成");
    }
    
    private void App_Startup(object? sender, StartupEventArgs e)
    {
        LogStartup("应用程序正在启动");
        try
        {
            // 如果需要，可以在这里添加更多的初始化逻辑
        }
        catch (Exception ex)
        {
            HandleException("启动过程", ex);
        }
    }
    
    private void App_Exit(object? sender, ExitEventArgs e)
    {
        LogStartup("应用程序正在退出");
    }
    
    private void Application_DispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogStartup($"UI线程异常: {e.Exception?.Message}");
        HandleException("UI线程异常", e.Exception);
        e.Handled = true; // 标记为已处理，防止应用崩溃
    }
    
    private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        LogStartup($"应用域未处理异常: {(e.ExceptionObject as Exception)?.Message}");
        HandleException("应用域未处理异常", e.ExceptionObject as Exception);
    }
    
    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // 使用Dispatcher确保在UI线程上执行
        Dispatcher.BeginInvoke(new Action(() =>
        {
            LogStartup($"任务异常: {e.Exception?.Message}");
            HandleException("任务异常", e.Exception);
        }));
        
        e.SetObserved(); // 标记为已观察，防止应用崩溃
    }
    
    private void HandleException(string source, Exception? ex)
    {
        try
        {
            if (ex == null)
            {
                return;
            }
            
            string errorMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex.Message}\n{ex.StackTrace}";
            
            // 记录到日志文件
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_logs");
            Directory.CreateDirectory(logPath);
            string logFile = Path.Combine(logPath, $"error_{DateTime.Now:yyyyMMdd}.log");
            
            lock (_logLock)
            {
                File.AppendAllText(logFile, errorMessage + "\n\n");
            }
            
            // 在UI线程上显示错误对话框
            Dispatcher.BeginInvoke(new Action(() =>
            {
                System.Windows.MessageBox.Show($"程序遇到了一个问题，已记录错误信息。\n\n错误来源: {source}\n错误详情: {ex.Message}\n\n详细日志已保存到: {logFile}", 
                    "应用程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }));
        }
        catch
        {
            // 即使异常处理代码出现问题，也不再抛出异常
        }
    }
    
    private void LogStartup(string message)
    {
        try
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_logs");
            Directory.CreateDirectory(logPath);
            string logFile = Path.Combine(logPath, $"startup_{DateTime.Now:yyyyMMdd}.log");
            
            lock (_logLock)
            {
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
            }
            
            Debug.WriteLine($"[启动日志] {message}");
        }
        catch
        {
            // 忽略日志记录失败
        }
    }
}

