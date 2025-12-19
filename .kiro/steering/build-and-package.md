# 构建和打包指南

## 编译项目

```powershell
# Debug编译
dotnet build

# Release编译
dotnet build -c Release
```

## 打包MSI安装程序（自包含，无需安装.NET）

### 步骤1：发布自包含版本

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=false /p:PublishTrimmed=false
```

### 步骤2：复制图标文件

```powershell
Copy-Item -Path "app.ico" -Destination "bin\Release\net8.0-windows\win-x64\publish\" -Force
```

### 步骤3：使用WiX打包MSI

```powershell
wix build -d SourceDir=bin\Release\net8.0-windows\win-x64\publish -ext WixToolset.UI.wixext installer\STM32Programmer_SelfContained.wxs -o installer\STM32Programmer_SelfContained.msi
```

### 一键打包命令

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=false /p:PublishTrimmed=false; Copy-Item -Path "app.ico" -Destination "bin\Release\net8.0-windows\win-x64\publish\" -Force; wix build -d SourceDir=bin\Release\net8.0-windows\win-x64\publish -ext WixToolset.UI.wixext installer\STM32Programmer_SelfContained.wxs -o installer\STM32Programmer_SelfContained.msi
```

## 输出文件

- MSI安装包：`installer\STM32Programmer_SelfContained.msi`
- 大小：约60MB（包含.NET运行时）

## 依赖工具

- WiX Toolset v4+（需要安装 `dotnet tool install --global wix`）
- WixToolset.UI.wixext 扩展
