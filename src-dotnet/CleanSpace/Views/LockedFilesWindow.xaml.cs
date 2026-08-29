using System.Windows;
using CleanSpace.Services;

namespace CleanSpace.Views;

public enum LockedFileChoice { Skip, Retry, CloseApplicationsAndRetry, ForceCloseApplicationsAndRetry, ScheduleOnRestart }
public sealed record LockedFileRow(string Path, string ProcessText, string ErrorText);

public partial class LockedFilesWindow : Window
{
    public LockedFileChoice Choice { get; private set; } = LockedFileChoice.Skip;

    public LockedFilesWindow(Localizer text, IReadOnlyList<LockedFileRow> rows, bool canCloseApplications)
    {
        InitializeComponent();
        Title = text["locked.title"];
        Heading.Text = text["locked.heading"];
        Explanation.Text = text["locked.explanation"];
        PathColumn.Header = text["col.path"];
        ProcessColumn.Header = text["locked.process"];
        ErrorColumn.Header = text["locked.error"];
        CloseAppsButton.Content = text["locked.close_retry"];
        CloseAppsButton.IsEnabled = canCloseApplications;
        CloseAppsButton.ToolTip = canCloseApplications ? null : text["locked.close_disabled"];
        ForceCloseButton.Content = text["locked.force_retry"];
        ForceCloseButton.IsEnabled = canCloseApplications;
        ForceCloseButton.ToolTip = canCloseApplications ? text["locked.force_tip"] : text["locked.close_disabled"];
        RetryButton.Content = text["locked.retry"];
        ScheduleButton.Content = text["locked.schedule"];
        SkipButton.Content = text["locked.skip"];
        LockedGrid.DataContext = rows;
    }

    private void CloseApps_Click(object sender, RoutedEventArgs e) { Choice = LockedFileChoice.CloseApplicationsAndRetry; DialogResult = true; }
    private void ForceClose_Click(object sender, RoutedEventArgs e) { Choice = LockedFileChoice.ForceCloseApplicationsAndRetry; DialogResult = true; }
    private void Retry_Click(object sender, RoutedEventArgs e) { Choice = LockedFileChoice.Retry; DialogResult = true; }
    private void Schedule_Click(object sender, RoutedEventArgs e) { Choice = LockedFileChoice.ScheduleOnRestart; DialogResult = true; }
    private void Skip_Click(object sender, RoutedEventArgs e) { Choice = LockedFileChoice.Skip; DialogResult = true; }
}
