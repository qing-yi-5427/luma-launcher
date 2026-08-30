using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace LumaLauncher.Models;

public enum LauncherResultKind
{
    Application,
    File,
    Folder
}

public sealed class LauncherResult : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Target { get; init; }
    public required LauncherResultKind Kind { get; init; }
    public required double Score { get; init; }
    public string SourceLabel => Kind == LauncherResultKind.Application ? "APP" : Kind == LauncherResultKind.Folder ? "FOLDER" : "FILE";
    public string FallbackGlyph => Kind == LauncherResultKind.Application ? "\uE71D" : Kind == LauncherResultKind.Folder ? "\uE8B7" : "\uE7C3";
    public bool CanRunAsAdministrator => Kind == LauncherResultKind.Application || Path.GetExtension(Target).Equals(".exe", StringComparison.OrdinalIgnoreCase);

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
