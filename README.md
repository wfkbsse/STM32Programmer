# STM32Programmer

一款基于 WPF 的 STM32 固件烧写工具，支持智能自动烧录功能。

![.NET 8](https://img.shields.io/badge/.NET-8.0-blue)
![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey)
![License](https://img.shields.io/badge/License-MIT-green)

## 功能特点

- 🔥 **固件烧写** - 支持 BOOT 和 APP 固件分别或一键烧写
- 🤖 **智能自动烧录** - 自动检测芯片连接，批量烧录无需手动操作
- 📁 **固件管理** - 自动搜索固件文件，支持多固件切换
- 📊 **实时进度** - 步骤指示器 + 进度条，清晰显示烧录状态
- 📝 **操作日志** - 详细记录每次操作，支持日志过滤
- 🎨 **现代 UI** - Material Design 风格，美观易用

## 截图

（添加你的截图）

## 系统要求

- Windows 10/11 (x64)
- .NET 8.0 运行时（框架依赖版本）或无需运行时（自包含版本）
- ST-LINK 驱动程序

## 安装

### 方式一：MSI 安装包（推荐）
下载 `STM32Programmer_v1.0_Setup.msi`，双击安装。

### 方式二：便携版
下载 `STM32Programmer_v1.0_Portable.zip`，解压后运行 `STM32Programmer.exe`。

## 编译

```bash
# 克隆仓库
git clone https://github.com/你的用户名/STM32Programmer.git
cd STM32Programmer

# 还原依赖
dotnet restore

# 编译
dotnet build -c Release

# 发布（框架依赖）
dotnet publish -c Release -o publish
```

## 项目结构

```
STM32Programmer/
├── Models/          # 数据模型
├── Services/        # 业务服务
├── Views/           # 视图组件
├── Converters/      # 值转换器
├── installer/       # 安装包配置
├── MainWindow.xaml  # 主窗口
└── SmartBurnWindow.xaml  # 智能烧录窗口
```

## 技术栈

- .NET 8.0 / WPF
- Material Design In XAML Toolkit
- WiX Toolset v4（安装包）

## 许可证

MIT License

## 贡献

欢迎提交 Issue 和 Pull Request！
