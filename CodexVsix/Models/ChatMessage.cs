using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CodexVsix.Models;

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _text;
    private string _displayText;
    private string? _title;
    private string? _detail;
    private bool _hasCustomDisplayText;
    private bool _renderMarkdown;

    public ChatMessage(
        bool isUser,
        string text,
        bool isEvent = false,
        string? title = null,
        string? detail = null,
        bool? supportsMarkdownText = null,
        bool supportsMarkdownDetail = false)
    {
        this.IsUser = isUser;
        this.IsEvent = isEvent;
        this.SupportsMarkdownText = supportsMarkdownText ?? (!isUser && !isEvent);
        this.SupportsMarkdownDetail = supportsMarkdownDetail;
        this._title = title;
        this._detail = detail;
        this._text = text;
        this._displayText = text;
        this._renderMarkdown = this.SupportsMarkdownText || this.SupportsMarkdownDetail;
        this.PromptSkillNames.CollectionChanged += this.HandlePromptSkillNamesChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsUser { get; }

    public bool IsEvent { get; }

    public bool SupportsMarkdownText { get; }

    public bool SupportsMarkdownDetail { get; }

    public string? Title
    {
        get => this._title;
        set
        {
            this._title = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasTitle));
            this.OnPropertyChanged(nameof(this.HasHeader));
        }
    }

    public string? Detail
    {
        get => this._detail;
        set
        {
            this._detail = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasDetail));
            this.OnPropertyChanged(nameof(this.CanToggleMarkdownView));
            this.OnPropertyChanged(nameof(this.HasHeader));
            this.OnPropertyChanged(nameof(this.ShowMarkdownDetail));
            this.OnPropertyChanged(nameof(this.ShowPlainDetail));
        }
    }

    public bool HasTitle => !string.IsNullOrWhiteSpace(this.Title);

    public bool HasDetail => !string.IsNullOrWhiteSpace(this.Detail);

    public bool HasHeader => this.HasTitle || this.CanToggleMarkdownView;

    public ObservableCollection<string> PromptSkillNames { get; } = [];

    public bool HasPromptSkillNames => this.PromptSkillNames.Count > 0;

    public string DisplayText
    {
        get => this._displayText;
        private set
        {
            this._displayText = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasDisplayText));
        }
    }

    public bool HasDisplayText => !string.IsNullOrWhiteSpace(this.DisplayText);

    public bool RenderMarkdown
    {
        get => this._renderMarkdown;
        set
        {
            if (this._renderMarkdown == value)
            {
                return;
            }

            this._renderMarkdown = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.IsTextMode));
            this.OnPropertyChanged(nameof(this.IsRenderedMode));
            this.OnPropertyChanged(nameof(this.ShowMarkdownText));
            this.OnPropertyChanged(nameof(this.ShowPlainDetail));
            this.OnPropertyChanged(nameof(this.ShowMarkdownDetail));
        }
    }

    public bool IsTextMode => !this.RenderMarkdown;

    public bool IsRenderedMode => this.RenderMarkdown;

    public bool CanToggleMarkdownView =>
        (this.SupportsMarkdownText && !string.IsNullOrWhiteSpace(this.Text))
        || (this.SupportsMarkdownDetail && this.HasDetail);

    public bool ShowMarkdownText => this.SupportsMarkdownText && this.RenderMarkdown;

    public bool ShowMarkdownDetail => this.SupportsMarkdownDetail && this.HasDetail && this.RenderMarkdown;

    public bool ShowPlainDetail => this.HasDetail && !this.ShowMarkdownDetail;

    public string Text
    {
        get => this._text;
        set
        {
            this._text = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.CanToggleMarkdownView));
            this.OnPropertyChanged(nameof(this.HasHeader));
            this.OnPropertyChanged(nameof(this.ShowMarkdownText));
            if (!this._hasCustomDisplayText)
            {
                this.DisplayText = value;
            }
        }
    }

    public void ApplyPromptSkillDisplay(System.Collections.Generic.IEnumerable<string> skillNames, string? displayText)
    {
        this._hasCustomDisplayText = false;
        this.PromptSkillNames.Clear();

        foreach (string? skillName in skillNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            this.PromptSkillNames.Add(skillName);
        }

        string normalizedDisplayText = displayText ?? string.Empty;
        this._hasCustomDisplayText = this.PromptSkillNames.Count > 0 || !string.Equals(normalizedDisplayText, this._text, System.StringComparison.Ordinal);
        this.DisplayText = this._hasCustomDisplayText ? normalizedDisplayText : this._text;
    }

    private void HandlePromptSkillNamesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.OnPropertyChanged(nameof(this.HasPromptSkillNames));
    }

    public void ToggleMarkdownView()
    {
        if (!this.CanToggleMarkdownView)
        {
            return;
        }

        this.RenderMarkdown = !this.RenderMarkdown;
    }

    public void SetMarkdownView(bool renderMarkdown)
    {
        if (!this.CanToggleMarkdownView)
        {
            return;
        }

        this.RenderMarkdown = renderMarkdown;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
