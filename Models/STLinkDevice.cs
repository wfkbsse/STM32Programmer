namespace STM32Programmer.Models
{
    public enum ConnectionStatus
    {
        Disconnected,
        Connected,
        Error
    }

    public class STLinkDevice
    {
        public string SerialNumber { get; set; }
        public string FirmwareVersion { get; set; }
        public ConnectionStatus Status { get; set; }
        public bool IsChipConnected { get; set; }
        public string ChipType { get; set; }
        public string ChipID { get; set; }
        public string ErrorMessage { get; set; }

        public STLinkDevice()
        {
            SerialNumber = string.Empty;
            FirmwareVersion = string.Empty;
            Status = ConnectionStatus.Disconnected;
            IsChipConnected = false;
            ChipType = string.Empty;
            ChipID = string.Empty;
            ErrorMessage = string.Empty;
        }

        public override string ToString()
        {
            if (Status == ConnectionStatus.Connected)
            {
                return $"ST-LINK #{SerialNumber}, 固件版本: {FirmwareVersion}";
            }
            else if (Status == ConnectionStatus.Error)
            {
                return $"ST-LINK 连接错误: {ErrorMessage}";
            }
            else
            {
                return "ST-LINK 未连接";
            }
        }
    }
} 