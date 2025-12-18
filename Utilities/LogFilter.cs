using STM32Programmer.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace STM32Programmer.Utilities
{
    /// <summary>
    /// 日志过滤器，用于在UI中筛选和展示日志
    /// </summary>
    public class LogFilter : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        
        private readonly ObservableCollection<LogEntry> _sourceCollection;
        private readonly System.ComponentModel.ICollectionView _filteredView;
        
        // 过滤设置
        private LogLevel _minLevel = LogLevel.Debug;
        private string _categoryFilter = string.Empty;
        private string _messageFilter = string.Empty;
        
        public System.ComponentModel.ICollectionView FilteredView => _filteredView;
        
        public LogFilter(ObservableCollection<LogEntry> source)
        {
            _sourceCollection = source;
            _filteredView = CollectionViewSource.GetDefaultView(source);
            _filteredView.Filter = ApplyFilter;
        }
        
        public LogLevel MinLevel
        {
            get => _minLevel;
            set
            {
                if (_minLevel != value)
                {
                    _minLevel = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MinLevel)));
                    RefreshFilter();
                }
            }
        }
        
        public string CategoryFilter
        {
            get => _categoryFilter;
            set
            {
                if (_categoryFilter != value)
                {
                    _categoryFilter = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CategoryFilter)));
                    RefreshFilter();
                }
            }
        }
        
        public string MessageFilter
        {
            get => _messageFilter;
            set
            {
                if (_messageFilter != value)
                {
                    _messageFilter = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MessageFilter)));
                    RefreshFilter();
                }
            }
        }
        
        // 刷新过滤器
        private void RefreshFilter()
        {
            _filteredView.Refresh();
        }
        
        // 应用过滤逻辑
        private bool ApplyFilter(object obj)
        {
            if (obj is not LogEntry entry) return false;
            
            // 级别过滤
            if (entry.Level < _minLevel) return false;
            
            // 类别过滤
            if (!string.IsNullOrEmpty(_categoryFilter) && 
                !entry.Category.Contains(_categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            
            // 消息过滤
            if (!string.IsNullOrEmpty(_messageFilter) && 
                !entry.Message.Contains(_messageFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            
            return true;
        }
    }
} 