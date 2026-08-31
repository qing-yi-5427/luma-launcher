using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace LumaLauncher.Models;

public enum LauncherResultKind
{
    Application,
    File,
    Folder,
    Calculation,
    Web,
    Command
}

public sealed class LauncherResult : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isFavorite;

    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Target { get; init; }
    public required LauncherResultKind Kind { get; init; }
    public required double Score { get; init; }
    public string Arguments { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string CopyText { get; init; } = string.Empty;
    public string SourceLabel => Kind switch
    {
        LauncherResultKind.Application => "APP",
        LauncherResultKind.Folder => "FOLDER",
        LauncherResultKind.File => "FILE",
        LauncherResultKind.Calculation => "CALC",
        LauncherResultKind.Web => "WEB",
        _ => "CMD"
    };
    public string FallbackGlyph => Kind switch
    {
        LauncherResultKind.Application => "\uE71D",
        LauncherResultKind.Folder => "\uE8B7",
        LauncherResultKind.File => "\uE7C3",
        LauncherResultKind.Calculation => "\uE8EF",
        LauncherResultKind.Web => "\uE774",
        _ => "\uE756"
    };
    public bool CanRunAsAdministrator => Kind is LauncherResultKind.Application or LauncherResultKind.Command ||
                                           Path.GetExtension(Target).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    public bool IsFileSystemItem => Kind is LauncherResultKind.Application or LauncherResultKind.File or LauncherResultKind.Folder;
    public string FavoriteGlyph => IsFavorite ? "★" : string.Empty;

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
                return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteGlyph));
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
                return;
            _icon = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
