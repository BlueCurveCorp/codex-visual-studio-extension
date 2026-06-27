using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;

using CodexVsix.Services;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodexVsix;

internal sealed class ShowCodexToolWindowCommand
{
    private readonly AsyncPackage _package;

    private ShowCodexToolWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        this._package = package;
        CommandID commandId = new(new Guid(GuidList.CommandSetString), PackageIds.ShowToolWindowCommand);
        MenuCommand menuCommand = new((_, _) => this._package.JoinableTaskFactory.RunAsync(this.ExecuteAsync).FileAndForget("CodexVsix/ShowToolWindow"), commandId);
        commandService.AddCommand(menuCommand);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        OleMenuCommandService? commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        if (commandService is not null)
        {
            _ = new ShowCodexToolWindowCommand(package, commandService);
        }
    }

    private async Task ExecuteAsync()
    {
        try
        {
            ToolWindowPane? existingWindow = await this._package.FindToolWindowAsync(typeof(CodexToolWindow), 0, false, this._package.DisposalToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(this._package.DisposalToken);
            if (existingWindow?.Frame is IVsWindowFrame existingFrame && IsFrameVisible(existingFrame))
            {
                _ = existingFrame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                return;
            }

            ToolWindowPane window = await this._package.FindToolWindowAsync(typeof(CodexToolWindow), 0, true, this._package.DisposalToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(this._package.DisposalToken);
            if (window?.Frame is not IVsWindowFrame frame)
            {
                throw new NotSupportedException(new LocalizationService().OpenWindowFailedMessage);
            }

            _ = frame.Show();
        }
        catch (Exception ex)
        {
            LocalizationService localization = new();
            _ = ActivityLog.TryLogError("CodexVsix", localization.ToolWindowOpenLogMessage + Environment.NewLine + ex);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _ = VsShellUtilities.ShowMessageBox(
                this._package,
                localization.OpenWindowFailedMessage + Environment.NewLine + Environment.NewLine + ex.Message,
                "Codex",
                OLEMSGICON.OLEMSGICON_CRITICAL,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    private static bool IsFrameVisible(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return frame.IsVisible() == 0;
    }
}
