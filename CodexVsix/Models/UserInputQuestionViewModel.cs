using System.Collections.ObjectModel;

namespace CodexVsix.Models;

public sealed class UserInputQuestionViewModel
{
    public string Header { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public bool IsSecret { get; set; }

    public bool AcceptsText { get; set; }

    public ObservableCollection<SelectionOption> Options { get; } = [];

    public string? SelectedOptionValue { get; set; }

    public string AnswerText { get; set; } = string.Empty;

    public string? ResolvedAnswer
    {
        get
        {
            if (this.AcceptsText)
            {
                return string.IsNullOrWhiteSpace(this.AnswerText) ? null : this.AnswerText.Trim();
            }

            return string.IsNullOrWhiteSpace(this.SelectedOptionValue) ? null : this.SelectedOptionValue;
        }
    }
}
