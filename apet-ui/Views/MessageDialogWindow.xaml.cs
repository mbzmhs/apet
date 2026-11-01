using System.Windows;

namespace APet.UI.Views;

/// <summary>消息/确认对话框(无边框暗黑风格,与主窗一致):替代系统 MessageBox。
/// Show 为纯提示(仅确定),Confirm 带「取消」返回是否确认;owner 为空时居中显示(如启动期无主窗)。</summary>
public partial class MessageDialogWindow : Window
{
    private MessageDialogWindow(string title, string message, bool confirm)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        if (confirm)
            return;
        // 纯提示:取消按钮隐藏后 Esc/关闭即关闭(未置 DialogResult 视为取消)
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
                Close();
        };
    }

    /// <summary>信息提示(仅「确定」)。</summary>
    public static void Show(Window? owner, string title, string message)
    {
        var dlg = new MessageDialogWindow(title, message, confirm: false);
        if (owner is null)
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        else
            dlg.Owner = owner;
        dlg.ShowDialog();
    }

    /// <summary>确认对话框(「确定/取消」);返回用户是否点了确定。</summary>
    public static bool Confirm(Window? owner, string title, string message)
    {
        var dlg = new MessageDialogWindow(title, message, confirm: true);
        if (owner is null)
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        else
            dlg.Owner = owner;
        dlg.ShowDialog();
        return dlg.DialogResult == true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
