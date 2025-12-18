using Microsoft.Win32;
using STM32Programmer.Models;
using System.IO;
using System.Security.Cryptography;

namespace STM32Programmer.Services
{
    public class FirmwareService
    {
        public event EventHandler<string>? LogMessage;
        private string _defaultSearchPath;

        public FirmwareService()
        {
            _defaultSearchPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            
            // 检查当前目录下是否有test_firmware文件夹
            string testFirmwarePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_firmware");
            if (Directory.Exists(testFirmwarePath))
            {
                _defaultSearchPath = testFirmwarePath;
                LogMessage?.Invoke(this, "检测到测试固件目录: " + testFirmwarePath);
            }
        }

        public void SetDefaultSearchPath(string path)
        {
            if (Directory.Exists(path))
            {
                _defaultSearchPath = path;
                LogMessage?.Invoke(this, "设置默认搜索路径: " + path);
            }
            else
            {
                LogMessage?.Invoke(this, "无效的搜索路径: " + path);
            }
        }

        public FirmwareFile? SelectFirmwareFile(FirmwareType type)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择" + (type == FirmwareType.Boot ? "BOOT" : "APP") + "固件文件",
                Filter = "固件文件 (*.bin;*.hex)|*.bin;*.hex|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = _defaultSearchPath
            };

            if (dialog.ShowDialog() == true)
            {
                var firmware = new FirmwareFile(dialog.FileName, type);
                ValidateFirmware(firmware);
                return firmware;
            }

            return null;
        }

        public async Task<FirmwareFile?> FindLatestFirmwareAsync(FirmwareType type, string searchPath = "")
        {
            try
            {
                return await Task.Run(() =>
                {
                    // 如果未指定搜索路径，使用默认路径
                    if (string.IsNullOrEmpty(searchPath))
                    {
                        searchPath = _defaultSearchPath;
                        LogMessage?.Invoke(this, "使用默认搜索路径: " + searchPath);
                    }

                    LogMessage?.Invoke(this, "正在搜索" + (type == FirmwareType.Boot ? "BOOT" : "APP") + "固件文件...");

                    // 搜索关键字
                    var keyword = type == FirmwareType.Boot ? "boot" : "app";
                    
                    // 查找所有匹配的文件
                    var files = Directory.GetFiles(searchPath, "*.bin", SearchOption.AllDirectories)
                        .Concat(Directory.GetFiles(searchPath, "*.hex", SearchOption.AllDirectories))
                        .Where(f => Path.GetFileName(f).ToLower().Contains(keyword))
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .ToList();

                    if (files.Count == 0)
                    {
                        LogMessage?.Invoke(this, "未找到" + (type == FirmwareType.Boot ? "BOOT" : "APP") + "固件文件");
                        return null;
                    }

                    // 取最新的文件
                    var latestFile = files.First();
                    LogMessage?.Invoke(this, "找到最新的" + (type == FirmwareType.Boot ? "BOOT" : "APP") + "固件文件: " + latestFile.Name + " (" + latestFile.LastWriteTime + ")");
                    
                    var firmware = new FirmwareFile(latestFile.FullName, type);
                    ValidateFirmware(firmware);
                    return firmware;
                });
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, "搜索固件文件时出错: " + ex.Message);
                return null;
            }
        }

        public bool ValidateFirmware(FirmwareFile firmware)
        {
            try
            {
                if (!File.Exists(firmware.FilePath))
                {
                    LogMessage?.Invoke(this, "文件不存在: " + firmware.FilePath);
                    firmware.IsValid = false;
                    return false;
                }

                // 计算文件哈希值
                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(firmware.FilePath);
                var hash = sha256.ComputeHash(stream);
                firmware.FileHash = BitConverter.ToString(hash).Replace("-", "").ToLower();

                // 检查文件大小
                if (firmware.FileSize == 0)
                {
                    LogMessage?.Invoke(this, "文件大小为0: " + firmware.FileName);
                    firmware.IsValid = false;
                    return false;
                }

                // 文件后缀检查
                var extension = Path.GetExtension(firmware.FilePath).ToLower();
                if (extension != ".bin" && extension != ".hex")
                {
                    LogMessage?.Invoke(this, "不支持的文件格式: " + extension);
                    firmware.IsValid = false;
                    return false;
                }

                // 文件名检查
                var fileName = Path.GetFileName(firmware.FilePath).ToLower();
                bool containsKeyword = firmware.Type == FirmwareType.Boot
                    ? fileName.Contains("boot")
                    : fileName.Contains("app");

                if (!containsKeyword)
                {
                    LogMessage?.Invoke(this, "警告: 文件名中未包含" + (firmware.Type == FirmwareType.Boot ? "boot" : "app") + "关键字");
                }

                firmware.IsValid = true;
                LogMessage?.Invoke(this, "固件验证通过: " + firmware.FileName + ", 大小: " + firmware.FileSize / 1024 + " KB, 哈希值: " + firmware.FileHash.Substring(0, 8) + "...");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, "验证固件时出错: " + ex.Message);
                firmware.IsValid = false;
                return false;
            }
        }

        public async Task<bool> CleanProjectFilesAsync(string projectPath)
        {
            try
            {
                return await Task.Run(() =>
                {
                    if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
                    {
                        LogMessage?.Invoke(this, "无效的项目路径");
                        return false;
                    }

                    LogMessage?.Invoke(this, "开始清理项目文件: " + projectPath);
                    int count = 0;

                    // 查找并删除编译生成的文件
                    var foldersToClean = new[] { "obj", "bin", "Debug", "Release", ".vs" };
                    var extensionsToClean = new[] { ".obj", ".o", ".d", ".pdb", ".bak", ".tmp", ".user" };

                    foreach (var folder in foldersToClean)
                    {
                        var dirs = Directory.GetDirectories(projectPath, folder, SearchOption.AllDirectories);
                        foreach (var dir in dirs)
                        {
                            try
                            {
                                Directory.Delete(dir, true);
                                LogMessage?.Invoke(this, "已删除目录: " + dir);
                                count++;
                            }
                            catch (Exception ex)
                            {
                                LogMessage?.Invoke(this, "无法删除目录: " + dir + ", 原因: " + ex.Message);
                            }
                        }
                    }

                    foreach (var ext in extensionsToClean)
                    {
                        var files = Directory.GetFiles(projectPath, $"*{ext}", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            try
                            {
                                File.Delete(file);
                                count++;
                            }
                            catch (Exception ex)
                            {
                                LogMessage?.Invoke(this, "无法删除文件: " + file + ", 原因: " + ex.Message);
                            }
                        }
                    }

                    LogMessage?.Invoke(this, "项目清理完成，共清理 " + count + " 个文件/目录");
                    return true;
                });
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, "清理项目文件时出错: " + ex.Message);
                return false;
            }
        }

        // 查找目录中所有符合特定类型的固件文件
        public async Task<List<FirmwareFile>> FindAllFirmwareFilesAsync(FirmwareType type, string searchPath = "")
        {
            try
            {
                return await Task.Run(() =>
                {
                    // 如果未指定搜索路径，使用默认路径
                    if (string.IsNullOrEmpty(searchPath))
                    {
                        searchPath = _defaultSearchPath;
                        LogMessage?.Invoke(this, "使用默认搜索路径: " + searchPath);
                    }

                    LogMessage?.Invoke(this, "正在搜索" + (type == FirmwareType.Boot ? "BOOT" : "APP") + "固件文件列表...");

                    // 搜索关键字
                    var keyword = type == FirmwareType.Boot ? "boot" : "app";
                    
                    // 查找所有匹配的文件
                    var files = Directory.GetFiles(searchPath, "*.bin", SearchOption.AllDirectories)
                        .Concat(Directory.GetFiles(searchPath, "*.hex", SearchOption.AllDirectories))
                        .Where(f => Path.GetFileName(f).ToLower().Contains(keyword))
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .ToList();

                    if (files.Count == 0)
                    {
                        LogMessage?.Invoke(this, "未找到" + (type == FirmwareType.Boot ? "BOOT" : "APP") + "固件文件");
                        return new List<FirmwareFile>();
                    }

                    LogMessage?.Invoke(this, $"找到{files.Count}个{(type == FirmwareType.Boot ? "BOOT" : "APP")}固件文件");
                    
                    // 创建并返回固件文件列表
                    var firmwareList = new List<FirmwareFile>();
                    foreach (var file in files)
                    {
                        var firmware = new FirmwareFile(file.FullName, type);
                        ValidateFirmware(firmware);
                        firmwareList.Add(firmware);
                    }
                    
                    return firmwareList;
                });
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, "搜索固件文件列表时出错: " + ex.Message);
                return new List<FirmwareFile>();
            }
        }

        protected virtual void Log(string message)
        {
            Log("固件", message, LogLevel.Info);
        }
        
        protected virtual void Log(string category, string message, LogLevel level = LogLevel.Info)
        {
            LogMessage?.Invoke(this, "[" + level + "][" + category + "] " + message);
        }
    }
} 