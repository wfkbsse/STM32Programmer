using System.IO;

namespace STM32Programmer.Models
{
    public enum FirmwareType
    {
        Boot,
        App
    }

    public class FirmwareFile
    {
        public string FilePath { get; set; }
        public string FileName => Path.GetFileName(FilePath);
        public FirmwareType Type { get; set; }
        public DateTime LastModified => File.Exists(FilePath) ? File.GetLastWriteTime(FilePath) : DateTime.MinValue;
        public long FileSize => File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
        public string FileHash { get; set; }
        public bool IsValid { get; set; }
        
        /// <summary>
        /// 烧录起始地址
        /// </summary>
        public string StartAddress { get; set; }

        public FirmwareFile()
        {
            FilePath = string.Empty;
            FileHash = string.Empty;
            IsValid = false;
            StartAddress = "0x08000000"; // 默认地址
        }

        public FirmwareFile(string path, FirmwareType type)
        {
            FilePath = path;
            Type = type;
            FileHash = string.Empty;
            IsValid = false;
            // 根据类型设置默认地址
            StartAddress = type == FirmwareType.Boot ? "0x08000000" : "0x08010000";
        }

        public override string ToString()
        {
            return $"{FileName} ({FileSize / 1024} KB)";
        }
    }
} 