using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using STM32Programmer.Models;

namespace STM32Programmer.Services
{
    /// <summary>
    /// STM32串口烧录服务，实现通过STM32内置的串口Bootloader进行固件烧录
    /// </summary>
    public class SerialBurnService
    {
        private SerialPort _serialPort;
        private readonly LogService _logService;
        private const int DefaultReadTimeout = 1000; // 默认读取超时时间(毫秒)
        private const int DefaultWriteTimeout = 1000; // 默认写入超时时间(毫秒)

        // Bootloader命令
        private readonly byte[] CMD_GET = new byte[] { 0x00, 0xFF }; // 获取命令
        private readonly byte[] CMD_GET_VERSION = new byte[] { 0x01, 0xFE }; // 获取版本
        private readonly byte[] CMD_GET_ID = new byte[] { 0x02, 0xFD }; // 获取芯片ID
        private readonly byte[] CMD_READ_MEMORY = new byte[] { 0x11, 0xEE }; // 读取内存
        private readonly byte[] CMD_GO = new byte[] { 0x21, 0xDE }; // 跳转执行
        private readonly byte[] CMD_WRITE_MEMORY = new byte[] { 0x31, 0xCE }; // 写入内存
        private readonly byte[] CMD_ERASE = new byte[] { 0x43, 0xBC }; // 擦除扇区
        private readonly byte[] CMD_ERASE_EXT = new byte[] { 0x44, 0xBB }; // 扩展擦除命令
        private readonly byte[] ACK = new byte[] { 0x79 }; // 应答成功
        private readonly byte[] NACK = new byte[] { 0x1F }; // 应答失败

        // 电平信号
        public enum PinState
        {
            Low = 0,
            High = 1
        }

        // 引导模式类型
        public enum BootModeType
        {
            AutoSwitch = 0,     // 自动切换
            ManualPinControl,   // 手动控制引脚
            UseResetCommand     // 使用复位命令
        }

        public event EventHandler<int> ProgressChanged = delegate { };
        public event EventHandler<string> StatusChanged = delegate { };
        public event EventHandler<bool> OperationCompleted = delegate { };

        public bool IsConnected => _serialPort?.IsOpen == true;
        
        public SerialBurnService(LogService logService)
        {
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));
            _serialPort = new SerialPort();
        }

        /// <summary>
        /// 连接到串口
        /// </summary>
        public bool Connect(string portName, int baudRate, int dataBits, StopBits stopBits, Parity parity)
        {
            try
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.Close();
                }

                _serialPort = new SerialPort
                {
                    PortName = portName,
                    BaudRate = baudRate,
                    DataBits = dataBits,
                    StopBits = stopBits,
                    Parity = parity,
                    ReadTimeout = DefaultReadTimeout,
                    WriteTimeout = DefaultWriteTimeout
                };

                _serialPort.Open();
                return true;
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"串口连接失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 断开串口连接
        /// </summary>
        public void Disconnect()
        {
            try
            {
                if (_serialPort?.IsOpen == true)
                {
                    _serialPort.Close();
                }
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"断开串口连接失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 发送数据到串口
        /// </summary>
        public void SendData(string data)
        {
            try
            {
                if (_serialPort?.IsOpen != true)
                {
                    _logService?.LogWarning("串口", "串口未连接，无法发送数据");
                    return;
                }
                
                _serialPort.WriteLine(data);
                _logService?.LogInfo("串口", $"已发送数据: {data}");
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"发送数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 进入Bootloader模式
        /// </summary>
        public async Task<bool> EnterBootloaderMode(BootModeType bootModeType)
        {
            try
            {
                if (!IsConnected)
                {
                    _logService?.LogError("串口", "串口未连接，无法进入Bootloader模式");
                    return false;
                }

                StatusChanged?.Invoke(this, "正在进入Bootloader模式...");

                switch (bootModeType)
                {
                    case BootModeType.AutoSwitch:
                        // 自动切换模式：先尝试DTR/RTS控制，然后再尝试发送命令
                        await EnterBootloaderWithPins();
                        break;
                    case BootModeType.ManualPinControl:
                        // 手动控制引脚模式
                        await EnterBootloaderWithPins();
                        break;
                    case BootModeType.UseResetCommand:
                        // 使用复位命令模式
                        await EnterBootloaderWithCommand();
                        break;
                }

                // 发送同步字节
                await SendSyncByte();

                // 增加延迟，等待设备稳定
                await Task.Delay(300);

                // 测试Bootloader是否就绪
                if (await IsBootloaderReady())
                {
                    StatusChanged?.Invoke(this, "成功进入Bootloader模式");
                    return true;
                }

                StatusChanged?.Invoke(this, "进入Bootloader模式失败");
                return false;
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"进入Bootloader模式失败: {ex.Message}");
                StatusChanged?.Invoke(this, $"进入Bootloader模式失败: {ex.Message}");
                return false;
            }
        }

        // 使用引脚控制进入Bootloader模式
        private async Task<bool> EnterBootloaderWithPins()
        {
            try
            {
                if (!IsConnected)
                    return false;

                _logService?.LogInfo("串口", "尝试使用引脚控制进入Bootloader模式");

                // 确保RTS和DTR可用
                if (!_serialPort.RtsEnable && !_serialPort.DtrEnable)
                {
                    _serialPort.RtsEnable = true;
                    _serialPort.DtrEnable = true;
                }

                // STM32引导模式引脚控制标准流程：
                // 1. BOOT0 = 高电平(1)
                // 2. RESET = 低电平(0)，触发复位
                // 3. 等待足够时间确保复位完成
                // 4. RESET = 高电平(1)，释放复位
                // 5. 等待足够时间确保引导程序初始化完成

                // 根据标准STM32F1文档，使用以下引脚信号映射：
                // - RTS = BOOT0 (BOOT引脚)
                // - DTR = RESET_N (复位引脚)
                // 注意：DTR/RTS信号在串口逻辑中是低有效的，即：
                // RTS=false 意味着BOOT0=高电平，RTS=true 意味着BOOT0=低电平
                // DTR=false 意味着RESET=高电平，DTR=true 意味着RESET=低电平

                // 步骤1：确保RESET处于高电平状态（不复位）
                _serialPort.DtrEnable = false; // RESET = 高
                await Task.Delay(100);

                // 步骤2：设置BOOT0为高电平，选择系统内存（引导加载程序）启动
                _serialPort.RtsEnable = false; // BOOT0 = 高电平
                await Task.Delay(100);

                // 步骤3：拉低RESET引脚触发复位
                _serialPort.DtrEnable = true; // RESET = 低（触发复位）
                await Task.Delay(200); // 等待200ms确保复位完成

                // 步骤4：释放RESET，芯片从Bootloader启动
                _serialPort.DtrEnable = false; // RESET = 高（释放复位）
                
                // 步骤5：等待足够时间确保引导加载程序初始化
                await Task.Delay(500);
                
                _logService?.LogInfo("串口", "引脚控制序列完成，设备应已进入Bootloader模式");
                return true;
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"使用引脚控制进入Bootloader失败: {ex.Message}");
                return false;
            }
        }

        // 使用命令进入Bootloader模式
        private async Task<bool> EnterBootloaderWithCommand()
        {
            try
            {
                if (!IsConnected)
                    return false;

                _logService?.LogInfo("串口", "尝试使用命令进入Bootloader模式");
                
                // 参考ST官方文档AN3155和AN2606
                // STM32 USART引导加载器协议：
                // 1. 引导加载器通信始于发送0x7F同步字节
                // 2. 设备会应答0x79（ACK）或0x1F（NACK）
                // 3. 引导加载器支持多项命令，包括获取版本、读保护等
                
                // 先清空缓冲区
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                
                // 发送多个同步字节(0x7F)以确保通信同步
                for (int i = 0; i < 10; i++)
                {
                    _serialPort.Write(new byte[] { 0x7F }, 0, 1);
                    await Task.Delay(10); // 短暂延迟
                }
                
                // 等待设备响应
                await Task.Delay(100);
                
                // 如果设备在Bootloader模式中，应该会响应ACK(0x79)
                // 我们还可以使用Get命令获取Bootloader版本确认是否成功进入
                byte[] getCmd = new byte[] { 0x00, 0xFF }; // Get命令及校验
                _serialPort.Write(getCmd, 0, getCmd.Length);
                
                // 等待设备响应足够时间
                await Task.Delay(500);
                
                _logService?.LogInfo("串口", "命令已发送，等待设备进入Bootloader模式");
                return true;
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"使用命令进入Bootloader失败: {ex.Message}");
                return false;
            }
        }

        // 发送同步字节
        private async Task<bool> SendSyncByte()
        {
            // 尝试多次发送同步字节，增加成功率
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    _logService?.LogInfo("串口", $"发送同步字节 (第{i+1}次尝试)");
                    
                    // 清空缓冲区
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                    
                    // 发送同步字节 0x7F
                    _serialPort.Write(new byte[] { 0x7F }, 0, 1);
                    
                    await Task.Delay(100);  // 增加延迟到100ms
                    
                    // 尝试读取ACK响应
                    if (_serialPort.BytesToRead > 0)
                    {
                        byte[] response = new byte[1];
                        _serialPort.Read(response, 0, 1);
                        
                        if (response[0] == ACK[0])
                        {
                            _logService?.LogInfo("串口", $"收到同步字节ACK响应 (第{i+1}次尝试成功)");
                            return true;
                        }
                        else
                        {
                            _logService?.LogWarning("串口", $"收到非ACK响应: 0x{response[0]:X2} (第{i+1}次尝试)");
                        }
                    }
                    else
                    {
                        _logService?.LogWarning("串口", $"未收到同步字节响应 (第{i+1}次尝试)");
                    }
                    
                    // 如果不是最后一次尝试，等待一段时间后重试
                    if (i < 2)
                    {
                        await Task.Delay(200);  // 重试前等待200ms
                    }
                }
                catch (Exception ex)
                {
                    _logService?.LogError("串口", $"第{i+1}次发送同步字节失败: {ex.Message}");
                    
                    // 如果不是最后一次尝试，继续尝试
                    if (i < 2)
                    {
                        await Task.Delay(200);
                        continue;
                    }
                    return false;
                }
            }
            
            _logService?.LogWarning("串口", "发送同步字节失败，尝试3次均未成功");
            return false;
        }

        // 检测Bootloader是否就绪
        private async Task<bool> IsBootloaderReady()
        {
            // 尝试多次GET命令，最多重试3次
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    _logService?.LogInfo("串口", $"尝试检测Bootloader就绪状态 (第{i+1}次尝试)");
                    
                    // 清空缓冲区
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                    
                    // 发送GET命令测试Bootloader是否响应
                    _serialPort.Write(CMD_GET, 0, CMD_GET.Length);
                    
                    await Task.Delay(200);  // 增加延迟到200ms，给设备更多响应时间
                    
                    // 读取应答
                    if (_serialPort.BytesToRead > 0)
                    {
                        byte[] response = new byte[1];
                        _serialPort.Read(response, 0, 1);
                        
                        if (response[0] == ACK[0])
                        {
                            _logService?.LogInfo("串口", $"Bootloader准备就绪 (第{i+1}次尝试成功)");
                            return true;
                        }
                        else
                        {
                            _logService?.LogWarning("串口", $"收到非ACK响应: 0x{response[0]:X2} (第{i+1}次尝试)");
                        }
                    }
                    else
                    {
                        _logService?.LogWarning("串口", $"未收到响应 (第{i+1}次尝试)");
                    }
                    
                    // 如果不是最后一次尝试，则等待一段时间后重试
                    if (i < 2)
                    {
                        await Task.Delay(300);  // 重试前等待300ms
                    }
                }
                catch (Exception ex)
                {
                    _logService?.LogError("串口", $"第{i+1}次检测Bootloader就绪状态失败: {ex.Message}");
                    
                    // 如果不是最后一次尝试，则继续尝试
                    if (i < 2)
                    {
                        await Task.Delay(300);  // 重试前等待300ms
                        continue;
                    }
                    return false;
                }
            }
            
            _logService?.LogWarning("串口", "Bootloader未响应GET命令，尝试3次均失败");
            return false;
        }

        /// <summary>
        /// 获取芯片ID
        /// </summary>
        public async Task<string> GetDeviceId()
        {
            try
            {
                if (!IsConnected)
                {
                    _logService?.LogError("串口", "串口未连接，无法获取设备ID");
                    return string.Empty;
                }

                // 清空缓冲区
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                
                // 发送GET ID命令
                _serialPort.Write(CMD_GET_ID, 0, CMD_GET_ID.Length);
                
                await Task.Delay(100);
                
                // 读取应答
                if (_serialPort.BytesToRead > 0)
                {
                    // 读取ACK
                    byte[] ackResponse = new byte[1];
                    _serialPort.Read(ackResponse, 0, 1);
                    
                    if (ackResponse[0] != ACK[0])
                    {
                        _logService?.LogError("串口", "获取设备ID失败: 未收到ACK");
                        return string.Empty;
                    }
                    
                    // 读取ID数据长度
                    byte[] lengthBytes = new byte[1];
                    _serialPort.Read(lengthBytes, 0, 1);
                    int length = lengthBytes[0] + 1; // +1 是因为STM32协议中长度字节不包括自身
                    
                    // 读取PID数据
                    byte[] pidData = new byte[length];
                    _serialPort.Read(pidData, 0, length);
                    
                    // 读取结束ACK
                    byte[] endAck = new byte[1];
                    _serialPort.Read(endAck, 0, 1);
                    
                    if (endAck[0] != ACK[0])
                    {
                        _logService?.LogError("串口", "获取设备ID失败: 未收到结束ACK");
                        return string.Empty;
                    }
                    
                    // 解析PID
                    string deviceId = BitConverter.ToString(pidData).Replace("-", "");
                    _logService?.LogInfo("串口", $"获取设备ID成功: {deviceId}");
                    
                    return deviceId;
                }
                
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"获取设备ID失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 获取Bootloader版本
        /// </summary>
        public async Task<string> GetBootloaderVersion()
        {
            try
            {
                if (!IsConnected)
                {
                    _logService?.LogError("串口", "串口未连接，无法获取Bootloader版本");
                    return string.Empty;
                }

                // 清空缓冲区
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                
                // 发送GET VERSION命令
                _serialPort.Write(CMD_GET_VERSION, 0, CMD_GET_VERSION.Length);
                
                await Task.Delay(100);
                
                // 读取应答
                if (_serialPort.BytesToRead > 0)
                {
                    // 读取ACK
                    byte[] ackResponse = new byte[1];
                    _serialPort.Read(ackResponse, 0, 1);
                    
                    if (ackResponse[0] != ACK[0])
                    {
                        _logService?.LogError("串口", "获取Bootloader版本失败: 未收到ACK");
                        return string.Empty;
                    }
                    
                    // 读取版本信息 (通常为3字节)
                    byte[] versionBytes = new byte[3];
                    _serialPort.Read(versionBytes, 0, 3);
                    
                    // 读取结束ACK
                    byte[] endAck = new byte[1];
                    _serialPort.Read(endAck, 0, 1);
                    
                    // 格式化版本信息
                    string version = $"{versionBytes[0]}.{versionBytes[1]}.{versionBytes[2]}";
                    _logService?.LogInfo("串口", $"获取Bootloader版本成功: {version}");
                    
                    return version;
                }
                
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"获取Bootloader版本失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 擦除Flash
        /// </summary>
        public async Task<bool> EraseFlash(bool massErase = true)
        {
            try
            {
                if (!IsConnected)
                {
                    _logService?.LogError("串口", "串口未连接，无法擦除Flash");
                    return false;
                }

                StatusChanged?.Invoke(this, "正在擦除Flash...");
                
                // 清空缓冲区
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                
                // 发送ERASE命令
                _serialPort.Write(CMD_ERASE, 0, CMD_ERASE.Length);
                
                await Task.Delay(100);
                
                // 读取应答
                byte[] ackResponse = new byte[1];
                _serialPort.Read(ackResponse, 0, 1);
                
                if (ackResponse[0] != ACK[0])
                {
                    _logService?.LogError("串口", "擦除Flash失败: 未收到ACK");
                    return false;
                }
                
                // 全片擦除
                if (massErase)
                {
                    // 发送全片擦除命令 (0xFF)
                    _serialPort.Write(new byte[] { 0xFF, 0x00 }, 0, 2);  // 0xFF and checksum
                }
                else
                {
                    // TODO: 实现部分擦除逻辑
                    // 需要发送要擦除的页数量和页号列表
                }
                
                // 擦除可能需要较长时间，延长超时
                _serialPort.ReadTimeout = 30000;  // 30秒
                
                // 读取擦除完成应答
                try
                {
                    byte[] eraseAck = new byte[1];
                    _serialPort.Read(eraseAck, 0, 1);
                    
                    // 恢复原超时设置
                    _serialPort.ReadTimeout = DefaultReadTimeout;
                    
                    if (eraseAck[0] == ACK[0])
                    {
                        _logService?.LogInfo("串口", "Flash擦除成功");
                        StatusChanged?.Invoke(this, "Flash擦除成功");
                        return true;
                    }
                    else
                    {
                        _logService?.LogError("串口", "Flash擦除失败: 未收到完成ACK");
                        return false;
                    }
                }
                catch (TimeoutException)
                {
                    _logService?.LogError("串口", "Flash擦除超时，可能是设备不支持全片擦除");
                    return false;
                }
                finally
                {
                    // 确保恢复原超时设置
                    _serialPort.ReadTimeout = DefaultReadTimeout;
                }
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"擦除Flash失败: {ex.Message}");
                StatusChanged?.Invoke(this, $"擦除Flash失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 烧录固件
        /// </summary>
        public async Task<bool> UploadFirmware(byte[] firmwareData, uint startAddress = 0x08000000)
        {
            if (firmwareData == null || firmwareData.Length == 0)
            {
                _logService?.LogError("串口", "固件数据为空，无法烧录");
                return false;
            }

            try
            {
                if (!IsConnected)
                {
                    _logService?.LogError("串口", "串口未连接，无法烧录固件");
                    return false;
                }

                StatusChanged?.Invoke(this, "准备烧录固件...");

                // 先擦除Flash
                if (!await EraseFlash(true))
                {
                    StatusChanged?.Invoke(this, "固件烧录失败: 擦除Flash失败");
                    return false;
                }

                StatusChanged?.Invoke(this, "正在写入固件...");
                ProgressChanged?.Invoke(this, 0);

                // 固件数据分块上传
                const int BLOCK_SIZE = 256; // 每次写入256字节
                int totalBlocks = (firmwareData.Length + BLOCK_SIZE - 1) / BLOCK_SIZE;
                int currentBlock = 0;
                int bytesUploaded = 0;

                while (bytesUploaded < firmwareData.Length)
                {
                    // 计算当前块的大小
                    int currentBlockSize = Math.Min(BLOCK_SIZE, firmwareData.Length - bytesUploaded);
                    
                    // 计算当前块的地址
                    uint currentAddress = startAddress + (uint)bytesUploaded;
                    
                    // 写入当前块
                    if (!await WriteMemoryBlock(currentAddress, 
                                              firmwareData.Skip(bytesUploaded).Take(currentBlockSize).ToArray()))
                    {
                        _logService?.LogError("串口", $"写入地址0x{currentAddress:X8}处的数据块失败");
                        StatusChanged?.Invoke(this, $"固件烧录失败: 写入地址0x{currentAddress:X8}处的数据块失败");
                        return false;
                    }
                    
                    // 更新进度
                    bytesUploaded += currentBlockSize;
                    currentBlock++;
                    int progressPercentage = (int)(bytesUploaded * 100.0 / firmwareData.Length);
                    ProgressChanged?.Invoke(this, progressPercentage);
                    
                    // 短暂延时，防止设备过载
                    await Task.Delay(10);
                }

                StatusChanged?.Invoke(this, "固件烧录完成");
                ProgressChanged?.Invoke(this, 100);
                OperationCompleted?.Invoke(this, true);
                
                return true;
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"烧录固件失败: {ex.Message}");
                StatusChanged?.Invoke(this, $"烧录固件失败: {ex.Message}");
                OperationCompleted?.Invoke(this, false);
                return false;
            }
        }

        /// <summary>
        /// 写入内存块
        /// </summary>
        private async Task<bool> WriteMemoryBlock(uint address, byte[] data)
        {
            try
            {
                if (data == null || data.Length == 0 || data.Length > 256)
                {
                    _logService?.LogError("串口", "写入数据无效");
                    return false;
                }

                // 清空缓冲区
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                
                // 发送写内存命令
                _serialPort.Write(CMD_WRITE_MEMORY, 0, CMD_WRITE_MEMORY.Length);
                
                // 等待ACK
                await Task.Delay(10);
                byte[] ackResponse = new byte[1];
                _serialPort.Read(ackResponse, 0, 1);
                
                if (ackResponse[0] != ACK[0])
                {
                    _logService?.LogError("串口", "写入内存失败: 未收到写命令ACK");
                    return false;
                }
                
                // 发送地址
                byte[] addressBytes = new byte[5];
                addressBytes[0] = (byte)((address >> 24) & 0xFF);
                addressBytes[1] = (byte)((address >> 16) & 0xFF);
                addressBytes[2] = (byte)((address >> 8) & 0xFF);
                addressBytes[3] = (byte)(address & 0xFF);
                addressBytes[4] = (byte)(addressBytes[0] ^ addressBytes[1] ^ addressBytes[2] ^ addressBytes[3]); // 校验和
                
                _serialPort.Write(addressBytes, 0, 5);
                
                // 等待地址ACK
                await Task.Delay(10);
                byte[] addressAck = new byte[1];
                _serialPort.Read(addressAck, 0, 1);
                
                if (addressAck[0] != ACK[0])
                {
                    _logService?.LogError("串口", $"写入内存失败: 地址0x{address:X8}未确认");
                    return false;
                }
                
                // 发送数据
                // 1. 发送数据长度-1和校验和
                byte dataSize = (byte)(data.Length - 1);
                _serialPort.Write(new byte[] { dataSize }, 0, 1);
                
                // 2. 发送实际数据和校验和
                byte checksum = dataSize;
                _serialPort.Write(data, 0, data.Length);
                
                // 计算数据校验和
                foreach (byte b in data)
                {
                    checksum ^= b;
                }
                
                // 发送校验和
                _serialPort.Write(new byte[] { checksum }, 0, 1);
                
                // 等待数据写入完成ACK
                await Task.Delay(10);
                byte[] dataAck = new byte[1];
                _serialPort.Read(dataAck, 0, 1);
                
                if (dataAck[0] != ACK[0])
                {
                    _logService?.LogError("串口", "写入内存失败: 数据未确认");
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"写入地址0x{address:X8}处的数据块失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重启设备
        /// </summary>
        public async Task<bool> ResetDevice()
        {
            try
            {
                if (!IsConnected)
                {
                    _logService?.LogError("串口", "串口未连接，无法重启设备");
                    return false;
                }

                StatusChanged?.Invoke(this, "正在重启设备...");
                
                // 方法1: 使用GO命令跳转到应用程序起始地址
                // 发送GO命令
                _serialPort.Write(CMD_GO, 0, CMD_GO.Length);
                
                // 等待ACK
                await Task.Delay(50);
                byte[] ackResponse = new byte[1];
                if (_serialPort.BytesToRead > 0)
                {
                    _serialPort.Read(ackResponse, 0, 1);
                    if (ackResponse[0] == ACK[0])
                    {
                        // 发送起始地址 (应用程序起始地址通常是0x08000000)
                        byte[] addressBytes = new byte[5];
                        uint appStartAddress = 0x08000000;
                        addressBytes[0] = (byte)((appStartAddress >> 24) & 0xFF);
                        addressBytes[1] = (byte)((appStartAddress >> 16) & 0xFF);
                        addressBytes[2] = (byte)((appStartAddress >> 8) & 0xFF);
                        addressBytes[3] = (byte)(appStartAddress & 0xFF);
                        addressBytes[4] = (byte)(addressBytes[0] ^ addressBytes[1] ^ addressBytes[2] ^ addressBytes[3]); // 校验和
                        
                        _serialPort.Write(addressBytes, 0, 5);
                        
                        // 等待跳转确认
                        await Task.Delay(50);
                        if (_serialPort.BytesToRead > 0)
                        {
                            byte[] jumpAck = new byte[1];
                            _serialPort.Read(jumpAck, 0, 1);
                            if (jumpAck[0] == ACK[0])
                            {
                                _logService?.LogInfo("串口", "设备已重启");
                                StatusChanged?.Invoke(this, "设备已重启");
                                return true;
                            }
                        }
                    }
                }
                
                // 方法2: 如果GO命令失败，尝试使用DTR/RTS引脚复位设备
                _serialPort.DtrEnable = false; // BOOT0 = 0 (正常模式)
                await Task.Delay(50);
                
                _serialPort.RtsEnable = true;  // RESET = 0 (复位)
                await Task.Delay(50);
                
                _serialPort.RtsEnable = false; // RESET = 1 (释放复位)
                await Task.Delay(100);
                
                _logService?.LogInfo("串口", "设备已通过引脚重启");
                StatusChanged?.Invoke(this, "设备已重启");
                
                return true;
            }
            catch (Exception ex)
            {
                _logService?.LogError("串口", $"重启设备失败: {ex.Message}");
                StatusChanged?.Invoke(this, $"重启设备失败: {ex.Message}");
                return false;
            }
        }
    }
} 