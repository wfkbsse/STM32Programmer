# MaterialDesign最佳实践

## 性能优化

### 1. 启用虚拟化

对于包含大量项目的集合控件（如ListBox、ListView等），启用虚拟化可以显著提升性能：

```xml
<ListBox VirtualizingStackPanel.IsVirtualizing="True"
         VirtualizingStackPanel.VirtualizationMode="Recycling">
    <!-- 列表内容 -->
</ListBox>
```

### 2. 使用BitmapCache缓存

对于不经常变化的UI元素，特别是具有复杂视觉效果的元素（如阴影），可以使用`CacheMode`提高渲染性能：

```xml
<Button>
    <Button.CacheMode>
        <BitmapCache EnableClearType="True"
                     RenderAtScale="1.0"
                     SnapsToDevicePixels="True"/>
    </Button.CacheMode>
</Button>
```

全局定义缓存资源：

```xml
<BitmapCache x:Key="UICache" 
             EnableClearType="True" 
             RenderAtScale="1.0" 
             SnapsToDevicePixels="True"/>
```

然后在元素上引用：

```xml
<Button CacheMode="{StaticResource UICache}"/>
```

### 3. 优化阴影渲染

MaterialDesign中的阴影可能会影响性能，可以通过以下方式优化：

- 降低阴影深度：
  ```xml
  <materialDesign:Card materialDesign:ShadowAssist.ShadowDepth="Depth1">
      <!-- 卡片内容 -->
  </materialDesign:Card>
  ```

- 对于不需要阴影的元素，可以完全禁用：
  ```xml
  <materialDesign:Card materialDesign:ShadowAssist.ShadowDepth="Depth0">
      <!-- 卡片内容 -->
  </materialDesign:Card>
  ```

## 线程安全最佳实践

### 1. UI更新应在主线程进行

始终在UI线程上更新UI元素：

```csharp
// 使用Dispatcher确保在UI线程上执行
Dispatcher.BeginInvoke(new Action(() => {
    // 更新UI元素
    StatusText.Text = "已完成";
}));
```

### 2. 使用锁保护共享资源

当多个线程访问同一资源时，使用锁保护：

```csharp
private readonly object _lockObject = new object();

public void UpdateResource()
{
    lock (_lockObject)
    {
        // 对共享资源的操作
    }
}
```

### 3. 高效使用异步编程

使用async/await简化异步操作：

```csharp
private async Task LoadDataAsync()
{
    try
    {
        // 显示加载状态
        StatusBar.Text = "正在加载数据...";
        
        // 执行耗时操作
        var result = await Task.Run(() => {
            // 耗时操作逻辑
            return new DataResult();
        });
        
        // 更新UI（自动在UI线程执行）
        DataGrid.ItemsSource = result.Items;
        StatusBar.Text = "数据加载完成";
    }
    catch (Exception ex)
    {
        StatusBar.Text = "加载失败: " + ex.Message;
    }
}
```

## 常见问题解决方案

### 1. 资源字典加载问题

如果遇到资源字典加载异常，可以尝试使用BundledTheme：

```xml
<ResourceDictionary.MergedDictionaries>
    <materialDesign:BundledTheme BaseTheme="Light" PrimaryColor="Blue" SecondaryColor="LightBlue" />
    <ResourceDictionary Source="pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Defaults.xaml" />
</ResourceDictionary.MergedDictionaries>
```

或者包含所有必要的颜色资源字典：

```xml
<ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="pack://application:,,,/MaterialDesignColors;component/Themes/Recommended/Primary/MaterialDesignColor.Blue.xaml" />
    <ResourceDictionary Source="pack://application:,,,/MaterialDesignColors;component/Themes/Recommended/Accent/MaterialDesignColor.LightBlue.xaml" />
    <ResourceDictionary Source="pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml" />
    <ResourceDictionary Source="pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Defaults.xaml" />
</ResourceDictionary.MergedDictionaries>
```

### 2. DialogHost问题

使用DialogHost时的常见问题解决方案：

```csharp
// 正确显示对话框
await DialogHost.Show(dialogContent, "RootDialog");

// 关闭对话框
DialogHost.Close("RootDialog");
```

确保在XAML中正确设置Identifier：

```xml
<materialDesign:DialogHost Identifier="RootDialog">
    <!-- 内容 -->
</materialDesign:DialogHost>
```

## 布局优化

### 1. 减少布局复杂性

复杂的布局嵌套会降低性能，尽量扁平化布局：

```xml
<!-- 优化前：深度嵌套 -->
<StackPanel>
    <StackPanel>
        <StackPanel>
            <TextBlock/>
        </StackPanel>
    </StackPanel>
</StackPanel>

<!-- 优化后：扁平化布局 -->
<StackPanel>
    <TextBlock/>
</StackPanel>
```

### 2. 使用Grid代替复杂嵌套

对于复杂布局，使用Grid通常比嵌套多个Panel更高效：

```xml
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="Auto"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
    </Grid.RowDefinitions>
    
    <TextBlock Grid.Row="0" Grid.Column="0"/>
    <Button Grid.Row="0" Grid.Column="1"/>
    <DataGrid Grid.Row="1" Grid.ColumnSpan="2"/>
</Grid>
```

## 引用

- [MaterialDesignInXaml官方文档](http://materialdesigninxaml.net/)
- [WPF性能优化指南](https://docs.microsoft.com/zh-cn/dotnet/desktop/wpf/advanced/optimizing-wpf-application-performance) 