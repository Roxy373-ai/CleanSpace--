using System.Windows.Media;

namespace CleanSpace.Models;

public sealed class CleanupItem
{
    public required FileRecord File { get; init; }
    public required string SourceKey { get; init; }
    public bool IsSelected { get => File.IsSelected; set => File.IsSelected = value; }
    public string FullPath => File.FullPath;
    public string SizeText => File.SizeText;
    public RiskLevel Risk => File.Risk;
    public string ReasonKey => File.ReasonKey;
}

public sealed class DuplicateGroup
{
    public required string Hash { get; init; }
    public required IReadOnlyList<FileRecord> Files { get; init; }
    public long ReclaimableBytes => Files.Count <= 1 ? 0 : Files.Skip(1).Sum(x => x.Size);
    public string Summary => $"{Files.Count} files · {SizeFormatter.Format(ReclaimableBytes)}";
}

public sealed class DuplicateRow
{
    public required int GroupNumber { get; init; }
    public required FileRecord File { get; init; }
    public bool KeepRecommended { get; init; }
    public bool IsSelected { get; set; }
    public string GroupText => $"#{GroupNumber}";
    public string Name => File.Name;
    public string FullPath => File.FullPath;
    public string SizeText => File.SizeText;
}

public sealed class HistoryItem
{
    public required DateTime Time { get; init; }
    public required string Path { get; init; }
    public required string Result { get; init; }
    public required long Size { get; init; }
    public string SizeText => SizeFormatter.Format(Size);
}

public sealed class InstalledApp
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string UninstallCommand { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public string IconPath { get; init; } = "";
    public long EstimatedSize { get; init; }

    public string SizeText => EstimatedSize > 0 ? SizeFormatter.Format(EstimatedSize) : "N/A";
}
public sealed class AppResidual
{
    private bool _isSelected;
    public required string Location { get; init; }
    public required string SourceKey { get; init; }
    public required string RiskKey { get; init; }
    public required bool CanRecycle { get; init; }
    public bool IsSelected
    {
        get => _isSelected;
        set => _isSelected = CanRecycle && value;
    }
}
