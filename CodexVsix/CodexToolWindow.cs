using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using CodexVsix.Services;

using Microsoft.VisualStudio.Shell;

namespace CodexVsix;

public sealed class CodexToolWindow : ToolWindowPane
{
    public CodexToolWindow() : base(null)
    {
        this.Caption = "Codex";

        try
        {
            this.Content = new CodexToolWindowControl();
        }
        catch (Exception ex)
        {
            _ = ActivityLog.TryLogError("CodexVsix", new LocalizationService().ToolWindowInitializeLogMessage + Environment.NewLine + ex);
            this.Content = CreateErrorView(ex);
        }
    }

    private static FrameworkElement CreateErrorView(Exception ex)
    {
        LocalizationService localization = new();
        return new Border
        {
            Padding = new Thickness(16),
            Background = Brushes.Transparent,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = localization.ToolWindowErrorMessage
                        + Environment.NewLine
                        + Environment.NewLine
                        + ex.Message,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }
}
