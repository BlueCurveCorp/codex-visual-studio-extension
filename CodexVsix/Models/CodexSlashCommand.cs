namespace CodexVsix.Models;

public sealed class CodexSlashCommand
{
    public CodexSlashCommand(string commandText, string displayText, bool acceptsArguments = false)
    {
        this.CommandText = commandText;
        this.DisplayText = displayText;
        this.AcceptsArguments = acceptsArguments;
    }

    public string CommandText { get; }

    public string DisplayText { get; }

    public bool AcceptsArguments { get; }
}
