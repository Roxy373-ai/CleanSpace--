using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace CleanSpace.Models;

public enum RiskLevel
{
    Safe,
    Caution,
    Blocked
}

public sealed class FileRecord : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _mediaStatus = "media.unchecked";
    private ImageSource? _thumbnail;

    public required string FullPath { get; init; }
    public string Name => Path.GetFileName(FullPath);
    public string DirectoryPath => Path.GetDirectoryName(FullPath) ?? FullPath;
    public required long Size { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required string Drive { get; init; }
    public required RiskLevel Risk { get; init; }
    public required string ReasonKey { get; init; }
    public ulong FileId { get; init; }
    public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();
    public bool IsMedia => MediaExtensions.Contains(Extension);
    public string SizeText => SizeFormatter.Format(Size);

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public string MediaStatus
    {
        get => _mediaStatus;
        set { if (_mediaStatus != value) { _mediaStatus = value; OnPropertyChanged(); } }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { if (!ReferenceEquals(_thumbnail, value)) { _thumbnail = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v"
    };
}

public static class SizeFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
