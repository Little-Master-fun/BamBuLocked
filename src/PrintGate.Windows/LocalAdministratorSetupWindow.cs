using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PrintGate.Core;

namespace PrintGate.Windows;

public sealed class LocalAdministratorSetupWindow : Window
{
    public LocalAdministratorSetupWindow()
    {
        Title = "设置本地管理员"; Width = 540; Height = 550; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PrintGate;component/AdminTheme.xaml", UriKind.Relative) });
        var panel = new StackPanel { Margin = new Thickness(30) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "本地管理员账号", FontSize = 25, Margin = new Thickness(0,0,0,12) });
        panel.Children.Add(new TextBlock { Text = "此账号用于登录记录管理页面，与校园账号和 Windows 账号独立。保存后替换原本地管理员，重新登录打印账户后生效。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,16) });
        panel.Children.Add(new TextBlock { Text = "用户名（3–64 个字符，不含空格）" });
        var username = new TextBox { MaxLength = 64, Margin = new Thickness(0,6,0,14) }; panel.Children.Add(username);
        panel.Children.Add(new TextBlock { Text = "新密码（至少 10 个字符）" });
        var password = new PasswordBox { MaxLength = 256, Padding = new Thickness(10), Margin = new Thickness(0,6,0,14) }; panel.Children.Add(password);
        panel.Children.Add(new TextBlock { Text = "再次输入密码" });
        var confirm = new PasswordBox { MaxLength = 256, Padding = new Thickness(10), Margin = new Thickness(0,6,0,20) }; panel.Children.Add(confirm);
        var save = new Button { Content = "保存管理员账号", IsDefault = true }; panel.Children.Add(save);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,14,0,0) }; panel.Children.Add(status);
        save.Click += async (_,_) =>
        {
            if (password.Password != confirm.Password) { status.Text = "两次输入的密码不一致。"; return; }
            var name = username.Text; var secret = password.Password;
            save.IsEnabled = username.IsEnabled = password.IsEnabled = confirm.IsEnabled = false;
            try
            {
                var credential = await Task.Run(() => LocalAdministratorCredential.Create(name, secret));
                LocalAdministratorSettings.Save(credential);
                password.Clear(); confirm.Clear();
                status.Text = "已保存。重新登录打印账户后，选择“本地管理员登录”并输入此账号。";
            }
            catch (ArgumentException error) { status.Text = error.Message; }
            catch { status.Text = "保存失败，请确认使用 Windows 管理员权限运行，且程序目录可写。"; }
            finally { secret = string.Empty; save.IsEnabled = username.IsEnabled = password.IsEnabled = confirm.IsEnabled = true; }
        };
    }
}
