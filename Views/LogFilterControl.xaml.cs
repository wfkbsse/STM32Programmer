using STM32Programmer.Models;
using STM32Programmer.Utilities;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace STM32Programmer.Views
{
    /// <summary>
    /// LogFilterControl.xaml 的交互逻辑
    /// </summary>
    public partial class LogFilterControl : System.Windows.Controls.UserControl
    {
        private LogFilter? _filter;
        
        public LogFilterControl()
        {
            InitializeComponent();
            
            // 初始化日志级别下拉框
            LogLevelComboBox.ItemsSource = Enum.GetValues(typeof(LogLevel));
            
            // 默认选择Info级别
            LogLevelComboBox.SelectedItem = LogLevel.Info;
            
            // 初始化日志类别集合
            LogCategories = new ObservableCollection<string>();
        }
        
        // 日志源集合
        public ObservableCollection<LogEntry> LogSource
        {
            get { return (ObservableCollection<LogEntry>)GetValue(LogSourceProperty); }
            set { SetValue(LogSourceProperty, value); }
        }
        
        // 使用依赖属性系统
        public static readonly DependencyProperty LogSourceProperty =
            DependencyProperty.Register("LogSource", typeof(ObservableCollection<LogEntry>), 
                typeof(LogFilterControl), new PropertyMetadata(null, OnLogSourceChanged));
        
        // 过滤后的视图
        public System.ComponentModel.ICollectionView FilteredView
        {
            get { return (System.ComponentModel.ICollectionView)GetValue(FilteredViewProperty); }
            set { SetValue(FilteredViewProperty, value); }
        }
        
        // 过滤视图依赖属性
        public static readonly DependencyProperty FilteredViewProperty =
            DependencyProperty.Register("FilteredView", typeof(System.ComponentModel.ICollectionView), 
                typeof(LogFilterControl), new PropertyMetadata(null));
        
        // 日志类别集合
        public ObservableCollection<string> LogCategories
        {
            get { return (ObservableCollection<string>)GetValue(LogCategoriesProperty); }
            set { SetValue(LogCategoriesProperty, value); }
        }
        
        // 日志类别依赖属性
        public static readonly DependencyProperty LogCategoriesProperty =
            DependencyProperty.Register("LogCategories", typeof(ObservableCollection<string>), 
                typeof(LogFilterControl), new PropertyMetadata(new ObservableCollection<string>()));
        
        // 更新日志类别列表
        public void UpdateCategories()
        {
            if (LogSource == null) return;
            
            // 清除现有类别
            LogCategories.Clear();
            
            // 添加空选项
            LogCategories.Add(string.Empty);
            
            // 收集所有唯一的类别
            var categories = LogSource
                .Select(log => log.Category)
                .Where(cat => !string.IsNullOrEmpty(cat))
                .Distinct()
                .OrderBy(cat => cat);
            
            foreach (var category in categories)
            {
                LogCategories.Add(category);
            }
            
            // 更新下拉框
            CategoryComboBox.ItemsSource = LogCategories;
        }
        
        private static void OnLogSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (LogFilterControl)d;
            if (e.NewValue is ObservableCollection<LogEntry> source)
            {
                // 创建过滤器
                control._filter = new LogFilter(source);
                control.DataContext = control._filter;
                
                // 设置过滤后的视图
                control.FilteredView = control._filter.FilteredView;
                
                // 监听源集合变化以更新类别
                source.CollectionChanged += (s, args) =>
                {
                    control.UpdateCategories();
                };
                
                // 初始更新类别
                control.UpdateCategories();
            }
        }
    }
}
