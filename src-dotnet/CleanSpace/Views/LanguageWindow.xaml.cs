using System.Windows;
using CleanSpace.Services;

namespace CleanSpace.Views;

public partial class LanguageWindow : Window
{
    public LocaleCode SelectedLocale { get; private set; } = LocaleCode.ZhCn;
    public LanguageWindow() => InitializeComponent();
    private void Chinese_Click(object sender, RoutedEventArgs e) { SelectedLocale = LocaleCode.ZhCn; DialogResult = true; }
    private void Korean_Click(object sender, RoutedEventArgs e) { SelectedLocale = LocaleCode.KoKr; DialogResult = true; }
}
