using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

using CodexVsix.Services;
using CodexVsix.ViewModels;

using Microsoft.VisualStudio.Shell;

namespace CodexVsix;

public partial class CodexSettingsToolWindowControl : UserControl
{
    private readonly CodexToolWindowViewModel _viewModel;

    public CodexSettingsToolWindowControl()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            this.InitializeComponent();
        }
        catch (System.Exception ex)
        {
            _ = ActivityLog.TryLogError("CodexVsix", new LocalizationService().SettingsToolWindowXamlLoadLogMessage + System.Environment.NewLine + ex);
            throw;
        }

        try
        {
            this._viewModel = CodexViewModelHost.GetOrCreate();
        }
        catch (System.Exception ex)
        {
            _ = ActivityLog.TryLogError("CodexVsix", new LocalizationService().SettingsToolWindowViewModelCreateLogMessage + System.Environment.NewLine + ex);
            throw;
        }

        this.DataContext = this._viewModel;
        this._viewModel.PropertyChanged += this.OnViewModelPropertyChanged;
        Unloaded += this.OnUnloaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        this.RefreshSectionContent();
        this.UpdateSectionVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName)
            || string.Equals(e.PropertyName, nameof(CodexToolWindowViewModel.SelectedSettingsSection), System.StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(CodexToolWindowViewModel.Localization), System.StringComparison.Ordinal))
        {
            this.RefreshSectionContent();
            this.UpdateSectionVisibility();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        this._viewModel.PropertyChanged -= this.OnViewModelPropertyChanged;
        Unloaded -= this.OnUnloaded;
    }

    private void RefreshSectionContent()
    {
        if (this._viewModel.IsMcpSectionSelected || this._viewModel.IsSkillsSectionSelected)
        {
            if (this._viewModel.RefreshIntegrationsCommand.CanExecute(null))
            {
                this._viewModel.RefreshIntegrationsCommand.Execute(null);
            }
        }
        else if (this._viewModel.RefreshCodexStatusCommand.CanExecute(null))
        {
            this._viewModel.RefreshCodexStatusCommand.Execute(null);
        }
    }

    private void UpdateSectionVisibility()
    {
        this.SettingsPlaceholderText.Visibility = this._viewModel.IsSettingsDetailPanelVisible ? Visibility.Collapsed : Visibility.Visible;
        this.AccountSectionPanel.Visibility = this._viewModel.IsAccountSectionSelected ? Visibility.Visible : Visibility.Collapsed;
        this.CodexSectionPanel.Visibility = this._viewModel.IsCodexSectionSelected ? Visibility.Visible : Visibility.Collapsed;
        this.McpSectionPanel.Visibility = this._viewModel.IsMcpSectionSelected ? Visibility.Visible : Visibility.Collapsed;
        this.SkillsSectionPanel.Visibility = this._viewModel.IsSkillsSectionSelected ? Visibility.Visible : Visibility.Collapsed;
    }
}
