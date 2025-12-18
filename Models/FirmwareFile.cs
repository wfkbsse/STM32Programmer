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

        public FirmwareFile()
        {
            FilePath = string.Empty;
            FileHash = string.Empty;
            IsValid = false;
        }

        public FirmwareFile(string path, FirmwareType type)
        {
            FilePath = path;
            Type = type;
            FileHash = string.Empty;
            IsValid = false;
        }

        public override string ToString()
        {
            return $"{FileName} ({FileSize / 1024} KB)";
        }
    }
} 