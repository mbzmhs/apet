using System.Windows;

namespace APet.UI.Views;

/// <summary>GSV 参考音频预热的模态进度窗:预热期间以 ShowDialog 阻塞对话操作。</summary>
public partial class WarmupWindow : Window
{
    public WarmupWindow()
    {
        InitializeComponent();
    }
}
