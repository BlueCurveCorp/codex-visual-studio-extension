using Newtonsoft.Json.Linq;

namespace CodexVsix.Models;

public sealed class CodexApprovalOption
{
    public CodexApprovalOption(string key, JToken decision)
    {
        this.Key = key;
        this.Decision = decision;
    }

    public string Key { get; }

    public JToken Decision { get; }
}
