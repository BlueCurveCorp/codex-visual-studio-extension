using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodexVsix.Models;

public sealed class CodexMcpServerSummary : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = string.Empty;

    public string AuthStatus { get; set; } = string.Empty;

    public string ToolsLabel { get; set; } = string.Empty;

    public bool IsShortcutSelected
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            this.OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public override string ToString() => this.Name;
}
