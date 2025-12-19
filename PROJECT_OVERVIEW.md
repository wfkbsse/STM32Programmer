# STM32Programmer 项目总体介绍

## 项目概述

STM32Programmer 是一款基于 WPF (.NET 8.0) 开发的 STM32 固件烧写工具，采用 Material Design 风格设计，提供直观易用的图形界面。

## 核心功能模块

### 1. 固件烧写
- 支持 BOOT 和 APP 固件分别烧写或一键烧写
- 支持 ST-LINK 和串口两种烧写方式
- 实时显示烧写进度和状态

### 2. 智能自动烧录 (SmartBurn)
- 自动检测芯片连接状态
- 批量烧录无需手动操作
- 支持连续烧录模式

### 3. 固件管理
- 自动搜索指定目录下的固件文件
- 支持多固件版本切换
- 固件信息预览

### 4. 串口通信
- 串口日志查看
- 串口数据发送
- 支持多种波特率配置

### 5. 设备管理
- ST-LINK 设备检测与连接
- 串口设备扫描与连接
- 设备状态实时监控

## 项目架构

```
STM32Programmer/
├── Models/              # 数据模型层
│   ├── FirmwareInfo.cs  # 固件信息模型
│   └── DeviceInfo.cs    # 设备信息模型
├── Services/            # 业务服务层
│   ├── STLinkService.cs     # ST-LINK 烧写服务
│   ├── SerialBurnService.cs # 串口烧写服务
│   └── FirmwareService.cs   # 固件管理服务
├── Views/               # 视图组件
├── ViewModels/          # 视图模型 (MVVM)
├── Converters/          # WPF 值转换器
├── Utilities/           # 工具类
├── Resources/           # 资源文件
├── installer/           # WiX 安装包配置
├── MainWindow.xaml      # 主窗口界面
├── MainWindow.xaml.cs   # 主窗口逻辑
├── SmartBurnWindow.xaml # 智能烧录窗口
└── SmartBurnWindow.xaml.cs
```

## 技术栈

| 技术 | 说明 |
|------|------|
| .NET 8.0 | 运行时框架 |
| WPF | Windows 桌面 UI 框架 |
| Material Design In XAML | UI 组件库 |
| System.IO.Ports | 串口通信 |
| WiX Toolset v4 | MSI 安装包制作 |

## 编译与打包

### 编译命令
```bash
# 标准编译
dotnet build -c Release

# 指定平台编译 (用于 MSI 打包，依赖框架模式)
dotnet build -c Release -r win-x64 --no-self-contained
```

### MSI 打包命令
```bash
wix build installer/STM32Programmer.wxs -d SourceDir=. -d PublishDir=bin/Release/net8.0-windows/win-x64 -ext WixToolset.UI.wixext -o STM32Programmer_v1.0_Setup.msi
```

## 系统要求

- Windows 10/11 (x64)
- .NET 8.0 运行时
- ST-LINK 驱动程序 (使用 ST-LINK 烧写时)

## 许可证

MIT License
