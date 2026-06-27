using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using CodexVsix.Services;

using Microsoft.VisualStudio.Shell;

namespace CodexVsix;

public sealed class CodexSettingsToolWindow : ToolWindowPane
{
    public CodexSettingsToolWindow() : base(null)
    {
        this.Caption = new LocalizationService().CodexSettingsNav;

        try
        {
            this.Content = new CodexSettingsToolWindowControl();
        }
        catch (Exception ex)
        {
            _ = ActivityLog.TryLogError("CodexVsix", new LocalizationService().SettingsToolWindowInitializeLogMessage + Environment.NewLine + ex);
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
                    Text = localization.SettingsToolWindowErrorMessage
                        + Environment.NewLine
                        + Environment.NewLine
                        + ex.Message,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }
}
