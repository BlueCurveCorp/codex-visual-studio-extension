using CodexVsix.ViewModels;

using Microsoft.VisualStudio.Shell;

namespace CodexVsix;

internal static class CodexViewModelHost
{
    private static CodexToolWindowViewModel? _instance;

    public static CodexToolWindowViewModel GetOrCreate()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _instance ??= new CodexToolWindowViewModel();
    }
}
