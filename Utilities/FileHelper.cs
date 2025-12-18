using System.IO;
using System.Security.Cryptography;

namespace STM32Programmer.Utilities
{
    public static class FileHelper
    {
        /// <summary>
        /// 计算文件的SHA256哈希值
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>文件的SHA256哈希值</returns>
        public static string ComputeFileHash(string filePath)
        {
            if (!File.Exists(filePath))
                return string.Empty;

            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        /// <summary>
        /// 获取路径下的最新文件
        /// </summary>
        /// <param name="directoryPath">文件夹路径</param>
        /// <param name="searchPattern">搜索模式，例如*.bin</param>
        /// <param name="searchOption">搜索选项</param>
        /// <param name="containsKeyword">文件名必须包含的关键字</param>
        /// <returns>最新文件的完整路径</returns>
        public static string GetLatestFile(string directoryPath, string searchPattern, 
            SearchOption searchOption = SearchOption.TopDirectoryOnly, string containsKeyword = "")
        {
            if (!Directory.Exists(directoryPath))
                return string.Empty;

            var files = Directory.GetFiles(directoryPath, searchPattern, searchOption)
                .Where(f => string.IsNullOrEmpty(containsKeyword) || 
                            Path.GetFileName(f).ToLower().Contains(containsKeyword.ToLower()))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .ToArray();

            return files.Length > 0 ? files[0].FullName : string.Empty;
        }

        /// <summary>
        /// 检查路径是否有效并在必要时创建目录
        /// </summary>
        /// <param name="directoryPath">目录路径</param>
        /// <returns>目录是否存在或创建成功</returns>
        public static bool EnsureDirectoryExists(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath))
                return false;

            if (!Directory.Exists(directoryPath))
            {
                try
                {
                    Directory.CreateDirectory(directoryPath);
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 获取文件大小的友好显示
        /// </summary>
        /// <param name="byteCount">字节数</param>
        /// <returns>友好的大小显示</returns>
        public static string GetFileSizeDisplay(long byteCount)
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

        /// <summary>
        /// 安全删除文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>操作是否成功</returns>
        public static bool SafeDeleteFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                File.Delete(filePath);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
} 