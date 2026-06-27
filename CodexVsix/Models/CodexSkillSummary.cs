using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodexVsix.Models;

public sealed class CodexSkillSummary : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string ShortDescription { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsEnabled
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

    public bool IsSystem { get; set; }

    public string ScopeLabel { get; set; } = string.Empty;

    public string DisplayTitle => string.IsNullOrWhiteSpace(this.DisplayName) ? this.Name : this.DisplayName;

    public string Summary => !string.IsNullOrWhiteSpace(this.ShortDescription) ? this.ShortDescription : this.Description;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
