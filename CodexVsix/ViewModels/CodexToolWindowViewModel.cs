using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using CodexVsix.Models;
using CodexVsix.Services;
using CodexVsix.UI;

using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;

using Newtonsoft.Json.Linq;

namespace CodexVsix.ViewModels;

public sealed class CodexToolWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private const double DefaultContextTokenBudget = 128000d;
    private const int AssistantOutputFlushDelayMilliseconds = 75;
    private const string SettingsSectionAccount = "account";
    private const string SettingsSectionCodexMenu = "codex-menu";
    private const string SettingsSectionCodex = "codex";
    private const string SettingsSectionIde = "ide";
    private const string SettingsSectionMcp = "mcp";
    private const string SettingsSectionSkills = "skills";
    private const string SettingsSectionLanguage = "language";
    private static readonly CodexSlashCommand[] AvailableSlashCommands =
    [
        new("/new", "/new"),
        new("/clear", "/clear"),
        new("/resume", "/resume [thread-id]", acceptsArguments: true),
        new("/fork", "/fork"),
        new("/compact", "/compact"),
        new("/review", "/review [instructions]", acceptsArguments: true),
        new("/model", "/model <model-id>", acceptsArguments: true),
        new("/fast", "/fast"),
        new("/plan", "/plan [prompt]", acceptsArguments: true),
        new("/permissions", "/permissions [read-only|workspace-write|danger-full-access]", acceptsArguments: true),
        new("/ide", "/ide"),
        new("/status", "/status"),
        new("/skills", "/skills"),
        new("/mcp", "/mcp"),
        new("/apps", "/apps"),
        new("/rename", "/rename <name>", acceptsArguments: true),
        new("/archive", "/archive"),
        new("/delete", "/delete")
    ];
    private readonly ExtensionSettingsStore _settingsStore = new();
    private readonly CodexProcessService _codexProcessService = new();
    private readonly CodexEnvironmentService _codexEnvironmentService = new();
    private readonly SolutionContextService _solutionContextService = new();
    private readonly object _assistantOutputSync = new();
    private readonly StringBuilder _assistantOutputBuffer = new();

    private CancellationTokenSource? _cts;
    private ChatMessage? _currentAssistantMessage;
    private ChatMessage? _currentPlanMessage;
    private ChatMessage? _currentTransientStatusMessage;
    private bool _assistantOutputFlushScheduled;
    private long _assistantOutputBufferVersion;
    private bool _hideRecentTasksPreview;
    private bool _pinRecentTasksPreview;
    private string _prompt = string.Empty;
    private string _promptEditorText = string.Empty;
    private string _output = string.Empty;
    private string _selectedModel = string.Empty;
    private string _selectedReasoningEffort = string.Empty;
    private string _selectedVerbosity = string.Empty;
    private const double ContextWindowBaselineTokens = 12000d;
    private double _contextTokenBudget = DefaultContextTokenBudget;
    private double _lastKnownContextTokensInWindow;
    private double _lastKnownRemainingTokens = DefaultContextTokenBudget - ContextWindowBaselineTokens;
    private TaskCompletionSource<JToken?>? _approvalDecisionTcs;
    private TaskCompletionSource<JObject?>? _userInputDecisionTcs;
    private bool _suppressThreadSelection;
    private bool _hasLoadedStartupSurfaces;
    private bool _isToolWindowStartupRefreshInProgress;
    private long _conversationStateVersion;

    public CodexToolWindowViewModel()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.Settings = this._settingsStore.Load();
        this.EnsureSettingsCollectionsInitialized();
        this.Localization = new LocalizationService(this.Settings.LanguageOverride);

        if (string.IsNullOrWhiteSpace(this.Settings.DefaultModel))
        {
            this.Settings.DefaultModel = "gpt-5.4";
        }

        if (string.IsNullOrWhiteSpace(this.Settings.ReasoningEffort))
        {
            this.Settings.ReasoningEffort = "high";
        }

        if (string.IsNullOrWhiteSpace(this.Settings.ModelVerbosity))
        {
            this.Settings.ModelVerbosity = "medium";
        }

        if (string.IsNullOrWhiteSpace(this.Settings.SandboxMode))
        {
            this.Settings.SandboxMode = "read-only";
        }

        this.NormalizeSelectionSettings();
        this.ApplyStartupWorkingDirectory();

        this._selectedModel = this.Settings.DefaultModel;
        this._selectedReasoningEffort = this.Settings.ReasoningEffort;
        this._selectedVerbosity = this.Settings.ModelVerbosity;
        this._codexProcessService.ApprovalRequestHandler = this.HandleApprovalRequestAsync;
        this._codexProcessService.UserInputRequestHandler = this.HandleUserInputRequestAsync;
        this._codexProcessService.ThreadCatalogChanged += this.HandleThreadCatalogChanged;
        this._codexProcessService.RateLimitsUpdated += this.HandleRateLimitsUpdated;
        this._codexProcessService.AccountUpdated += this.HandleAccountUpdated;

        this.SendCommand = new DelegateCommand(this.Send, () => !this.IsBusy && this.IsCodexReady && !string.IsNullOrWhiteSpace(this.BuildEffectivePrompt()));
        this.CancelCommand = new DelegateCommand(this.Cancel, () => this.IsBusy && !this.IsStopping);
        this.SaveSettingsCommand = new DelegateCommand(this.ApplySettings);
        this.ClearOutputCommand = new DelegateCommand(() => this.Output = string.Empty);
        this.UseSolutionDirectoryCommand = new DelegateCommand(this.UseSolutionDirectory);
        this.OpenCodexConfigCommand = new DelegateCommand(this.OpenCodexConfig);
        this.OpenExtensionSettingsCommand = new DelegateCommand(this.OpenExtensionSettings);
        this.OpenCodexSkillsFolderCommand = new DelegateCommand(this.OpenCodexSkillsFolder);
        this.OpenCodexDocsCommand = new DelegateCommand(this.OpenCodexDocs);
        this.OpenKeyboardShortcutsCommand = new DelegateCommand(this.OpenKeyboardShortcuts);
        this.OpenPathCommand = new DelegateCommand(this.OpenPath);
        this.OpenReferencedPathCommand = new DelegateCommand(this.OpenReferencedPath, this.CanOpenReferencedPath);
        this.RefreshCodexStatusCommand = new DelegateCommand(this.RefreshCodexStatus);
        this.RunCodexLoginCommand = new DelegateCommand(this.RunCodexLogin, _ => this.CanRunCodexLogin);
        this.CopyCodexInstallCommand = new DelegateCommand(this.CopyCodexInstallCommandText);
        this.OpenSettingsPanelCommand = new DelegateCommand(this.OpenSettingsPanel);
        this.RefreshIntegrationsCommand = new DelegateCommand(this.RefreshIntegrations);
        this.AddManagedMcpCommand = new DelegateCommand(this.AddManagedMcp);
        this.RemoveManagedMcpCommand = new DelegateCommand(this.RemoveManagedMcp);
        this.RefreshModelsCommand = new DelegateCommand(this.RefreshModels, _ => !this.IsRefreshingModels);
        this.AddCustomModelCommand = new DelegateCommand(this.AddCustomModel, _ => !string.IsNullOrWhiteSpace(this.CustomModelInput) || !string.IsNullOrWhiteSpace(this.SelectedModel));
        this.RemoveCustomModelCommand = new DelegateCommand(this.RemoveCustomModel);
        this.CreateSkillCommand = new DelegateCommand(this.CreateSkill, _ => this.CanCreateSkill());
        this.PasteImageCommand = new DelegateCommand(this.PasteImageFromClipboard);
        this.AddImageFileCommand = new DelegateCommand(this.AddAttachment);
        this.RemoveSelectedImageCommand = new DelegateCommand(this.RemoveSelectedImage, () => this.SelectedImagePath is not null);
        this.RemoveAttachmentCommand = new DelegateCommand(this.RemoveAttachment);
        this.RemoveDetectedPromptSkillCommand = new DelegateCommand(this.RemoveDetectedPromptSkill);
        this.InsertSlashCommandCommand = new DelegateCommand(this.InsertSlashCommand);
        this.InsertSelectedMentionCommand = new DelegateCommand(this.InsertSelectedMention, () => this.SelectedMention is not null);
        this.ReuseHistoryPromptCommand = new DelegateCommand(this.ReuseHistoryPrompt, () => this.SelectedHistoryPrompt is not null);
        this.NewThreadCommand = new DelegateCommand(this.StartNewThread, () => this.IsCodexReady && !this.IsStopping);
        this.DismissRecentTasksPreviewCommand = new DelegateCommand(this.DismissRecentTasksPreview);
        this.BeginRenameThreadCommand = new DelegateCommand(this.BeginRenameThread, parameter => !this.IsBusy && parameter is CodexThreadSummary);
        this.RenameThreadCommand = new DelegateCommand(this.RenameSelectedThread, this.CanRenameThread);
        this.CancelRenameThreadCommand = new DelegateCommand(this.CancelRenameThread, _ => !string.IsNullOrWhiteSpace(this.EditingThreadId));
        this.DeleteThreadCommand = new DelegateCommand(this.DeleteThread, parameter => !this.IsBusy && parameter is CodexThreadSummary);
        this.OpenHistoryPanelCommand = new DelegateCommand(this.OpenHistoryPanel);
        this.ToggleHistoryPanelCommand = new DelegateCommand(this.ToggleHistoryPanel);
        this.ToggleSettingsPanelCommand = new DelegateCommand(this.ToggleSettingsPanel);
        this.CloseSettingsDetailCommand = new DelegateCommand(this.CloseSettingsDetail);
        this.CloseSidebarCommand = new DelegateCommand(this.CloseSidebar);
        this.SelectSettingsSectionCommand = new DelegateCommand(this.SelectSettingsSection);
        this.TogglePreferredMcpCommand = new DelegateCommand(this.TogglePreferredMcp);
        this.SelectReasoningEffortCommand = new DelegateCommand(this.SelectReasoningEffort);
        this.SelectVerbosityCommand = new DelegateCommand(this.SelectVerbosity);
        this.SelectApprovalPolicyCommand = new DelegateCommand(this.SelectApprovalPolicy);
        this.SelectSandboxModeCommand = new DelegateCommand(this.SelectSandboxMode);
        this.ToggleSkillEnabledCommand = new DelegateCommand(this.ToggleSkillEnabled);
        this.InstallRemoteSkillCommand = new DelegateCommand(this.InstallRemoteSkill);
        this.SelectLanguageCommand = new DelegateCommand(this.SelectLanguage);
        this.LogOutCommand = new DelegateCommand(this.LogOut, _ => this.CanLogOutAndLogIn);
        this.LogOutAndLoginCommand = new DelegateCommand(this.LogOutAndLogin, _ => this.CanLogOutAndLogIn);
        this.ResolveApprovalCommand = new DelegateCommand(this.ResolveApproval);
        this.ResolveUserInputCommand = new DelegateCommand(this.ResolveUserInput);

        this.ReplaceModelOptions(MergeModelOptions(
            Enumerable.Empty<SelectionOption>(),
            CreateFallbackModelOptions(),
            this.Settings.CustomModels,
            this._selectedModel));

        foreach (string item in this.GetRecentPromptHistory())
        {
            this.PromptHistory.Add(item);
        }

        foreach (CodexManagedMcpServer server in this.Settings.ManagedMcpServers)
        {
            this.ManagedMcpServers.Add(CloneManagedMcpServer(server));
        }

        this.ManagedMcpServers.CollectionChanged += this.HandleManagedMcpServersChanged;
        this.Skills.CollectionChanged += this.HandleSkillsChanged;
        this.McpServers.CollectionChanged += this.HandleMcpServersChanged;
        this.Threads.CollectionChanged += this.HandleThreadsChanged;
        this.Messages.CollectionChanged += this.HandleMessagesChanged;

        this.RefreshMentions();
        this.UpdateContextEstimate();
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(this.InitializeSafeAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalizationService Localization { get; private set; }

    public void Dispose()
    {
        _ = (this._approvalDecisionTcs?.TrySetResult(JValue.CreateString("cancel")));
        _ = (this._userInputDecisionTcs?.TrySetResult(new JObject { ["answers"] = new JObject() }));
        this._cts?.Cancel();
        this._cts?.Dispose();
        this.ClearPendingAssistantOutput();
        this.ManagedMcpServers.CollectionChanged -= this.HandleManagedMcpServersChanged;
        this.Skills.CollectionChanged -= this.HandleSkillsChanged;
        this.McpServers.CollectionChanged -= this.HandleMcpServersChanged;
        this.Threads.CollectionChanged -= this.HandleThreadsChanged;
        this.Messages.CollectionChanged -= this.HandleMessagesChanged;
        this._codexProcessService.ThreadCatalogChanged -= this.HandleThreadCatalogChanged;
        this._codexProcessService.RateLimitsUpdated -= this.HandleRateLimitsUpdated;
        this._codexProcessService.AccountUpdated -= this.HandleAccountUpdated;
        this._codexProcessService.Dispose();
    }

    public CodexExtensionSettings Settings { get; }

    public ObservableCollection<string> MentionSuggestions { get; } = [];
    public ObservableCollection<string> AttachedImages { get; } = [];
    public ObservableCollection<string> PromptHistory { get; } = [];
    public ObservableCollection<CodexThreadSummary> Threads { get; } = [];
    public ObservableCollection<CodexAppSummary> Apps { get; } = [];
    public ObservableCollection<CodexMcpServerSummary> McpServers { get; } = [];
    public ObservableCollection<CodexManagedMcpServer> ManagedMcpServers { get; } = [];
    public ObservableCollection<CodexSkillSummary> Skills { get; } = [];
    public ObservableCollection<CodexRemoteSkillSummary> RemoteSkills { get; } = [];
    public ObservableCollection<CodexSkillSummary> DetectedPromptSkills { get; } = [];
    public ObservableCollection<CodexSlashCommand> SlashCommandSuggestions { get; } = [];
    public ObservableRangeCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<SelectionOption> ModelOptions { get; } = [];

    public bool IsRefreshingModels
    {
        get; private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            this.OnPropertyChanged();
            this.RefreshModelsCommand?.RaiseCanExecuteChanged();
        }
    }

    public string ModelRefreshStatus
    {
        get; private set
        {
            value ??= string.Empty;
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value;
            this.OnPropertyChanged();
        }
    } = string.Empty;

    public string CustomModelInput
    {
        get; set
        {
            value ??= string.Empty;
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value;
            this.OnPropertyChanged();
            this.AddCustomModelCommand?.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    public SelectionOption[] ReasoningOptions => MergeConfigurableOptions(
        this.Localization.CreateReasoningOptions(),
        this.Settings.CustomReasoningEfforts,
        this.SelectedReasoningEffort).ToArray();

    public SelectionOption[] ReasoningMenuOptions => new[]
    {
        new SelectionOption(this.Localization.ReasoningEffortLabel, "__label")
    }.Concat(this.ReasoningOptions).ToArray();

    public SelectionOption[] VerbosityOptions => MergeConfigurableOptions(
        this.Localization.CreateVerbosityOptions(),
        this.Settings.CustomVerbosityOptions,
        this.SelectedVerbosity).ToArray();

    public SelectionOption[] ServiceTierOptions => MergeConfigurableOptions(
        this.Localization.CreateServiceTierOptions(),
        this.Settings.CustomServiceTiers,
        this.SelectedServiceTier).ToArray();

    public SelectionOption[] ApprovalPolicyOptions => this.Localization.CreateApprovalPolicyOptions();

    public SelectionOption[] SandboxModeOptions => this.Localization.CreateSandboxModeOptions();

    public SelectionOption[] LanguageOptions => this.Localization.CreateLanguageOptions();

    public SelectionOption[] McpTransportOptions => new[]
    {
        new SelectionOption(this.Localization.ManagedMcpStdioOption, "stdio"),
        new SelectionOption(this.Localization.ManagedMcpUrlOption, "url")
    };

    public DelegateCommand SendCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand SaveSettingsCommand { get; }
    public DelegateCommand ClearOutputCommand { get; }
    public DelegateCommand UseSolutionDirectoryCommand { get; }
    public DelegateCommand OpenCodexConfigCommand { get; }
    public DelegateCommand OpenExtensionSettingsCommand { get; }
    public DelegateCommand OpenCodexSkillsFolderCommand { get; }
    public DelegateCommand OpenCodexDocsCommand { get; }
    public DelegateCommand OpenKeyboardShortcutsCommand { get; }
    public DelegateCommand OpenPathCommand { get; }
    public DelegateCommand OpenReferencedPathCommand { get; }
    public DelegateCommand RefreshCodexStatusCommand { get; }
    public DelegateCommand RunCodexLoginCommand { get; }
    public DelegateCommand CopyCodexInstallCommand { get; }
    public DelegateCommand OpenSettingsPanelCommand { get; }
    public DelegateCommand RefreshIntegrationsCommand { get; }
    public DelegateCommand AddManagedMcpCommand { get; }
    public DelegateCommand RemoveManagedMcpCommand { get; }
    public DelegateCommand RefreshModelsCommand { get; }
    public DelegateCommand AddCustomModelCommand { get; }
    public DelegateCommand RemoveCustomModelCommand { get; }
    public DelegateCommand CreateSkillCommand { get; }
    public DelegateCommand PasteImageCommand { get; }
    public DelegateCommand AddImageFileCommand { get; }
    public DelegateCommand RemoveSelectedImageCommand { get; }
    public DelegateCommand RemoveAttachmentCommand { get; }
    public DelegateCommand RemoveDetectedPromptSkillCommand { get; }
    public DelegateCommand InsertSlashCommandCommand { get; }
    public DelegateCommand InsertSelectedMentionCommand { get; }
    public DelegateCommand ReuseHistoryPromptCommand { get; }
    public DelegateCommand NewThreadCommand { get; }
    public DelegateCommand DismissRecentTasksPreviewCommand { get; }
    public DelegateCommand BeginRenameThreadCommand { get; }
    public DelegateCommand RenameThreadCommand { get; }
    public DelegateCommand CancelRenameThreadCommand { get; }
    public DelegateCommand DeleteThreadCommand { get; }
    public DelegateCommand OpenHistoryPanelCommand { get; }
    public DelegateCommand ToggleHistoryPanelCommand { get; }
    public DelegateCommand ToggleSettingsPanelCommand { get; }
    public DelegateCommand CloseSettingsDetailCommand { get; }
    public DelegateCommand CloseSidebarCommand { get; }
    public DelegateCommand SelectSettingsSectionCommand { get; }
    public DelegateCommand TogglePreferredMcpCommand { get; }
    public DelegateCommand SelectReasoningEffortCommand { get; }
    public DelegateCommand SelectVerbosityCommand { get; }
    public DelegateCommand SelectApprovalPolicyCommand { get; }
    public DelegateCommand SelectSandboxModeCommand { get; }
    public DelegateCommand ToggleSkillEnabledCommand { get; }
    public DelegateCommand InstallRemoteSkillCommand { get; }
    public DelegateCommand SelectLanguageCommand { get; }
    public DelegateCommand LogOutCommand { get; }
    public DelegateCommand LogOutAndLoginCommand { get; }
    public DelegateCommand ResolveApprovalCommand { get; }
    public DelegateCommand ResolveUserInputCommand { get; }

    public string Prompt
    {
        get => this._prompt;
        set => RunOnUiThread(() => { Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread(); this.ApplyRawPrompt(value); });
    }

    public string PromptEditorText
    {
        get => this._promptEditorText;
        set => RunOnUiThread(() => { Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread(); this.ApplyPromptEditorText(value); });
    }

    public bool HasSlashCommandSuggestions => this.SlashCommandSuggestions.Count > 0;

    public string Output
    {
        get => this._output;
        set => RunOnUiThread(() =>
        {
            this._output = value;
            this.OnPropertyChanged();
        });
    }

    public bool IsBusy
    {
        get; private set
        {
            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.PrimaryActionCommand));
            this.OnPropertyChanged(nameof(this.PrimaryActionTooltip));
            this.OnPropertyChanged(nameof(this.ShowSendActionIcon));
            this.OnPropertyChanged(nameof(this.ShowStopActionIcon));
            this.OnPropertyChanged(nameof(this.ShowStoppingIndicator));
            this.SendCommand.RaiseCanExecuteChanged();
            this.CancelCommand.RaiseCanExecuteChanged();
            this.NewThreadCommand.RaiseCanExecuteChanged();
            this.BeginRenameThreadCommand.RaiseCanExecuteChanged();
            this.RenameThreadCommand.RaiseCanExecuteChanged();
            this.CancelRenameThreadCommand.RaiseCanExecuteChanged();
            this.DeleteThreadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsStopping
    {
        get; private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.PrimaryActionCommand));
            this.OnPropertyChanged(nameof(this.PrimaryActionTooltip));
            this.OnPropertyChanged(nameof(this.ShowSendActionIcon));
            this.OnPropertyChanged(nameof(this.ShowStopActionIcon));
            this.OnPropertyChanged(nameof(this.ShowStoppingIndicator));
            this.CancelCommand.RaiseCanExecuteChanged();
            this.NewThreadCommand.RaiseCanExecuteChanged();
            this.DeleteThreadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ShowHistoryPanel
    {
        get; set
        {
            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.IsSidebarExpanded));
            this.OnPropertyChanged(nameof(this.SidebarWidth));
            this.OnPropertyChanged(nameof(this.IsHistoryViewSelected));
            this.OnPropertyChanged(nameof(this.IsSettingsViewSelected));
            this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
        }
    }

    public bool ShowSettingsPanel
    {
        get; set
        {
            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.IsSidebarExpanded));
            this.OnPropertyChanged(nameof(this.SidebarWidth));
            this.OnPropertyChanged(nameof(this.IsHistoryViewSelected));
            this.OnPropertyChanged(nameof(this.IsSettingsViewSelected));
            this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
        }
    }

    public bool IsSidebarExpanded => this.ShowHistoryPanel || this.ShowSettingsPanel;

    public double SidebarWidth => this.IsSidebarExpanded ? 292d : 0d;

    public bool IsHistoryViewSelected => this.ShowHistoryPanel || (this._pinRecentTasksPreview && this.ShowRecentTasksPreview);

    public bool IsSettingsViewSelected => this.ShowSettingsPanel;

    public string CurrentMentionQuery
    {
        get; private set
        {
            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasMentionSuggestions));
        }
    } = string.Empty;

    public bool HasMentionSuggestions => this.MentionSuggestions.Count > 0 && !string.IsNullOrWhiteSpace(this.CurrentMentionQuery);

    public ApprovalPromptViewModel? CurrentApprovalPrompt
    {
        get; private set
        {
            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasCurrentApprovalPrompt));
        }
    }

    public bool HasCurrentApprovalPrompt => this.CurrentApprovalPrompt is not null;

    public UserInputPromptViewModel? CurrentUserInputPrompt
    {
        get; private set
        {
            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasCurrentUserInputPrompt));
        }
    }

    public bool HasCurrentUserInputPrompt => this.CurrentUserInputPrompt is not null;

    public CodexEnvironmentStatus CodexEnvironmentStatus
    {
        get; private set
        {
            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.IsCodexReady));
            this.OnPropertyChanged(nameof(this.ShowCodexSetupCard));
            this.OnPropertyChanged(nameof(this.CodexSetupTitle));
            this.OnPropertyChanged(nameof(this.CodexSetupSummary));
            this.OnPropertyChanged(nameof(this.CodexSetupDetail));
            this.OnPropertyChanged(nameof(this.CodexSetupInstallCommand));
            this.OnPropertyChanged(nameof(this.CodexSetupExecutablePath));
            this.OnPropertyChanged(nameof(this.CodexSetupAuthenticationLabel));
            this.OnPropertyChanged(nameof(this.CodexSetupVersionLabel));
            this.OnPropertyChanged(nameof(this.ShowCodexSetupDetail));
            this.OnPropertyChanged(nameof(this.ShowCodexSetupVersion));
            this.OnPropertyChanged(nameof(this.NeedsCodexInstall));
            this.OnPropertyChanged(nameof(this.NeedsCodexLogin));
            this.OnPropertyChanged(nameof(this.CanRunCodexLogin));
            this.OnPropertyChanged(nameof(this.CurrentAccountLabel));
            this.OnPropertyChanged(nameof(this.CanLogOutAndLogIn));
            this.SendCommand.RaiseCanExecuteChanged();
            this.NewThreadCommand.RaiseCanExecuteChanged();
            this.RunCodexLoginCommand.RaiseCanExecuteChanged();
            this.LogOutCommand.RaiseCanExecuteChanged();
            this.LogOutAndLoginCommand.RaiseCanExecuteChanged();
        }
    } = new() { Stage = CodexSetupStage.Checking };

    public bool IsCodexReady => this.CodexEnvironmentStatus.IsReady;

    public bool HasCompletedEnvironmentCheck
    {
        get; private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.ShowCodexSetupCard));
        }
    }

    public bool ShowCodexSetupCard => this.HasCompletedEnvironmentCheck
        && this.CodexEnvironmentStatus.Stage != CodexSetupStage.Unknown
        && this.CodexEnvironmentStatus.Stage != CodexSetupStage.Checking
        && this.CodexEnvironmentStatus.Stage != CodexSetupStage.Ready;

    public bool ShowCodexSetupDetail => !string.IsNullOrWhiteSpace(this.CodexSetupDetail);

    public bool ShowCodexSetupVersion => !string.IsNullOrWhiteSpace(this.CodexSetupVersionLabel);

    public bool NeedsCodexInstall => this.CodexEnvironmentStatus.Stage == CodexSetupStage.MissingExecutable;

    public bool NeedsCodexLogin => this.CodexEnvironmentStatus.Stage == CodexSetupStage.MissingAuthentication
        && this.CodexEnvironmentStatus.RequiresOpenaiAuth;

    public bool CanRunCodexLogin => this.CodexEnvironmentStatus.Stage == CodexSetupStage.MissingAuthentication
        && this.CodexEnvironmentStatus.RequiresOpenaiAuth
        && !string.IsNullOrWhiteSpace(this.CodexEnvironmentStatus.ResolvedExecutablePath);

    public bool CanLogOutAndLogIn => !string.IsNullOrWhiteSpace(this.GetLoginExecutablePath());

    public string CodexSetupTitle => this.CodexEnvironmentStatus.Stage switch
    {
        CodexSetupStage.Checking => this.Localization.SetupCheckingTitle,
        CodexSetupStage.MissingExecutable => this.Localization.SetupMissingExecutableTitle,
        CodexSetupStage.MissingAuthentication => this.CodexEnvironmentStatus.RequiresOpenaiAuth
            ? this.Localization.SetupMissingAuthTitle
            : this.Localization.SetupMissingProviderAuthTitle,
        CodexSetupStage.Ready => this.Localization.SetupReadyTitle,
        CodexSetupStage.Error => this.Localization.SetupErrorTitle,
        _ => this.Localization.SetupCheckingTitle
    };

    public string CodexSetupSummary => this.CodexEnvironmentStatus.Stage switch
    {
        CodexSetupStage.Checking => this.Localization.SetupCheckingSummary,
        CodexSetupStage.MissingExecutable => this.Localization.SetupMissingExecutableSummary,
        CodexSetupStage.MissingAuthentication => this.CodexEnvironmentStatus.RequiresOpenaiAuth
            ? this.Localization.SetupMissingAuthSummary
            : this.Localization.SetupMissingProviderAuthSummary,
        CodexSetupStage.Ready => this.Localization.SetupReadySummary,
        CodexSetupStage.Error => this.Localization.SetupErrorSummary,
        _ => string.Empty
    };

    public string CodexSetupDetail => this.CodexEnvironmentStatus.Stage switch
    {
        CodexSetupStage.MissingExecutable => this.Localization.SetupInstallDetail,
        CodexSetupStage.MissingAuthentication => this.CodexEnvironmentStatus.RequiresOpenaiAuth
            ? this.Localization.SetupMissingAuthDetail
            : this.Localization.SetupMissingProviderAuthDetail,
        CodexSetupStage.Error => this.CodexEnvironmentStatus.ErrorDetail,
        _ => string.Empty
    };

    public string CodexSetupInstallCommand => CodexEnvironmentService.FallbackInstallCommand;

    public string CodexSetupExecutablePath => string.IsNullOrWhiteSpace(this.CodexEnvironmentStatus.ResolvedExecutablePath)
        ? this.CodexEnvironmentStatus.ConfiguredExecutablePath
        : this.CodexEnvironmentStatus.ResolvedExecutablePath;

    public string CodexSetupAuthenticationLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(this.CodexEnvironmentStatus.AuthenticationLabel))
            {
                return this.CodexEnvironmentStatus.AuthenticationLabel;
            }

            if (this.CodexEnvironmentStatus.HasApiKey)
            {
                return this.Localization.SetupApiKeyLabel;
            }

            if (this.CodexEnvironmentStatus.HasAuthFile)
            {
                return this.Localization.SetupAuthFileLabel;
            }

            return this.CodexEnvironmentStatus.AuthFilePath;
        }
    }

    public string CodexSetupVersionLabel => this.CodexEnvironmentStatus.Version;

    public string CurrentAccountLabel
    {
        get
        {
            if (this.CodexEnvironmentStatus.HasAccountEmail)
            {
                return this.CodexEnvironmentStatus.AccountEmail;
            }

            if (!string.IsNullOrWhiteSpace(this.CodexEnvironmentStatus.AuthenticationLabel))
            {
                return this.CodexEnvironmentStatus.AuthenticationLabel;
            }

            if (this.CodexEnvironmentStatus.HasApiKey)
            {
                return this.Localization.SetupApiKeyLabel;
            }

            return this.Localization.NotSignedInLabel;
        }
    }

    public bool HasManagedMcpServers => this.ManagedMcpServers.Count > 0;

    public bool HasDetectedMcpServers => this.McpServers.Count > 0;

    public bool HasSkills => this.Skills.Count > 0;

    public bool HasRemoteSkills => this.RemoteSkills.Count > 0;

    public bool HasDetectedPromptSkills => this.DetectedPromptSkills.Count > 0;

    public string PromptDisplayText
    {
        get; private set
        {
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasPromptDisplayText));
        }
    } = string.Empty;

    public bool HasPromptDisplayText => !string.IsNullOrWhiteSpace(this.PromptDisplayText);

    public string CodexConfigPath => this._solutionContextService.GetCodexConfigPath();

    public string ExtensionSettingsPath => this._settingsStore.SettingsFilePath;

    public string CodexSkillsDirectory => this._solutionContextService.GetCodexSkillsDirectory();

    public string SelectedSettingsSection
    {
        get; private set
        {
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.IsAccountSectionSelected));
            this.OnPropertyChanged(nameof(this.IsCodexMenuExpanded));
            this.OnPropertyChanged(nameof(this.IsCodexSectionSelected));
            this.OnPropertyChanged(nameof(this.IsIdeSectionSelected));
            this.OnPropertyChanged(nameof(this.IsMcpSectionSelected));
            this.OnPropertyChanged(nameof(this.IsSkillsSectionSelected));
            this.OnPropertyChanged(nameof(this.IsLanguageSectionSelected));
            this.OnPropertyChanged(nameof(this.IsSettingsDetailPanelVisible));
            this.OnPropertyChanged(nameof(this.SelectedSettingsSectionTitle));
        }
    } = string.Empty;

    public bool IsAccountSectionSelected => string.Equals(this.SelectedSettingsSection, SettingsSectionAccount, StringComparison.Ordinal);

    public bool IsCodexMenuExpanded => string.Equals(this.SelectedSettingsSection, SettingsSectionCodexMenu, StringComparison.Ordinal)
        || this.IsCodexSectionSelected;

    public bool IsCodexSectionSelected => string.Equals(this.SelectedSettingsSection, SettingsSectionCodex, StringComparison.Ordinal);

    public bool IsIdeSectionSelected => string.Equals(this.SelectedSettingsSection, SettingsSectionIde, StringComparison.Ordinal);

    public bool IsMcpSectionSelected => string.Equals(this.SelectedSettingsSection, SettingsSectionMcp, StringComparison.Ordinal);

    public bool IsSkillsSectionSelected => string.Equals(this.SelectedSettingsSection, SettingsSectionSkills, StringComparison.Ordinal);

    public bool IsLanguageSectionSelected => string.Equals(this.SelectedSettingsSection, SettingsSectionLanguage, StringComparison.Ordinal);

    public bool IsSettingsDetailPanelVisible => this.IsAccountSectionSelected
        || this.IsCodexSectionSelected
        || this.IsIdeSectionSelected
        || this.IsMcpSectionSelected
        || this.IsSkillsSectionSelected;

    public string SelectedSettingsSectionTitle => this.SelectedSettingsSection switch
    {
        SettingsSectionAccount => this.Localization.AccountTitle,
        SettingsSectionCodex => this.Localization.CodexSettingsNav,
        SettingsSectionIde => this.Localization.IdeSettingsNav,
        SettingsSectionMcp => this.Localization.McpSettingsNav,
        SettingsSectionSkills => this.Localization.SkillsSettingsNav,
        _ => this.Localization.SettingsTitle
    };

    public string SettingsWorkspaceTitle => this.Localization.CodexSettingsNav;

    public string SelectedModel
    {
        get => this._selectedModel;
        set
        {
            value = NormalizeModelValue(value);
            if (string.Equals(this._selectedModel, value, StringComparison.Ordinal))
            {
                return;
            }

            this._selectedModel = value;
            this.Settings.DefaultModel = value;
            this.EnsureSelectedModelOption(value);
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.ProfileLabel));
            this.OnPropertyChanged(nameof(this.SelectedModelLabel));
            this.AddCustomModelCommand?.RaiseCanExecuteChanged();
            this.SaveSettings();
        }
    }

    public string SelectedReasoningEffort
    {
        get => this._selectedReasoningEffort;
        set
        {
            value = NormalizeReasoningEffortValue(value);
            if (string.Equals(this._selectedReasoningEffort, value, StringComparison.Ordinal))
            {
                return;
            }

            this._selectedReasoningEffort = value;
            this.Settings.ReasoningEffort = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.ReasoningOptions));
            this.OnPropertyChanged(nameof(this.ReasoningMenuOptions));
            this.OnPropertyChanged(nameof(this.SelectedReasoningEffortLabel));
            this.SaveSettings();
        }
    }

    public string SelectedVerbosity
    {
        get => this._selectedVerbosity;
        set
        {
            value = EnsureKnownOrCustomOptionValue(value, this.VerbosityOptions, "medium");
            if (string.Equals(this._selectedVerbosity, value, StringComparison.Ordinal))
            {
                return;
            }

            this._selectedVerbosity = value;
            this.Settings.ModelVerbosity = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.VerbosityOptions));
            this.OnPropertyChanged(nameof(this.SelectedVerbosityLabel));
            this.SaveSettings();
        }
    }

    public string SelectedSandboxMode
    {
        get => this.Settings.SandboxMode;
        set
        {
            value = EnsureOptionValue(value, this.SandboxModeOptions, "read-only");
            if (string.Equals(this.Settings.SandboxMode, value, StringComparison.Ordinal))
            {
                return;
            }

            this.Settings.SandboxMode = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.SelectedSandboxModeLabel));
            this.SaveSettings();
        }
    }

    public string SelectedApprovalPolicy
    {
        get => this.Settings.ApprovalPolicy;
        set
        {
            value = EnsureOptionValue(value, this.ApprovalPolicyOptions, string.Empty);
            if (string.Equals(this.Settings.ApprovalPolicy, value, StringComparison.Ordinal))
            {
                return;
            }

            this.Settings.ApprovalPolicy = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.SelectedApprovalPolicyLabel));
            this.SaveSettings();
        }
    }

    public string ProfileLabel => string.IsNullOrWhiteSpace(this.Settings.Profile) ? "develop" : this.Settings.Profile;

    public string CollaborationModeLabel => this.PlanModeEnabled ? this.Localization.AgentModeLabel : this.Localization.QuestionModeLabel;

    public string SelectedModelLabel => GetOptionLabel(this.ModelOptions, this.SelectedModel, this.ModelOptions.FirstOrDefault()?.Label ?? "gpt-5.4");

    public bool IsFastModeEnabled => string.Equals(this.SelectedServiceTier, "fast", StringComparison.Ordinal);

    public string SelectedReasoningEffortLabel => GetOptionLabel(this.ReasoningOptions, this.SelectedReasoningEffort, this.ReasoningOptions.FirstOrDefault(option => string.Equals(option.Value, "high", StringComparison.Ordinal))?.Label ?? "high");

    public string SelectedVerbosityLabel => GetOptionLabel(this.VerbosityOptions, this.SelectedVerbosity, this.VerbosityOptions.FirstOrDefault(option => string.Equals(option.Value, "medium", StringComparison.Ordinal))?.Label ?? "medium");

    public string SelectedServiceTier
    {
        get => this.Settings.ServiceTier;
        set
        {
            value = EnsureKnownOrCustomOptionValue(value, this.ServiceTierOptions, string.Empty);
            if (string.Equals(this.Settings.ServiceTier, value, StringComparison.Ordinal))
            {
                return;
            }

            this.Settings.ServiceTier = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.ServiceTierOptions));
            this.OnPropertyChanged(nameof(this.SelectedServiceTierLabel));
            this.OnPropertyChanged(nameof(this.IsFastModeEnabled));
            this.SaveSettings();
        }
    }

    public string SelectedServiceTierLabel => GetOptionLabel(this.ServiceTierOptions, this.SelectedServiceTier, this.ServiceTierOptions.FirstOrDefault()?.Label ?? string.Empty);

    public string SelectedApprovalPolicyLabel => GetOptionLabel(this.ApprovalPolicyOptions, this.SelectedApprovalPolicy, this.ApprovalPolicyOptions.FirstOrDefault()?.Label ?? string.Empty);

    public string SelectedSandboxModeLabel => GetOptionLabel(this.SandboxModeOptions, this.SelectedSandboxMode, this.SandboxModeOptions.FirstOrDefault(option => string.Equals(option.Value, "read-only", StringComparison.Ordinal))?.Label ?? "read-only");

    public bool PlanModeEnabled
    {
        get => this.Settings.PlanModeEnabled;
        set
        {
            if (this.Settings.PlanModeEnabled == value)
            {
                return;
            }

            this.Settings.PlanModeEnabled = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.CollaborationModeLabel));
            this.SaveSettings();
        }
    }

    public bool IncludeIdeContextEnabled
    {
        get => this.Settings.IncludeIdeContext;
        set
        {
            if (this.Settings.IncludeIdeContext == value)
            {
                return;
            }

            this.Settings.IncludeIdeContext = value;
            this.OnPropertyChanged();
            this.SaveSettings();
        }
    }

    public Geometry ContextRingGeometry
    {
        get; private set => RunOnUiThread(() =>
                                               {
                                                   field = value;
                                                   this.OnPropertyChanged();
                                               });
    } = Geometry.Parse("M 8,1 A 7,7 0 1 1 7.99,1");

    public string ContextTokensLabel => this._lastKnownRemainingTokens > 0
        ? FormatCompactTokenCount(this._lastKnownRemainingTokens)
        : string.Empty;

    public string ContextWindowDetail => string.Format(
        CultureInfo.CurrentUICulture,
        this.Localization.ContextWindowDetailFormat,
        FormatPercent(GetContextUsedRatio(this._lastKnownContextTokensInWindow, this._contextTokenBudget)),
        FormatPercent(GetContextRemainingRatio(this._lastKnownContextTokensInWindow, this._contextTokenBudget)));

    public string SkillSearchText
    {
        get; set
        {
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value ?? string.Empty;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.VisibleSkills));
            this.OnPropertyChanged(nameof(this.VisibleRemoteSkills));
        }
    } = string.Empty;

    public string HistorySearchText
    {
        get; set
        {
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value ?? string.Empty;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.VisibleThreads));
            this.OnPropertyChanged(nameof(this.HasVisibleThreads));
        }
    } = string.Empty;

    public string LanguageSearchText
    {
        get; set
        {
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value ?? string.Empty;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.VisibleLanguageOptions));
            this.OnPropertyChanged(nameof(this.HasVisibleLanguageOptions));
        }
    } = string.Empty;

    public IEnumerable<CodexSkillSummary> VisibleSkills => this.Skills.Where(skill =>
        string.IsNullOrWhiteSpace(this.SkillSearchText)
        || (skill.DisplayTitle ?? string.Empty).IndexOf(this.SkillSearchText, StringComparison.OrdinalIgnoreCase) >= 0
        || (skill.Name ?? string.Empty).IndexOf(this.SkillSearchText, StringComparison.OrdinalIgnoreCase) >= 0
        || (skill.Summary ?? string.Empty).IndexOf(this.SkillSearchText, StringComparison.OrdinalIgnoreCase) >= 0);

    public IEnumerable<CodexRemoteSkillSummary> VisibleRemoteSkills => this.RemoteSkills.Where(skill =>
        string.IsNullOrWhiteSpace(this.SkillSearchText)
        || (skill.Name ?? string.Empty).IndexOf(this.SkillSearchText, StringComparison.OrdinalIgnoreCase) >= 0
        || (skill.Description ?? string.Empty).IndexOf(this.SkillSearchText, StringComparison.OrdinalIgnoreCase) >= 0);

    public IEnumerable<CodexThreadSummary> VisibleThreads => this.Threads.Where(thread =>
        string.IsNullOrWhiteSpace(this.HistorySearchText)
        || (thread.Title ?? string.Empty).IndexOf(this.HistorySearchText, StringComparison.OrdinalIgnoreCase) >= 0
        || (thread.Subtitle ?? string.Empty).IndexOf(this.HistorySearchText, StringComparison.OrdinalIgnoreCase) >= 0
        || (thread.Preview ?? string.Empty).IndexOf(this.HistorySearchText, StringComparison.OrdinalIgnoreCase) >= 0);

    public IEnumerable<CodexThreadSummary> RecentThreadsPreview => this.IsRecentTasksPreviewExpanded ? this.Threads : this.Threads.Take(3);

    public bool IsRecentTasksPreviewExpanded { get; private set; }

    public IEnumerable<SelectionOption> VisibleLanguageOptions => this.LanguageOptions.Where(option =>
        string.IsNullOrWhiteSpace(this.LanguageSearchText)
        || option.Label.IndexOf(this.LanguageSearchText, StringComparison.OrdinalIgnoreCase) >= 0);

    public string LanguageSearchPlaceholder => this.Localization.SearchLanguagesPlaceholder;

    public CodexRateLimitSummary RateLimitSummary
    {
        get; private set
        {
            field = value ?? new CodexRateLimitSummary();
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasRateLimitData));
            this.OnPropertyChanged(nameof(this.RateLimitEntries));
            this.OnPropertyChanged(nameof(this.ShowRateLimitUnavailableEntry));
        }
    } = new();

    public bool HasRateLimitData => this.RateLimitSummary.HasAnyData;

    public IEnumerable<CodexRateLimitWindowSummary> RateLimitEntries => this.RateLimitSummary.Entries;

    public bool ShowRateLimitUnavailableEntry => !this.HasRateLimitData;

    public bool HasPreferredMcpServers => (this.Settings.PreferredMcpServers?.Count ?? 0) > 0;

    public IEnumerable<string> PreferredMcpServers => this.Settings.PreferredMcpServers ?? Enumerable.Empty<string>();

    public bool HasThreads => this.Threads.Count > 0;

    public bool HasVisibleThreads => this.VisibleThreads.Any();

    public bool HasVisibleLanguageOptions => this.VisibleLanguageOptions.Any();

    public bool HasMoreThreadsThanPreview => !this.IsRecentTasksPreviewExpanded && this.Threads.Count > 3;

    public bool ShowRecentTasksPreview => this.HasThreads
        && !this.ShowHistoryPanel
        && !this.ShowSettingsPanel
        && !this._hideRecentTasksPreview
        && (this.Messages.Count == 0 || this._pinRecentTasksPreview);

    public DelegateCommand PrimaryActionCommand => this.IsBusy ? this.CancelCommand : this.SendCommand;

    public string PrimaryActionTooltip => this.IsStopping
        ? this.Localization.StoppingTooltip
        : (this.IsBusy ? this.Localization.StopTooltip : this.Localization.SendTooltip);

    public bool ShowSendActionIcon => !this.IsBusy;

    public bool ShowStopActionIcon => this.IsBusy && !this.IsStopping;

    public bool ShowStoppingIndicator => this.IsBusy && this.IsStopping;

    public string SelectedLanguageTag
    {
        get => NormalizeLanguageTag(this.Settings.LanguageOverride);
        set
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string normalized = NormalizeLanguageTag(value);
            if (string.Equals(NormalizeLanguageTag(this.Settings.LanguageOverride), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            this.Settings.LanguageOverride = normalized;
            this.ApplyLocalization(normalized);
            this.OnPropertyChanged();
            this.SaveSettings();
        }
    }

    public CodexThreadSummary? SelectedThread
    {
        get; set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;
            if (string.IsNullOrWhiteSpace(this.EditingThreadId))
            {
                this.RenameThreadName = value?.Title ?? string.Empty;
            }

            this.OnPropertyChanged();
            this.BeginRenameThreadCommand?.RaiseCanExecuteChanged();
            this.RenameThreadCommand?.RaiseCanExecuteChanged();
            this.CancelRenameThreadCommand?.RaiseCanExecuteChanged();
            this.DeleteThreadCommand?.RaiseCanExecuteChanged();

            if (!this._suppressThreadSelection && value is not null)
            {
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => this.OpenThreadAsync(value.ThreadId));
            }
        }
    }

    public string RenameThreadName
    {
        get; set
        {
            field = value;
            this.OnPropertyChanged();
            this.RenameThreadCommand?.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    public string EditingThreadId
    {
        get; private set
        {
            value ??= string.Empty;
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value;
            this.OnPropertyChanged();
            this.BeginRenameThreadCommand?.RaiseCanExecuteChanged();
            this.RenameThreadCommand?.RaiseCanExecuteChanged();
            this.CancelRenameThreadCommand?.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    public string NewSkillName
    {
        get; set
        {
            field = value;
            this.OnPropertyChanged();
            this.CreateSkillCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    public string NewSkillDescription
    {
        get; set
        {
            field = value;
            this.OnPropertyChanged();
        }
    } = string.Empty;

    public string? SelectedMention
    {
        get; set
        {
            field = value;
            this.OnPropertyChanged();
            this.InsertSelectedMentionCommand.RaiseCanExecuteChanged();
        }
    }

    public string? SelectedImagePath
    {
        get; set
        {
            field = value;
            this.OnPropertyChanged();
            this.RemoveSelectedImageCommand.RaiseCanExecuteChanged();
        }
    }

    public string? SelectedHistoryPrompt
    {
        get; set
        {
            field = value;
            this.OnPropertyChanged();
            this.ReuseHistoryPromptCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task SendAsync()
    {
        string editorPrompt = this._promptEditorText ?? string.Empty;
        string promptToSend = this.BuildEffectivePrompt(editorPrompt);
        if (this.IsBusy || string.IsNullOrWhiteSpace(promptToSend))
        {
            return;
        }

        (bool Handled, string Prompt, JObject? ReviewTarget) slashCommand = await this.DispatchSlashCommandAsync(editorPrompt);
        if (slashCommand.Handled)
        {
            return;
        }

        if (slashCommand.ReviewTarget is not null)
        {
            promptToSend = slashCommand.Prompt;
        }
        else if (!string.Equals(slashCommand.Prompt, editorPrompt, StringComparison.Ordinal))
        {
            promptToSend = this.BuildEffectivePrompt(slashCommand.Prompt);
        }

        if (!this.IsCodexReady)
        {
            this.AppendOutput("[" + this.Localization.OutputTagSetup + "] " + this.CodexSetupSummary + Environment.NewLine);
            return;
        }

        this.EnsureThreadMatchesWorkingDirectory();
        bool shouldAutoNameThread = this.Messages.Count == 0 && this.SelectedThread is null;

        if (shouldAutoNameThread)
        {
            _ = this.BeginConversationStateChange();
            this.Settings.CurrentThreadId = string.Empty;
            this.Settings.LastThreadWorkingDirectory = this.Settings.WorkingDirectory;
            this._codexProcessService.ResetThread();
        }

        if (string.IsNullOrWhiteSpace(this.Settings.CurrentThreadId))
        {
            this._codexProcessService.ResetThread();
        }

        long conversationStateVersion = this.CaptureConversationStateVersion();

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        string ideContextSummary = await this.CaptureIdeContextSummaryAsync();

        this.SaveSettings();
        this.AddPromptToHistory(promptToSend);
        this.ClearPersistedEventMessages();
        this.AddUserMessage(promptToSend.Trim());

        this.IsBusy = true;
        this.IsStopping = false;
        this._cts = new CancellationTokenSource();
        this._currentAssistantMessage = null;
        this._currentPlanMessage = null;
        this.ClearTransientStatusMessage();
        this.Prompt = string.Empty;

        try
        {
            int exitCode = await this._codexProcessService.ExecuteAsync(
                promptToSend,
                this.Settings,
                this.AttachedImages.ToList(),
                ideContextSummary,
                slashCommand.ReviewTarget,
                onOutput: text =>
                {
                    if (this.IsConversationStateCurrent(conversationStateVersion))
                    {
                        this.AppendAssistantOutput(text, conversationStateVersion);
                    }
                },
                onError: text =>
                {
                    if (this.IsConversationStateCurrent(conversationStateVersion))
                    {
                        this.AppendStderr(text);
                    }
                },
                onEventMessage: message =>
                {
                    if (this.IsConversationStateCurrent(conversationStateVersion))
                    {
                        this.AddRuntimeEventMessage(message);
                    }
                },
                onTokenUsage: (tokensInContextWindow, contextWindow) =>
                {
                    if (this.IsConversationStateCurrent(conversationStateVersion))
                    {
                        this.UpdateTokenUsage(tokensInContextWindow, contextWindow);
                    }
                },
                cancellationToken: this._cts.Token);

            await this.FlushPendingAssistantOutputAsync().ConfigureAwait(false);

            if (!this.IsConversationStateCurrent(conversationStateVersion))
            {
                return;
            }

            if (exitCode != 0 && this._currentAssistantMessage is null)
            {
                this.AddAssistantMessage(this.Localization.CodexNoResponse);
            }

            this.Settings.CurrentThreadId = this._codexProcessService.CurrentThreadId ?? this.Settings.CurrentThreadId;
            this.Settings.LastThreadWorkingDirectory = this.Settings.WorkingDirectory;
            this.SaveSettings();
            await this.RefreshThreadsAsync(this.Settings.CurrentThreadId).ConfigureAwait(false);
            await this.EnsureCurrentThreadHasFriendlyNameAsync(shouldAutoNameThread ? promptToSend : null).ConfigureAwait(false);
            await this.RefreshServerSurfacesAsync().ConfigureAwait(false);
            this.AppendOutput($"{Environment.NewLine}[{this.Localization.ExitCodeLabel}: {exitCode}]{Environment.NewLine}");
        }
        catch (OperationCanceledException)
        {
            if (this.IsConversationStateCurrent(conversationStateVersion))
            {
                this.AddAssistantMessage(this.Localization.ExecutionCanceled);
                this.AppendOutput($"{Environment.NewLine}[{this.Localization.ExecutionCanceledTag}]{Environment.NewLine}");
            }
        }
        catch (Exception ex)
        {
            if (this.IsConversationStateCurrent(conversationStateVersion))
            {
                this.AddAssistantMessage(this.Localization.ExecutionError + " " + ex.Message);
                this.AppendOutput($"{Environment.NewLine}[{this.Localization.ExecutionErrorTag}] {ex.Message}{Environment.NewLine}");
            }
        }
        finally
        {
            await this.FlushPendingAssistantOutputAsync().ConfigureAwait(false);

            if (this.IsConversationStateCurrent(conversationStateVersion))
            {
                this.ClearTransientStatusMessage();
            }

            this.IsStopping = false;
            this.IsBusy = false;
            this._cts?.Dispose();
            this._cts = null;
        }
    }

    private async Task<(bool Handled, string Prompt, JObject? ReviewTarget)> DispatchSlashCommandAsync(string prompt)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        string trimmed = (prompt ?? string.Empty).Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return (false, prompt ?? string.Empty, null);
        }

        int separatorIndex = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        string command = (separatorIndex < 0 ? trimmed : trimmed.Substring(0, separatorIndex)).ToLowerInvariant();
        string arguments = separatorIndex < 0 ? string.Empty : trimmed.Substring(separatorIndex + 1).Trim();
        if (!AvailableSlashCommands.Any(item => string.Equals(item.CommandText, command, StringComparison.Ordinal)))
        {
            return (false, prompt ?? string.Empty, null);
        }

        try
        {
            switch (command)
            {
                case "/new":
                case "/clear":
                    this.ClearSlashCommandComposer();
                    this.StartNewThread();
                    return (true, string.Empty, null);

                case "/resume":
                    this.ClearSlashCommandComposer();
                    if (string.IsNullOrWhiteSpace(arguments))
                    {
                        this.OpenHistoryPanel();
                    }
                    else
                    {
                        await this.OpenThreadAsync(arguments);
                    }

                    return (true, string.Empty, null);

                case "/fork":
                    await this.ForkCurrentThreadAsync();
                    return (true, string.Empty, null);

                case "/compact":
                    await this.CompactCurrentThreadAsync();
                    return (true, string.Empty, null);

                case "/review":
                    this.PlanModeEnabled = false;
                    this.ClearSlashCommandComposer();
                    return (false, trimmed, BuildReviewTarget(arguments));

                case "/model":
                    this.ClearSlashCommandComposer();
                    if (string.IsNullOrWhiteSpace(arguments))
                    {
                        this.SelectSettingsSection(SettingsSectionCodex);
                    }
                    else
                    {
                        this.SelectedModel = arguments;
                    }

                    return (true, string.Empty, null);

                case "/fast":
                    this.ClearSlashCommandComposer();
                    this.SelectedServiceTier = this.IsFastModeEnabled ? string.Empty : "fast";
                    return (true, string.Empty, null);

                case "/plan":
                    this.PlanModeEnabled = true;
                    this.ClearSlashCommandComposer();
                    return string.IsNullOrWhiteSpace(arguments)
                        ? (true, string.Empty, null)
                        : (false, arguments, null);

                case "/permissions":
                    this.ClearSlashCommandComposer();
                    this.ApplyPermissionsSlashCommand(arguments);
                    return (true, string.Empty, null);

                case "/ide":
                    this.ClearSlashCommandComposer();
                    this.IncludeIdeContextEnabled = !this.IncludeIdeContextEnabled;
                    return (true, string.Empty, null);

                case "/status":
                    this.ClearSlashCommandComposer();
                    this.AddAssistantMessage(this.BuildSlashStatusMessage());
                    return (true, string.Empty, null);

                case "/skills":
                    this.ClearSlashCommandComposer();
                    this.SelectSettingsSection(SettingsSectionSkills);
                    return (true, string.Empty, null);

                case "/mcp":
                    this.ClearSlashCommandComposer();
                    this.SelectSettingsSection(SettingsSectionMcp);
                    return (true, string.Empty, null);

                case "/apps":
                    this.ClearSlashCommandComposer();
                    this.SelectSettingsSection(SettingsSectionAccount);
                    return (true, string.Empty, null);

                case "/rename":
                    await this.RenameCurrentThreadAsync(arguments);
                    return (true, string.Empty, null);

                case "/archive":
                    await this.ArchiveCurrentThreadAsync(deletePermanently: false);
                    return (true, string.Empty, null);

                case "/delete":
                    await this.ArchiveCurrentThreadAsync(deletePermanently: true);
                    return (true, string.Empty, null);
            }
        }
        catch (Exception ex)
        {
            this.ClearSlashCommandComposer();
            this.AddAssistantMessage(this.Localization.ExecutionError + " " + ex.Message);
            this.AppendOutput("[" + command + "] " + ex.Message + Environment.NewLine);
            return (true, string.Empty, null);
        }

        return (false, prompt ?? string.Empty, null);
    }

    private static JObject BuildReviewTarget(string arguments)
    {
        string value = (arguments ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return new JObject { ["type"] = "uncommittedChanges" };
        }

        const string BasePrefix = "base ";
        if (value.StartsWith(BasePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new JObject
            {
                ["type"] = "baseBranch",
                ["branch"] = value.Substring(BasePrefix.Length).Trim()
            };
        }

        const string CommitPrefix = "commit ";
        if (value.StartsWith(CommitPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new JObject
            {
                ["type"] = "commit",
                ["sha"] = value.Substring(CommitPrefix.Length).Trim()
            };
        }

        return new JObject
        {
            ["type"] = "custom",
            ["instructions"] = value
        };
    }

    private async Task ForkCurrentThreadAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        string threadId = this.Settings.CurrentThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("A saved Codex thread is required before it can be forked.");
        }

        this.ClearSlashCommandComposer();
        this.IsBusy = true;
        try
        {
            string forkedThreadId = await this._codexProcessService.ForkThreadAsync(this.Settings, threadId, CancellationToken.None);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            this.Settings.CurrentThreadId = forkedThreadId;
            this.SaveSettings();
            await this.RefreshThreadsAsync(forkedThreadId);
            await this.OpenThreadAsync(forkedThreadId);
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    private async Task CompactCurrentThreadAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        string threadId = this.Settings.CurrentThreadId;
        this.ClearSlashCommandComposer();
        this.IsBusy = true;
        try
        {
            await this._codexProcessService.CompactThreadAsync(this.Settings, threadId, CancellationToken.None);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await this.OpenThreadAsync(threadId);
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    private async Task RenameCurrentThreadAsync(string name)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        string threadId = this.Settings.CurrentThreadId;
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("/rename requires a saved thread and a non-empty name.");
        }

        this.ClearSlashCommandComposer();
        await this._codexProcessService.RenameThreadAsync(this.Settings, threadId, name, CancellationToken.None);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        await this.RefreshThreadsAsync(threadId);
    }

    private async Task ArchiveCurrentThreadAsync(bool deletePermanently)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        string threadId = this.Settings.CurrentThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("A saved Codex thread is required for this command.");
        }

        this.ClearSlashCommandComposer();
        if (deletePermanently)
        {
            await this._codexProcessService.DeleteThreadAsync(this.Settings, threadId, CancellationToken.None);
        }
        else
        {
            await this._codexProcessService.ArchiveThreadAsync(this.Settings, threadId, CancellationToken.None);
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        this.StartNewThread();
    }

    private void ApplyPermissionsSlashCommand(string value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            this.SelectSettingsSection(SettingsSectionCodex);
            return;
        }

        if (this.SandboxModeOptions.Any(option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            this.SelectedSandboxMode = normalized;
            return;
        }

        if (this.ApprovalPolicyOptions.Any(option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            this.SelectedApprovalPolicy = normalized;
            return;
        }

        throw new InvalidOperationException("Unsupported permissions value: " + value);
    }

    private string BuildSlashStatusMessage()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "- model: `" + this.SelectedModel + "`",
            "- reasoning_effort: `" + this.SelectedReasoningEffort + "`",
            "- verbosity: `" + this.SelectedVerbosity + "`",
            "- service_tier: `" + this.SelectedServiceTier + "`",
            "- approval_policy: `" + this.SelectedApprovalPolicy + "`",
            "- sandbox_mode: `" + this.SelectedSandboxMode + "`",
            "- cwd: `" + this.Settings.WorkingDirectory + "`",
            "- thread_id: `" + this.Settings.CurrentThreadId + "`",
            "- ide_context: `" + this.IncludeIdeContextEnabled.ToString().ToLowerInvariant() + "`"
        });
    }

    private void ClearSlashCommandComposer()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.PromptEditorText = string.Empty;
    }

    private async Task EnsureCurrentThreadHasFriendlyNameAsync(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(this.Settings.CurrentThreadId) || string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        CodexThreadSummary? currentThread = null;
        RunOnUiThread(() =>
        {
            currentThread = this.Threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, this.Settings.CurrentThreadId, StringComparison.Ordinal));
        });

        if (currentThread is null || !string.IsNullOrWhiteSpace(currentThread.Name))
        {
            return;
        }

        string friendlyName = BuildFriendlyThreadName(prompt);
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return;
        }

        await this._codexProcessService.RenameThreadAsync(this.Settings, currentThread.ThreadId, friendlyName, CancellationToken.None).ConfigureAwait(false);
        await this.RefreshThreadsAsync(currentThread.ThreadId).ConfigureAwait(false);
    }

    private static string BuildFriendlyThreadName(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }

        string compact = Regex.Replace(prompt.Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
        if (compact.Length <= 96)
        {
            return compact;
        }

        string shortened = compact.Substring(0, 96).TrimEnd();
        return shortened + "...";
    }

    private void Send()
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(this.SendAsync);
    }

    private async Task<string> CaptureIdeContextSummaryAsync()
    {
        if (!this.IncludeIdeContextEnabled)
        {
            return string.Empty;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        string summary = this._solutionContextService.BuildIdeContextSummary(this.Settings.WorkingDirectory);
        return string.IsNullOrWhiteSpace(summary)
            ? string.Empty
            : this.Localization.IdeContextPrefix + Environment.NewLine + summary;
    }

    private void SaveSettings()
    {
        this.Settings.ManagedMcpServers = this.ManagedMcpServers
            .Select(CloneManagedMcpServer)
            .ToList();
        this.Settings.PreferredMcpServers = this.Settings.PreferredMcpServers
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        this.Settings.CustomModels = this.Settings.CustomModels
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => NormalizeModelValue(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        this.Settings.CustomReasoningEfforts = NormalizeManualOptionEntries(this.Settings.CustomReasoningEfforts)
            .Select(NormalizeManualReasoningOptionEntry)
            .Where(entry => !string.IsNullOrWhiteSpace(ParseManualSelectionOption(entry)?.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        this.Settings.CustomVerbosityOptions = NormalizeManualOptionEntries(this.Settings.CustomVerbosityOptions);
        this.Settings.CustomServiceTiers = NormalizeManualOptionEntries(this.Settings.CustomServiceTiers);
        this.PersistSelectedModelIfCustom();
        this.Settings.DefaultModel = this.SelectedModel;
        this.Settings.ReasoningEffort = this.SelectedReasoningEffort;
        this.Settings.ModelVerbosity = this.SelectedVerbosity;
        this.Settings.ServiceTier = this.SelectedServiceTier;
        this.Settings.ApprovalPolicy = this.SelectedApprovalPolicy;
        this.Settings.SandboxMode = this.SelectedSandboxMode;
        this._settingsStore.Save(this.Settings);
    }

    private void ApplySettings()
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            this.SaveSettings();
            await this.RefreshCodexStatusAsync().ConfigureAwait(false);
            if (!this.IsCodexReady)
            {
                this.ClearServerSurfaces();
                return;
            }

            await this.RefreshModelOptionsAsync().ConfigureAwait(false);
            await this.RefreshServerSurfacesAsync(forceSkillReload: true).ConfigureAwait(false);
        });
    }

    private void Cancel()
    {
        if (!this.IsBusy || this.IsStopping)
        {
            return;
        }

        _ = this.BeginConversationStateChange();
        this.IsStopping = true;
        _ = (this._approvalDecisionTcs?.TrySetResult(JValue.CreateString("cancel")));
        this.CurrentApprovalPrompt = null;
        this._approvalDecisionTcs = null;
        this.DismissUserInputPrompt();
        CancellationTokenSource? cts = this._cts;
        this._cts = null;
        this._codexProcessService.CancelActiveTurn();
        cts?.Cancel();
        cts?.Dispose();
        this.ClearTransientStatusMessage();
        this._currentAssistantMessage = null;
        this._currentPlanMessage = null;
        this.IsStopping = false;
        this.IsBusy = false;
    }

    public void PasteImageFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsImage())
            {
                return;
            }

            BitmapSource? image = Clipboard.GetImage();
            if (image is null)
            {
                return;
            }

            string filePath = SaveBitmapToTempPng(image);
            this.AttachedImages.Add(filePath);
            this.SelectedImagePath = filePath;
            this.UpdateContextEstimate();
        }
        catch (Exception ex)
        {
            this.AppendOutput(this.Localization.ImagePasteErrorPrefix + ex.Message + Environment.NewLine);
        }
    }

    private void AddAttachment()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        OpenFileDialog dialog = new()
        {
            Filter = this.Localization.AllFilesFilter,
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (string? fileName in dialog.FileNames)
        {
            if (IsImageFile(fileName))
            {
                this.AttachedImages.Add(fileName);
                this.SelectedImagePath = fileName;
            }
            else
            {
                this.AppendFileReferenceToPrompt(fileName);
            }
        }

        this.UpdateContextEstimate();
    }

    private void RemoveSelectedImage()
    {
        if (this.SelectedImagePath is null)
        {
            return;
        }

        _ = this.AttachedImages.Remove(this.SelectedImagePath);
        this.SelectedImagePath = null;
        this.UpdateContextEstimate();
    }

    private void RemoveAttachment(object? parameter)
    {
        if (parameter is not string filePath || string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        _ = this.AttachedImages.Remove(filePath);
        if (string.Equals(this.SelectedImagePath, filePath, StringComparison.Ordinal))
        {
            this.SelectedImagePath = null;
        }

        this.UpdateContextEstimate();
    }

    private void UseSolutionDirectory()
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            this.ApplyWorkingDirectory(this._solutionContextService.GetBestWorkingDirectory(), resetConversation: true);
            this.OnPropertyChanged(nameof(this.Settings));
            this.SaveSettings();
            await this.RefreshThreadsAsync(this.Settings.CurrentThreadId).ConfigureAwait(false);
            await this.RefreshModelOptionsAsync().ConfigureAwait(false);
            await this.RefreshServerSurfacesAsync().ConfigureAwait(false);
        });
    }

    private void ApplyStartupWorkingDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string? solutionDirectory = this._solutionContextService.TryGetBestWorkspaceDirectory();
        if (string.IsNullOrWhiteSpace(solutionDirectory) || !Directory.Exists(solutionDirectory))
        {
            return;
        }

        if (string.Equals(this.Settings.WorkingDirectory, solutionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        this.ApplyWorkingDirectory(solutionDirectory, resetConversation: true);
        this.OnPropertyChanged(nameof(this.Settings));
        this._settingsStore.Save(this.Settings);
    }

    private void ApplyWorkingDirectory(string workingDirectory, bool resetConversation)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return;
        }

        this.Settings.WorkingDirectory = workingDirectory;
        if (!resetConversation)
        {
            return;
        }

        _ = this.BeginConversationStateChange();
        this.Settings.CurrentThreadId = string.Empty;
        this.Settings.LastThreadWorkingDirectory = workingDirectory;
        this._codexProcessService.ResetThread();
        RunOnUiThread(() =>
        {
            this._suppressThreadSelection = true;
            this.SelectedThread = null;
            this._suppressThreadSelection = false;
            this.EditingThreadId = string.Empty;
            this.RenameThreadName = string.Empty;
            this.Messages.Clear();
            this.Output = string.Empty;
        });
    }

    private void EnsureThreadMatchesWorkingDirectory()
    {
        string currentWorkingDirectory = (this.Settings.WorkingDirectory ?? string.Empty).Trim();
        string lastThreadWorkingDirectory = (this.Settings.LastThreadWorkingDirectory ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(currentWorkingDirectory))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(this.Settings.CurrentThreadId))
        {
            this.Settings.LastThreadWorkingDirectory = currentWorkingDirectory;
            return;
        }

        if (string.Equals(currentWorkingDirectory, lastThreadWorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        this.Settings.CurrentThreadId = string.Empty;
        this.Settings.LastThreadWorkingDirectory = currentWorkingDirectory;
        this._codexProcessService.ResetThread();
    }

    private void NormalizeSelectionSettings()
    {
        this.Settings.DefaultModel = NormalizeModelValue(this.Settings.DefaultModel);
        if (string.IsNullOrWhiteSpace(this.Settings.DefaultModel))
        {
            this.Settings.DefaultModel = "gpt-5.4";
        }

        this.Settings.ReasoningEffort = EnsureKnownOrCustomOptionValue(NormalizeReasoningEffortValue(this.Settings.ReasoningEffort), this.ReasoningOptions, "high");
        this.Settings.ModelVerbosity = EnsureKnownOrCustomOptionValue(this.Settings.ModelVerbosity, this.VerbosityOptions, "medium");
        this.Settings.ServiceTier = EnsureKnownOrCustomOptionValue(this.Settings.ServiceTier, this.ServiceTierOptions, string.Empty);
        this.Settings.SandboxMode = EnsureOptionValue(this.Settings.SandboxMode, this.SandboxModeOptions, "read-only");
        this.Settings.ApprovalPolicy = EnsureOptionValue(this.Settings.ApprovalPolicy, this.ApprovalPolicyOptions, string.Empty);
    }

    private void EnsureSettingsCollectionsInitialized()
    {
        this.Settings.PromptHistory ??= [];
        this.Settings.CustomModels ??= [];
        this.Settings.CustomReasoningEfforts ??= [];
        this.Settings.CustomVerbosityOptions ??= [];
        this.Settings.CustomServiceTiers ??= [];
        this.Settings.ManagedMcpServers ??= [];
        this.Settings.PreferredMcpServers ??= [];

        this.Settings.PromptHistory = this.Settings.PromptHistory
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToList();

        this.Settings.CustomModels = this.Settings.CustomModels
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => NormalizeModelValue(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        this.Settings.CustomReasoningEfforts = NormalizeManualOptionEntries(this.Settings.CustomReasoningEfforts)
            .Select(NormalizeManualReasoningOptionEntry)
            .Where(entry => !string.IsNullOrWhiteSpace(ParseManualSelectionOption(entry)?.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        this.Settings.CustomVerbosityOptions = NormalizeManualOptionEntries(this.Settings.CustomVerbosityOptions);
        this.Settings.CustomServiceTiers = NormalizeManualOptionEntries(this.Settings.CustomServiceTiers);

        this.Settings.ManagedMcpServers = this.Settings.ManagedMcpServers
            .Where(server => server is not null)
            .ToList();

        this.Settings.PreferredMcpServers = this.Settings.PreferredMcpServers
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string EnsureOptionValue(string? currentValue, IEnumerable<SelectionOption> options, string fallbackValue)
    {
        string normalized = (currentValue ?? string.Empty).Trim();
        if (options.Any(option => string.Equals(option.Value, normalized, StringComparison.Ordinal)))
        {
            return normalized;
        }

        if (options.Any(option => string.Equals(option.Value, fallbackValue, StringComparison.Ordinal)))
        {
            return fallbackValue;
        }

        return options.FirstOrDefault()?.Value ?? fallbackValue;
    }

    private static string EnsureKnownOrCustomOptionValue(string? currentValue, IEnumerable<SelectionOption> options, string fallbackValue)
    {
        string normalized = (currentValue ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            string? knownValue = options.FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase))?.Value;
            return string.IsNullOrWhiteSpace(knownValue) ? normalized : knownValue!;
        }

        if (options.Any(option => string.Equals(option.Value, fallbackValue, StringComparison.Ordinal)))
        {
            return fallbackValue;
        }

        return options.FirstOrDefault()?.Value ?? fallbackValue;
    }

    private static IReadOnlyList<SelectionOption> MergeConfigurableOptions(
        IEnumerable<SelectionOption> defaultOptions,
        IEnumerable<string>? manualOptions,
        string selectedValue)
    {
        List<SelectionOption> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void AddOption(SelectionOption? option)
        {
            if (option is null)
            {
                return;
            }

            string value = (option.Value ?? string.Empty).Trim();
            if (!seen.Add(value))
            {
                return;
            }

            string label = string.IsNullOrWhiteSpace(option.Label) ? value : option.Label.Trim();
            result.Add(new SelectionOption(label, value));
        }

        foreach (SelectionOption option in defaultOptions ?? Enumerable.Empty<SelectionOption>())
        {
            AddOption(option);
        }

        foreach (string entry in manualOptions ?? Enumerable.Empty<string>())
        {
            AddOption(ParseManualSelectionOption(entry));
        }

        string selected = (selectedValue ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            AddOption(new SelectionOption(selected + " (custom)", selected));
        }

        return result;
    }

    private static List<string> NormalizeManualOptionEntries(IEnumerable<string>? entries)
    {
        return entries?
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];
    }

    private static string NormalizeManualReasoningOptionEntry(string entry)
    {
        SelectionOption? option = ParseManualSelectionOption(entry);
        if (option is null)
        {
            return string.Empty;
        }

        string value = NormalizeReasoningEffortValue(option.Value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Equals(option.Label, option.Value, StringComparison.Ordinal)
            ? value
            : option.Label + "|" + value;
    }

    private static SelectionOption? ParseManualSelectionOption(string? entry)
    {
        string normalized = (entry ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        int separatorIndex = normalized.IndexOf('|');
        if (separatorIndex < 0)
        {
            separatorIndex = normalized.IndexOf('=');
        }

        if (separatorIndex > 0 && separatorIndex < normalized.Length - 1)
        {
            string label = normalized.Substring(0, separatorIndex).Trim();
            string value = normalized.Substring(separatorIndex + 1).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return new SelectionOption(string.IsNullOrWhiteSpace(label) ? value : label, value);
            }
        }

        return new SelectionOption(normalized, normalized);
    }

    private static string NormalizeModelValue(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        return string.Equals(normalized, "gpt-5.2 codex", StringComparison.OrdinalIgnoreCase)
            ? "gpt-5.2-codex"
            : normalized;
    }

    private static string NormalizeReasoningEffortValue(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "minimum" or "min" or "minimal" => "minimal",
            "maximum" or "max" => "xhigh",
            _ => normalized,
        };
    }

    private static string NormalizeLanguageTag(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.ToLowerInvariant() switch
        {
            "pt" => "pt-BR",
            "en-us" => "en",
            _ => normalized
        };
    }

    private static string GetOptionLabel(IEnumerable<SelectionOption> options, string? value, string fallbackLabel)
    {
        string normalized = (value ?? string.Empty).Trim();
        string? label = options.FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase))?.Label;
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label!;
        }

        return string.IsNullOrWhiteSpace(normalized) ? fallbackLabel : normalized;
    }

    private string GetLoginExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(this.CodexEnvironmentStatus.ResolvedExecutablePath))
        {
            return this.CodexEnvironmentStatus.ResolvedExecutablePath;
        }

        return this.Settings.CodexExecutablePath ?? string.Empty;
    }

    private void ApplyLocalization(string? languageOverride)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        this.Localization = new LocalizationService(languageOverride);
        CultureInfo.CurrentUICulture = this.Localization.Culture;
        CultureInfo.CurrentCulture = this.Localization.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = this.Localization.Culture;
        CultureInfo.DefaultThreadCurrentCulture = this.Localization.Culture;
        CodexToolWindowManager.RefreshSettingsToolWindowCaption(this.Localization);
        this.OnPropertyChanged(nameof(this.Localization));
        this.OnPropertyChanged(nameof(this.ReasoningOptions));
        this.OnPropertyChanged(nameof(this.ReasoningMenuOptions));
        this.OnPropertyChanged(nameof(this.VerbosityOptions));
        this.OnPropertyChanged(nameof(this.ServiceTierOptions));
        this.OnPropertyChanged(nameof(this.ApprovalPolicyOptions));
        this.OnPropertyChanged(nameof(this.SandboxModeOptions));
        this.OnPropertyChanged(nameof(this.LanguageOptions));
        this.OnPropertyChanged(nameof(this.SelectedLanguageTag));
        this.OnPropertyChanged(nameof(this.SelectedReasoningEffortLabel));
        this.OnPropertyChanged(nameof(this.SelectedVerbosityLabel));
        this.OnPropertyChanged(nameof(this.SelectedServiceTierLabel));
        this.OnPropertyChanged(nameof(this.SelectedApprovalPolicyLabel));
        this.OnPropertyChanged(nameof(this.SelectedSandboxModeLabel));
        this.OnPropertyChanged(nameof(this.CollaborationModeLabel));
        this.OnPropertyChanged(nameof(this.SelectedSettingsSectionTitle));
        this.OnPropertyChanged(nameof(this.SettingsWorkspaceTitle));
        this.OnPropertyChanged(nameof(this.CurrentAccountLabel));
        this.OnPropertyChanged(nameof(this.CodexSetupTitle));
        this.OnPropertyChanged(nameof(this.CodexSetupSummary));
        this.OnPropertyChanged(nameof(this.CodexSetupDetail));
        this.OnPropertyChanged(nameof(this.CodexSetupAuthenticationLabel));
        this.OnPropertyChanged(nameof(this.LanguageSearchPlaceholder));
        this.OnPropertyChanged(nameof(this.VisibleThreads));
        this.OnPropertyChanged(nameof(this.VisibleSkills));
        this.OnPropertyChanged(nameof(this.VisibleRemoteSkills));
        this.OnPropertyChanged(nameof(this.VisibleLanguageOptions));
        this.OnPropertyChanged(string.Empty);
    }

    private void OpenCodexConfig()
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            this._solutionContextService.OpenCodexConfig();
        });
    }

    private void OpenExtensionSettings()
    {
        this.SaveSettings();
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            this._solutionContextService.OpenPath(this._settingsStore.SettingsFilePath);
        });
    }

    private void OpenSettingsPanel()
    {
        this._pinRecentTasksPreview = false;
        this.IsRecentTasksPreviewExpanded = false;
        this.SelectedSettingsSection = string.Empty;
        this.ShowSettingsPanel = true;
        this.ShowHistoryPanel = false;
        this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
        this.OnPropertyChanged(nameof(this.RecentThreadsPreview));
        this.OnPropertyChanged(nameof(this.HasMoreThreadsThanPreview));
        this.OnPropertyChanged(nameof(this.IsRecentTasksPreviewExpanded));
        this.OnPropertyChanged(nameof(this.IsHistoryViewSelected));
        this.OnPropertyChanged(nameof(this.IsSettingsViewSelected));
    }

    private void OpenHistoryPanel()
    {
        this._hideRecentTasksPreview = false;
        this._pinRecentTasksPreview = true;
        this.IsRecentTasksPreviewExpanded = true;
        this.ShowHistoryPanel = false;
        this.ShowSettingsPanel = false;
        this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
        this.OnPropertyChanged(nameof(this.RecentThreadsPreview));
        this.OnPropertyChanged(nameof(this.HasMoreThreadsThanPreview));
        this.OnPropertyChanged(nameof(this.IsRecentTasksPreviewExpanded));
        this.OnPropertyChanged(nameof(this.IsHistoryViewSelected));
        this.OnPropertyChanged(nameof(this.IsSettingsViewSelected));
    }

    private void OpenCodexDocs()
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            this._solutionContextService.OpenUrl("https://openai.com/codex/get-started/");
        });
    }

    private void OpenKeyboardShortcuts()
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            this._solutionContextService.OpenUrl("https://learn.microsoft.com/visualstudio/ide/identifying-and-customizing-keyboard-shortcuts-in-visual-studio");
        });
    }

    private void OpenCodexSkillsFolder()
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            this._solutionContextService.OpenCodexSkillsDirectory();
        });
    }

    private void OpenPath(object? parameter)
    {
        if (parameter is not string path || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            this._solutionContextService.OpenPath(path);
        });
    }

    private void OpenReferencedPath(object? parameter)
    {
        if (parameter is not string reference || string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (this.TryResolveReferencedFile(reference, out ReferencedFile resolved))
            {
                this._solutionContextService.OpenFileInVisualStudio(resolved.Path, resolved.Line, resolved.Column);
            }
        });
    }

    private bool CanOpenReferencedPath(object? parameter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return parameter is string reference
            && this.TryResolveReferencedFile(reference, out _);
    }

    private bool TryResolveReferencedFile(string reference, out ReferencedFile resolved)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        resolved = default;
        string normalized = NormalizeReferencedFileText(reference);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            normalized = uri.LocalPath + uri.Fragment;
        }
        else if (Uri.TryCreate(normalized, UriKind.Absolute, out uri)
            && uri.Scheme.Length != 1)
        {
            return false;
        }

        normalized = DecodeReferencedFileText(normalized);
        string pathText = StripReferencedFilePosition(normalized, out int? line, out int? column);
        pathText = NormalizeReferencedPathText(DecodeReferencedFileText(pathText));
        foreach (string candidate in this.GetReferencedFileCandidates(pathText))
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    resolved = new ReferencedFile(fullPath, line, column);
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private IEnumerable<string> GetReferencedFileCandidates(string pathText)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(pathText))
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (Path.IsPathRooted(pathText))
        {
            if (seen.Add(pathText))
            {
                yield return pathText;
            }
            yield break;
        }

        string workingDirectory = (this.Settings.WorkingDirectory ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            string candidate = Path.Combine(workingDirectory, pathText);
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        string? solutionDirectory = this._solutionContextService.TryGetSolutionDirectory();
        if (!string.IsNullOrWhiteSpace(solutionDirectory)
            && !string.Equals(solutionDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(solutionDirectory, pathText);
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (string candidate in this.GetSolutionFileReferenceCandidates(pathText, solutionDirectory))
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string NormalizeReferencedFileText(string reference)
    {
        return (reference ?? string.Empty)
            .Trim()
            .Trim('`', '\'', '"', '<', '>')
            .TrimEnd('.', ',', ';');
    }

    private static string StripReferencedFilePosition(string reference, out int? line, out int? column)
    {
        line = null;
        column = null;
        reference = StripReferencedFileFragmentPosition(reference, out line, out column);

        Match match = Regex.Match(reference, @"^(?<path>.+?)(?::(?<line>\d+)(?::(?<column>\d+))?)$");
        if (!match.Success)
        {
            return reference;
        }

        line = int.TryParse(match.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLine)
            ? parsedLine
            : null;
        column = int.TryParse(match.Groups["column"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedColumn)
            ? parsedColumn
            : null;
        return match.Groups["path"].Value;
    }

    private IEnumerable<string> GetSolutionFileReferenceCandidates(string pathText, string? solutionDirectory)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        string normalizedReference = NormalizeReferenceForComparison(pathText);
        if (string.IsNullOrWhiteSpace(normalizedReference))
        {
            yield break;
        }

        foreach (var candidate in this._solutionContextService.GetSolutionFilePaths()
            .Select(path => new
            {
                Path = path,
                Score = ScoreSolutionFileReference(path, normalizedReference, solutionDirectory)
            })
            .Where(candidate => candidate.Score >= 0)
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase))
        {
            yield return candidate.Path;
        }
    }

    private static int ScoreSolutionFileReference(string filePath, string normalizedReference, string? solutionDirectory)
    {
        string normalizedFullPath = NormalizeReferenceForComparison(filePath);
        if (string.Equals(normalizedFullPath, normalizedReference, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            string solutionRoot = solutionDirectory!;
            if (filePath.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase))
            {
                string relativePath = filePath.Substring(solutionRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedRelativePath = NormalizeReferenceForComparison(relativePath);
                if (string.Equals(normalizedRelativePath, normalizedReference, StringComparison.OrdinalIgnoreCase))
                {
                    return 1;
                }
            }
        }

        bool includesDirectory = normalizedReference.IndexOf('/') >= 0;
        if (includesDirectory
            && normalizedFullPath.EndsWith("/" + normalizedReference, StringComparison.OrdinalIgnoreCase))
        {
            return 20 + Math.Max(0, normalizedFullPath.Length - normalizedReference.Length);
        }

        if (!includesDirectory
            && string.Equals(Path.GetFileName(filePath), normalizedReference, StringComparison.OrdinalIgnoreCase))
        {
            return 100 + filePath.Length;
        }

        return -1;
    }

    private static string StripReferencedFileFragmentPosition(string reference, out int? line, out int? column)
    {
        line = null;
        column = null;

        int hashIndex = reference.IndexOf('#');
        if (hashIndex < 0)
        {
            return reference;
        }

        string fragment = reference.Substring(hashIndex + 1);
        string path = reference.Substring(0, hashIndex);
        Match match = Regex.Match(fragment, @"^L?(?<line>\d+)(?:(?:C|:)(?<column>\d+))?", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            line = int.TryParse(match.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLine)
                ? parsedLine
                : null;
            column = int.TryParse(match.Groups["column"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedColumn)
                ? parsedColumn
                : null;
        }

        return path;
    }

    private static string DecodeReferencedFileText(string reference)
    {
        try
        {
            return Uri.UnescapeDataString(reference);
        }
        catch
        {
            return reference;
        }
    }

    private static string NormalizeReferencedPathText(string pathText)
    {
        string normalized = (pathText ?? string.Empty).Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal)
            || normalized.StartsWith(".\\", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        return normalized;
    }

    private static string NormalizeReferenceForComparison(string pathText)
    {
        string normalized = NormalizeReferencedPathText(pathText)
            .Replace('\\', '/')
            .Trim();

        while (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(1);
        }

        return normalized;
    }

    private readonly struct ReferencedFile
    {
        public ReferencedFile(string path, int? line, int? column)
        {
            this.Path = path;
            this.Line = line;
            this.Column = column;
        }

        public string Path { get; }

        public int? Line { get; }

        public int? Column { get; }
    }

    private void RefreshIntegrations()
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            await this.RefreshCodexStatusAsync().ConfigureAwait(false);
            if (!this.IsCodexReady)
            {
                this.ClearServerSurfaces();
                return;
            }

            await this.RefreshServerSurfacesAsync(forceSkillReload: true).ConfigureAwait(false);
        });
    }

    private void RefreshCodexStatus()
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(this.RefreshCodexStatusAsync);
    }

    private void RefreshModels(object? _)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => this.RefreshModelOptionsAsync(force: true));
    }

    private void AddCustomModel(object? _)
    {
        string model = NormalizeModelValue(string.IsNullOrWhiteSpace(this.CustomModelInput) ? this.SelectedModel : this.CustomModelInput);
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        this.AddCustomModelToSettings(model);
        this.SelectedModel = model;
        this.CustomModelInput = string.Empty;
        this.ReplaceModelOptions(MergeModelOptions(
            this.ModelOptions,
            CreateFallbackModelOptions(),
            this.Settings.CustomModels,
            this.SelectedModel));
        this.ModelRefreshStatus = "Modelo personalizado adicionado.";
        this.SaveSettings();
    }

    private void RemoveCustomModel(object? parameter)
    {
        string model = NormalizeModelValue(parameter as string);
        if (string.IsNullOrWhiteSpace(model)
            || string.Equals(model, this.SelectedModel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        this.Settings.CustomModels = this.Settings.CustomModels
            .Where(item => !string.Equals(item, model, StringComparison.OrdinalIgnoreCase))
            .ToList();
        this.ReplaceModelOptions(MergeModelOptions(
            this.ModelOptions,
            CreateFallbackModelOptions(),
            this.Settings.CustomModels,
            this.SelectedModel));
        this.SaveSettings();
    }

    private void RunCodexLogin(object? _)
    {
        string executablePath = this.GetLoginExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            this._codexEnvironmentService.LaunchLoginTerminal(executablePath);
        });
    }

    private void LogOutAndLogin(object? _)
    {
        string executablePath = this.GetLoginExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            try
            {
                this._codexEnvironmentService.DeleteAuthFile(this.CodexEnvironmentStatus.AuthFilePath);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                this._codexEnvironmentService.LaunchLoginTerminal(executablePath);
                await this.RefreshCodexStatusAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.AppendOutput("[" + this.Localization.OutputTagAuth + "] " + ex.Message + Environment.NewLine);
            }
        });
    }

    private void LogOut(object? _)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            try
            {
                this._codexEnvironmentService.DeleteAuthFile(this.CodexEnvironmentStatus.AuthFilePath);
                await this.RefreshCodexStatusAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.AppendOutput("[" + this.Localization.OutputTagAuth + "] " + ex.Message + Environment.NewLine);
            }
        });
    }

    private void CopyCodexInstallCommandText(object? _)
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Clipboard.SetText(this.CodexSetupInstallCommand);
        });
    }

    private void AddManagedMcp(object? parameter)
    {
        string transport = string.Equals(parameter as string, "url", StringComparison.OrdinalIgnoreCase)
            ? "url"
            : "stdio";

        this.ManagedMcpServers.Add(new CodexManagedMcpServer
        {
            Enabled = true,
            Name = transport == "url" ? this.Localization.ManagedMcpDefaultUrlName : this.Localization.ManagedMcpDefaultName,
            TransportType = transport
        });
    }

    private void RemoveManagedMcp(object? parameter)
    {
        if (parameter is CodexManagedMcpServer server)
        {
            _ = this.ManagedMcpServers.Remove(server);
        }
    }

    private void CreateSkill(object? _)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(this.CreateSkillAsync);
    }

    private async Task CreateSkillAsync()
    {
        if (!this.CanCreateSkill())
        {
            return;
        }

        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string skillFile = this._solutionContextService.CreateSkillTemplate(this.NewSkillName, this.NewSkillDescription);
            this.NewSkillName = string.Empty;
            this.NewSkillDescription = string.Empty;
            this._codexProcessService.InvalidateSkillsCache();
            this._solutionContextService.OpenPath(skillFile);
            await this.RefreshServerSurfacesAsync(forceSkillReload: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.AppendOutput("[" + this.Localization.OutputTagSkills + "] " + ex.Message + Environment.NewLine);
        }
    }

    private bool CanCreateSkill()
    {
        return SolutionContextService.IsValidSkillName(this.NewSkillName);
    }

    private void ToggleHistoryPanel()
    {
        bool nextState = !this._pinRecentTasksPreview || !this.ShowRecentTasksPreview;
        this._pinRecentTasksPreview = nextState;
        this._hideRecentTasksPreview = !nextState;
        if (!nextState)
        {
            this.IsRecentTasksPreviewExpanded = false;
        }

        this.ShowHistoryPanel = false;
        if (nextState)
        {
            this.ShowSettingsPanel = false;
        }

        this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
        this.OnPropertyChanged(nameof(this.RecentThreadsPreview));
        this.OnPropertyChanged(nameof(this.HasMoreThreadsThanPreview));
        this.OnPropertyChanged(nameof(this.IsRecentTasksPreviewExpanded));
        this.OnPropertyChanged(nameof(this.IsHistoryViewSelected));
        this.OnPropertyChanged(nameof(this.IsSettingsViewSelected));
    }

    private void ToggleSettingsPanel()
    {
        bool nextState = !this.ShowSettingsPanel;
        this._pinRecentTasksPreview = false;
        this.IsRecentTasksPreviewExpanded = false;
        this.ShowSettingsPanel = nextState;
        if (nextState)
        {
            this.ShowHistoryPanel = false;
            this.SelectedSettingsSection = string.Empty;
        }

        this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
        this.OnPropertyChanged(nameof(this.RecentThreadsPreview));
        this.OnPropertyChanged(nameof(this.HasMoreThreadsThanPreview));
        this.OnPropertyChanged(nameof(this.IsRecentTasksPreviewExpanded));
        this.OnPropertyChanged(nameof(this.IsHistoryViewSelected));
        this.OnPropertyChanged(nameof(this.IsSettingsViewSelected));
    }

    private void CloseSidebar()
    {
        this.ShowHistoryPanel = false;
        this.ShowSettingsPanel = false;
        this._pinRecentTasksPreview = false;
        this.IsRecentTasksPreviewExpanded = false;
        this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
        this.OnPropertyChanged(nameof(this.RecentThreadsPreview));
        this.OnPropertyChanged(nameof(this.HasMoreThreadsThanPreview));
        this.OnPropertyChanged(nameof(this.IsRecentTasksPreviewExpanded));
        this.OnPropertyChanged(nameof(this.IsHistoryViewSelected));
        this.OnPropertyChanged(nameof(this.IsSettingsViewSelected));
    }

    private void CloseSettingsDetail()
    {
        this.SelectedSettingsSection = string.Empty;
    }

    private void SelectSettingsSection(object? parameter)
    {
        if (parameter is not string section || string.IsNullOrWhiteSpace(section))
        {
            return;
        }

        if ((string.Equals(section, SettingsSectionCodexMenu, StringComparison.Ordinal)
                || string.Equals(section, SettingsSectionLanguage, StringComparison.Ordinal))
            && string.Equals(this.SelectedSettingsSection, section, StringComparison.Ordinal))
        {
            this.SelectedSettingsSection = string.Empty;
        }
        else
        {
            this.SelectedSettingsSection = section;
        }

        this.ShowSettingsPanel = true;
        this.ShowHistoryPanel = false;
        this.OnPropertyChanged(nameof(this.IsHistoryViewSelected));
        this.OnPropertyChanged(nameof(this.IsSettingsViewSelected));

        if (IsExternalSettingsSection(section))
        {
            this.PrepareExternalSettingsSection(section);
            CodexToolWindowManager.ShowSettingsToolWindow(section);
        }
    }

    public void EnsureExternalSettingsSection(string section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return;
        }

        this.SelectedSettingsSection = section;
        this.ShowSettingsPanel = false;
        this.ShowHistoryPanel = false;
        this.PrepareExternalSettingsSection(section);
    }

    private static bool IsExternalSettingsSection(string section)
    {
        return string.Equals(section, SettingsSectionAccount, StringComparison.Ordinal)
            || string.Equals(section, SettingsSectionCodex, StringComparison.Ordinal)
            || string.Equals(section, SettingsSectionMcp, StringComparison.Ordinal)
            || string.Equals(section, SettingsSectionSkills, StringComparison.Ordinal);
    }

    private void PrepareExternalSettingsSection(string section)
    {
        if (string.Equals(section, SettingsSectionMcp, StringComparison.Ordinal)
            || string.Equals(section, SettingsSectionSkills, StringComparison.Ordinal))
        {
            this.RefreshIntegrations();
            return;
        }

        if (string.Equals(section, SettingsSectionAccount, StringComparison.Ordinal)
            || string.Equals(section, SettingsSectionCodex, StringComparison.Ordinal))
        {
            this.RefreshCodexStatus();
        }
    }

    private void TogglePreferredMcp(object? parameter)
    {
        string serverName = parameter switch
        {
            CodexMcpServerSummary server => server.Name,
            string value => value,
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(serverName))
        {
            return;
        }

        List<string> preferredServers = this.Settings.PreferredMcpServers;
        int existingIndex = preferredServers.FindIndex(name => string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            preferredServers.RemoveAt(existingIndex);
        }
        else
        {
            preferredServers.Add(serverName);
        }

        this.SyncMcpShortcutSelections();
        this.OnPropertyChanged(nameof(this.HasPreferredMcpServers));
        this.OnPropertyChanged(nameof(this.PreferredMcpServers));
        this.SaveSettings();
    }

    private void SelectReasoningEffort(object? parameter)
    {
        if (parameter is string value)
        {
            this.SelectedReasoningEffort = value;
        }
    }

    private void SelectVerbosity(object? parameter)
    {
        if (parameter is string value)
        {
            this.SelectedVerbosity = value;
        }
    }

    private void SelectApprovalPolicy(object? parameter)
    {
        if (parameter is string value)
        {
            this.SelectedApprovalPolicy = value;
        }
    }

    private void SelectSandboxMode(object? parameter)
    {
        if (parameter is string value)
        {
            this.SelectedSandboxMode = value;
        }
    }

    private void SelectLanguage(object? parameter)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (parameter is string value)
        {
            this.SelectedLanguageTag = value;
        }
    }

    private void ToggleSkillEnabled(object? parameter)
    {
        if (parameter is not CodexSkillSummary skill)
        {
            return;
        }

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => this.ToggleSkillEnabledAsync(skill));
    }

    private async Task ToggleSkillEnabledAsync(CodexSkillSummary skill)
    {
        bool requestedValue = skill.IsEnabled;

        try
        {
            bool effectiveValue = await this._codexProcessService.SetSkillEnabledAsync(this.Settings, skill.Path, requestedValue, CancellationToken.None).ConfigureAwait(false);
            RunOnUiThread(() => skill.IsEnabled = effectiveValue);
            this._codexProcessService.InvalidateSkillsCache();
            await this.RefreshServerSurfacesAsync(forceSkillReload: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunOnUiThread(() => skill.IsEnabled = !requestedValue);
            this.AppendOutput("[" + this.Localization.OutputTagSkills + "] " + ex.Message + Environment.NewLine);
        }
    }

    private void InstallRemoteSkill(object? parameter)
    {
        if (parameter is not CodexRemoteSkillSummary skill)
        {
            return;
        }

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => this.InstallRemoteSkillAsync(skill));
    }

    private async Task InstallRemoteSkillAsync(CodexRemoteSkillSummary skill)
    {
        try
        {
            string? path = await this._codexProcessService.InstallRemoteSkillAsync(this.Settings, skill.Id, CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
            {
                this._solutionContextService.OpenPath(path);
            }

            this._codexProcessService.InvalidateSkillsCache();
            await this.RefreshServerSurfacesAsync(forceSkillReload: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.AppendOutput("[" + this.Localization.OutputTagRemoteSkills + "] " + ex.Message + Environment.NewLine);
        }
    }

    private async Task InitializeSafeAsync()
    {
        try
        {
            await this.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = ActivityLog.TryLogError("CodexVsix", this.Localization.AsyncPanelInitializeLogMessage + Environment.NewLine + ex);
            this.AppendOutput("[" + this.Localization.OutputTagInit + "] " + ex.Message + Environment.NewLine);
            this.CodexEnvironmentStatus = new CodexEnvironmentStatus
            {
                Stage = CodexSetupStage.Error,
                ErrorDetail = ex.Message
            };
            this.ClearServerSurfaces();
        }
    }

    private async Task InitializeAsync()
    {
        this.Settings.CurrentThreadId = string.Empty;
        this.Settings.LastThreadWorkingDirectory = this.Settings.WorkingDirectory;
        this._codexProcessService.ResetThread();

        await this.RefreshCodexStatusAsync().ConfigureAwait(false);
        if (!this.IsCodexReady)
        {
            this.ClearServerSurfaces();
            return;
        }

        await this.RefreshModelOptionsAsync().ConfigureAwait(false);
        await this.RefreshThreadsAsync(null).ConfigureAwait(false);
        await this.RefreshServerSurfacesAsync(forceSkillReload: true).ConfigureAwait(false);
        this._hasLoadedStartupSurfaces = true;
    }

    private async Task RefreshCodexStatusAsync()
    {
        try
        {
            CodexEnvironmentStatus status = await this._codexEnvironmentService.InspectAsync(this.Settings, CancellationToken.None).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                this.CodexEnvironmentStatus = status;
                this.HasCompletedEnvironmentCheck = true;
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                this.CodexEnvironmentStatus = new CodexEnvironmentStatus
                {
                    Stage = CodexSetupStage.Error,
                    ConfiguredExecutablePath = this.Settings.CodexExecutablePath ?? string.Empty,
                    AuthFilePath = this._codexEnvironmentService.GetAuthFilePath(this.Settings.EnvironmentVariables),
                    ErrorDetail = ex.Message
                };
                this.HasCompletedEnvironmentCheck = true;
            });
        }
    }

    private void ClearServerSurfaces()
    {
        RunOnUiThread(() =>
        {
            this.Apps.Clear();
            this.McpServers.Clear();
            this.Skills.Clear();
            this.RemoteSkills.Clear();
            this.DetectedPromptSkills.Clear();
            this.RateLimitSummary = new CodexRateLimitSummary();
            this.PromptDisplayText = string.Empty;
            this._hasLoadedStartupSurfaces = false;
            this.OnPropertyChanged(nameof(this.HasDetectedPromptSkills));
            this.OnPropertyChanged(nameof(this.HasRemoteSkills));
            this.OnPropertyChanged(nameof(this.VisibleSkills));
            this.OnPropertyChanged(nameof(this.VisibleRemoteSkills));
        });
    }

    public void EnsureToolWindowStartupState()
    {
        if (this._isToolWindowStartupRefreshInProgress)
        {
            return;
        }

        // Rehydrate status/surfaces when the tool window is shown again, but avoid
        // repeating expensive startup calls once the current session is already loaded.
        if (this.HasCompletedEnvironmentCheck && (this.IsCodexReady ? this._hasLoadedStartupSurfaces : true))
        {
            return;
        }

        this._isToolWindowStartupRefreshInProgress = true;
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            try
            {
                await this.RefreshCodexStatusAsync().ConfigureAwait(false);
                if (!this.IsCodexReady)
                {
                    this.ClearServerSurfaces();
                    return;
                }

                if (this._hasLoadedStartupSurfaces)
                {
                    return;
                }

                await this.RefreshModelOptionsAsync().ConfigureAwait(false);
                await this.RefreshThreadsAsync(null).ConfigureAwait(false);
                await this.RefreshServerSurfacesAsync(forceSkillReload: false).ConfigureAwait(false);
                this._hasLoadedStartupSurfaces = true;
            }
            catch (Exception ex)
            {
                this.AppendOutput("[" + this.Localization.OutputTagInit + "] " + ex.Message + Environment.NewLine);
            }
            finally
            {
                RunOnUiThread(() => this._isToolWindowStartupRefreshInProgress = false);
            }
        });
    }

    private async Task RefreshModelOptionsAsync(bool force = false)
    {
        if (this.IsRefreshingModels && !force)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            this.IsRefreshingModels = true;
            this.ModelRefreshStatus = "Atualizando modelos...";
        });

        try
        {
            if (!this.IsCodexReady)
            {
                RunOnUiThread(() =>
                {
                    if (this.ModelOptions.Count == 0)
                    {
                        this.ReplaceModelOptions(MergeModelOptions(
                            Enumerable.Empty<SelectionOption>(),
                            CreateFallbackModelOptions(),
                            this.Settings.CustomModels,
                            this.SelectedModel));
                    }

                    this.ModelRefreshStatus = "Codex não está pronto. Mantendo modelos locais.";
                });
                return;
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            IReadOnlyList<SelectionOption> models = await this._codexProcessService.ListModelsAsync(this.Settings, timeout.Token, this.Settings.IncludeHiddenModels).ConfigureAwait(false);
            if (models.Count == 0)
            {
                RunOnUiThread(() =>
                {
                    if (this.ModelOptions.Count == 0)
                    {
                        this.ReplaceModelOptions(MergeModelOptions(
                            Enumerable.Empty<SelectionOption>(),
                            CreateFallbackModelOptions(),
                            this.Settings.CustomModels,
                            this.SelectedModel));
                    }

                    this.ModelRefreshStatus = "Nenhum modelo remoto retornado. Mantendo lista atual.";
                });
                return;
            }

            RunOnUiThread(() =>
            {
                this.ReplaceModelOptions(MergeModelOptions(
                    models,
                    CreateFallbackModelOptions(),
                    this.Settings.CustomModels,
                    this.SelectedModel));
                this.ModelRefreshStatus = "Modelos atualizados pelo Codex.";
                this.OnPropertyChanged(nameof(this.SelectedModelLabel));
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                if (this.ModelOptions.Count == 0)
                {
                    this.ReplaceModelOptions(MergeModelOptions(
                        Enumerable.Empty<SelectionOption>(),
                        CreateFallbackModelOptions(),
                        this.Settings.CustomModels,
                        this.SelectedModel));
                }

                this.ModelRefreshStatus = "Falha ao atualizar modelos. Mantendo lista atual.";
            });
            this.AppendOutput(this.Localization.LoadModelsErrorPrefix + ex.Message + Environment.NewLine);
        }
        finally
        {
            RunOnUiThread(() => this.IsRefreshingModels = false);
        }
    }

    private static IReadOnlyList<SelectionOption> MergeModelOptions(
        IEnumerable<SelectionOption> remoteModels,
        IEnumerable<SelectionOption> fallbackModels,
        IEnumerable<string> customModels,
        string selectedModel)
    {
        List<SelectionOption> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<SelectionOption> fallbackList = NormalizeOptions(fallbackModels).ToList();
        HashSet<string> fallbackValues = new(fallbackList.Select(option => option.Value), StringComparer.OrdinalIgnoreCase);
        string selected = NormalizeModelValue(selectedModel);

        void AddOption(string? label, string? value)
        {
            value = NormalizeModelValue(value);
            if (!string.IsNullOrWhiteSpace(selected) && string.Equals(value, selected, StringComparison.OrdinalIgnoreCase))
            {
                value = selected;
            }

            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
            {
                return;
            }

            label = string.IsNullOrWhiteSpace(label) ? value : label!.Trim();
            result.Add(new SelectionOption(label, value));
        }

        foreach (SelectionOption option in NormalizeOptions(remoteModels))
        {
            AddOption(option.Label, option.Value);
        }

        foreach (string model in customModels ?? Enumerable.Empty<string>())
        {
            string value = NormalizeModelValue(model);
            if (string.IsNullOrWhiteSpace(value) || fallbackValues.Contains(value))
            {
                continue;
            }

            AddOption(value + " (custom)", value);
        }

        if (!string.IsNullOrWhiteSpace(selected) && !fallbackValues.Contains(selected))
        {
            AddOption(selected + " (custom)", selected);
        }

        foreach (SelectionOption? option in fallbackList)
        {
            AddOption(option.Label, option.Value);
        }

        if (result.Count == 0)
        {
            foreach (SelectionOption option in CreateFallbackModelOptions())
            {
                AddOption(option.Label, option.Value);
            }
        }

        return result;
    }

    private static IEnumerable<SelectionOption> NormalizeOptions(IEnumerable<SelectionOption>? options)
    {
        return options?
            .Where(option => option is not null && !string.IsNullOrWhiteSpace(option.Value))
            .Select(option =>
            {
                string value = NormalizeModelValue(option.Value);
                string label = string.IsNullOrWhiteSpace(option.Label) ? value : option.Label.Trim();
                return new SelectionOption(label, value);
            })
            .Where(option => !string.IsNullOrWhiteSpace(option.Value))
            ?? Enumerable.Empty<SelectionOption>();
    }

    private void ReplaceModelOptions(IEnumerable<SelectionOption> options)
    {
        this.ModelOptions.Clear();
        foreach (SelectionOption option in options)
        {
            this.ModelOptions.Add(option);
        }

        this.EnsureSelectedModelOption(this.SelectedModel);
        this.OnPropertyChanged(nameof(this.SelectedModelLabel));
    }

    private void EnsureSelectedModelOption(string? model)
    {
        string value = NormalizeModelValue(model);
        if (string.IsNullOrWhiteSpace(value)
            || this.ModelOptions.Any(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        this.ModelOptions.Add(new SelectionOption(value + " (custom)", value));
    }

    private void AddCustomModelToSettings(string model)
    {
        model = NormalizeModelValue(model);
        if (string.IsNullOrWhiteSpace(model)
            || this.Settings.CustomModels.Any(item => string.Equals(item, model, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        this.Settings.CustomModels.Add(model);
    }

    private void PersistSelectedModelIfCustom()
    {
        string selected = NormalizeModelValue(this.SelectedModel);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        if (CreateFallbackModelOptions().Any(option => string.Equals(option.Value, selected, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SelectionOption? existingOption = this.ModelOptions.FirstOrDefault(option => string.Equals(option.Value, selected, StringComparison.OrdinalIgnoreCase));
        if (existingOption is not null && !existingOption.Label.EndsWith(" (custom)", StringComparison.Ordinal))
        {
            return;
        }

        this.AddCustomModelToSettings(selected);
    }

    private async Task RefreshThreadsAsync(string? preferredThreadId = null)
    {
        try
        {
            IReadOnlyList<CodexThreadSummary> threads = await this._codexProcessService.ListThreadsAsync(this.Settings, CancellationToken.None).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                string selectedThreadId = preferredThreadId ?? this.SelectedThread?.ThreadId ?? this.Settings.CurrentThreadId;
                string editingThreadId = this.EditingThreadId;
                this.Threads.Clear();
                foreach (CodexThreadSummary thread in threads)
                {
                    this.Threads.Add(thread);
                }

                this._suppressThreadSelection = true;
                this.SelectedThread = this.Threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, selectedThreadId, StringComparison.Ordinal));
                this._suppressThreadSelection = false;

                if (!string.IsNullOrWhiteSpace(editingThreadId)
                    && !this.Threads.Any(thread => string.Equals(thread.ThreadId, editingThreadId, StringComparison.Ordinal)))
                {
                    this.EditingThreadId = string.Empty;
                    this.RenameThreadName = this.SelectedThread?.Title ?? string.Empty;
                }
            });
        }
        catch (Exception ex)
        {
            this.AppendOutput(this.Localization.LoadTopicsErrorPrefix + ex.Message + Environment.NewLine);
        }
    }

    private async Task RefreshServerSurfacesAsync(bool forceSkillReload = false)
    {
        if (!this.IsCodexReady)
        {
            this.ClearServerSurfaces();
            return;
        }

        try
        {
            Task<IReadOnlyList<CodexAppSummary>> appsTask = this._codexProcessService.ListAppsAsync(this.Settings, CancellationToken.None);
            Task<IReadOnlyList<CodexMcpServerSummary>> mcpTask = this._codexProcessService.ListMcpServersAsync(this.Settings, CancellationToken.None);
            Task<IReadOnlyList<CodexSkillSummary>> skillsTask = this._codexProcessService.ListSkillsAsync(this.Settings, CancellationToken.None, forceSkillReload);
            Task<IReadOnlyList<CodexRemoteSkillSummary>> remoteSkillsTask = this.GetRemoteSkillsSafeAsync();
            Task<CodexRateLimitSummary> rateLimitsTask = this.GetRateLimitsSafeAsync();
            await Task.WhenAll(appsTask, mcpTask, skillsTask, remoteSkillsTask, rateLimitsTask).ConfigureAwait(false);

            RunOnUiThread(() =>
            {
                this.Apps.Clear();
                foreach (CodexAppSummary app in appsTask.Result)
                {
                    this.Apps.Add(app);
                }

                this.McpServers.Clear();
                foreach (CodexMcpServerSummary server in mcpTask.Result)
                {
                    this.McpServers.Add(server);
                }
                this.SyncMcpShortcutSelections();

                this.Skills.Clear();
                foreach (CodexSkillSummary skill in skillsTask.Result)
                {
                    this.Skills.Add(skill);
                }

                this.RemoteSkills.Clear();
                foreach (CodexRemoteSkillSummary skill in remoteSkillsTask.Result)
                {
                    if (this.Skills.Any(installed => string.Equals(installed.Name, skill.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    this.RemoteSkills.Add(skill);
                }

                this.RateLimitSummary = rateLimitsTask.Result;
                this.OnPropertyChanged(nameof(this.VisibleSkills));
                this.OnPropertyChanged(nameof(this.VisibleRemoteSkills));
                this.OnPropertyChanged(nameof(this.HasRemoteSkills));
            });
        }
        catch (Exception ex)
        {
            this.AppendOutput("[" + this.Localization.OutputTagServer + "] " + ex.Message + Environment.NewLine);
        }
    }

    private async Task<IReadOnlyList<CodexRemoteSkillSummary>> GetRemoteSkillsSafeAsync()
    {
        try
        {
            return await this._codexProcessService.ListRemoteSkillsAsync(this.Settings, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<CodexRemoteSkillSummary>();
        }
    }

    private async Task<CodexRateLimitSummary> GetRateLimitsSafeAsync()
    {
        try
        {
            return await this._codexProcessService.GetAccountRateLimitsAsync(this.Settings, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return new CodexRateLimitSummary();
        }
    }

    private async Task OpenThreadAsync(string threadId)
    {
        if (this.IsBusy || string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        long conversationStateVersion = this.BeginConversationStateChange();

        try
        {
            CodexThreadConversation? conversation = await this._codexProcessService.LoadThreadConversationAsync(this.Settings, threadId, CancellationToken.None).ConfigureAwait(false);
            if (conversation is null || !this.IsConversationStateCurrent(conversationStateVersion))
            {
                return;
            }

            this.Settings.CurrentThreadId = conversation.Thread.ThreadId;
            this.Settings.LastThreadWorkingDirectory = this.Settings.WorkingDirectory;
            this.SaveSettings();

            RunOnUiThread(() =>
            {
                this._currentAssistantMessage = null;
                this._currentPlanMessage = null;
                this.Messages.ReplaceAll(conversation.Messages.Select(this.CreateDisplayMessage));

                this.Output = string.Empty;
                this.CloseSidebar();
            });

            await this.RefreshThreadsAsync(conversation.Thread.ThreadId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (this.IsConversationStateCurrent(conversationStateVersion))
            {
                this.AppendOutput(this.Localization.LoadTopicsErrorPrefix + ex.Message + Environment.NewLine);
            }
        }
    }

    private void StartNewThread()
    {
        if (this.IsStopping)
        {
            return;
        }

        if (this.IsBusy)
        {
            this.Cancel();
        }

        _ = this.BeginConversationStateChange();
        this._hideRecentTasksPreview = true;
        this._pinRecentTasksPreview = false;
        this.IsRecentTasksPreviewExpanded = false;
        this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
        this.OnPropertyChanged(nameof(this.RecentThreadsPreview));
        this.OnPropertyChanged(nameof(this.HasMoreThreadsThanPreview));
        this.OnPropertyChanged(nameof(this.IsRecentTasksPreviewExpanded));
        _ = (this._approvalDecisionTcs?.TrySetResult(JValue.CreateString("cancel")));
        this._approvalDecisionTcs = null;
        this.CurrentApprovalPrompt = null;
        this._currentAssistantMessage = null;
        this._currentPlanMessage = null;
        this.Settings.CurrentThreadId = string.Empty;
        this.Settings.LastThreadWorkingDirectory = this.Settings.WorkingDirectory;
        this._codexProcessService.ResetThread();
        this.SaveSettings();

        RunOnUiThread(() =>
        {
            this._suppressThreadSelection = true;
            this.SelectedThread = null;
            this._suppressThreadSelection = false;
            this.RenameThreadName = string.Empty;
            this.Messages.Clear();
            this.Output = string.Empty;
            this.UpdateContextEstimate();
            this.CloseSidebar();
        });

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => this.RefreshThreadsAsync(null));
    }

    private void DismissRecentTasksPreview()
    {
        if (this._hideRecentTasksPreview)
        {
            return;
        }

        this._hideRecentTasksPreview = true;
        this._pinRecentTasksPreview = false;
        this.IsRecentTasksPreviewExpanded = false;
        this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
        this.OnPropertyChanged(nameof(this.RecentThreadsPreview));
        this.OnPropertyChanged(nameof(this.HasMoreThreadsThanPreview));
        this.OnPropertyChanged(nameof(this.IsRecentTasksPreviewExpanded));
        this.OnPropertyChanged(nameof(this.IsHistoryViewSelected));
    }

    private void BeginRenameThread(object? parameter)
    {
        if (parameter is not CodexThreadSummary thread)
        {
            return;
        }

        this.EditingThreadId = thread.ThreadId;
        this.RenameThreadName = thread.Title;
    }

    private bool CanRenameThread(object? parameter)
    {
        return !this.IsBusy
            && !string.IsNullOrWhiteSpace(this.RenameThreadName)
            && this.ResolveThreadForRename(parameter) is not null;
    }

    private void RenameSelectedThread(object? parameter)
    {
        CodexThreadSummary? selectedThread = this.ResolveThreadForRename(parameter);
        string? newName = this.RenameThreadName?.Trim();
        if (selectedThread is null || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            try
            {
                await this._codexProcessService.RenameThreadAsync(this.Settings, selectedThread.ThreadId, newName!, CancellationToken.None).ConfigureAwait(false);
                await this.RefreshThreadsAsync(selectedThread.ThreadId).ConfigureAwait(false);
                RunOnUiThread(() =>
                {
                    this.EditingThreadId = string.Empty;
                    this.RenameThreadName = this.SelectedThread?.Title ?? string.Empty;
                });
            }
            catch (Exception ex)
            {
                this.AppendOutput(this.Localization.LoadTopicsErrorPrefix + ex.Message + Environment.NewLine);
            }
        });
    }

    private void CancelRenameThread(object? parameter)
    {
        string editingThreadId = this.EditingThreadId;
        if (string.IsNullOrWhiteSpace(editingThreadId))
        {
            return;
        }

        if (parameter is CodexThreadSummary thread
            && !string.Equals(thread.ThreadId, editingThreadId, StringComparison.Ordinal))
        {
            return;
        }

        this.EditingThreadId = string.Empty;
        this.RenameThreadName = this.SelectedThread?.Title ?? string.Empty;
    }

    private CodexThreadSummary? ResolveThreadForRename(object? parameter)
    {
        if (parameter is CodexThreadSummary thread)
        {
            return thread;
        }

        if (!string.IsNullOrWhiteSpace(this.EditingThreadId))
        {
            return this.Threads.FirstOrDefault(threadSummary => string.Equals(threadSummary.ThreadId, this.EditingThreadId, StringComparison.Ordinal));
        }

        return this.SelectedThread;
    }

    private void DeleteThread(object? parameter)
    {
        if (this.IsBusy || parameter is not CodexThreadSummary thread)
        {
            return;
        }

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            try
            {
                await this._codexProcessService.DeleteThreadAsync(this.Settings, thread.ThreadId, CancellationToken.None).ConfigureAwait(false);

                if (string.Equals(this.Settings.CurrentThreadId, thread.ThreadId, StringComparison.Ordinal))
                {
                    _ = this.BeginConversationStateChange();
                    this.Settings.CurrentThreadId = string.Empty;
                    this.Settings.LastThreadWorkingDirectory = this.Settings.WorkingDirectory;
                    this._codexProcessService.ResetThread();
                    this.SaveSettings();

                    RunOnUiThread(() =>
                    {
                        this._suppressThreadSelection = true;
                        this.SelectedThread = null;
                        this._suppressThreadSelection = false;
                        this.EditingThreadId = string.Empty;
                        this.RenameThreadName = string.Empty;
                        this.Messages.Clear();
                        this.Output = string.Empty;
                    });
                }

                await this.RefreshThreadsAsync(this.Settings.CurrentThreadId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.AppendOutput(this.Localization.LoadTopicsErrorPrefix + ex.Message + Environment.NewLine);
            }
        });
    }

    private void HandleThreadCatalogChanged()
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => this.RefreshThreadsAsync(this.Settings.CurrentThreadId));
    }

    private static IEnumerable<SelectionOption> CreateFallbackModelOptions()
    {
        return new[]
        {
            new SelectionOption("GPT-5.4", "gpt-5.4"),
            new SelectionOption("GPT-5.2 Codex", "gpt-5.2-codex"),
            new SelectionOption("GPT-5.2", "gpt-5.2"),
            new SelectionOption("GPT-5", "gpt-5")
        };
    }

    private void RefreshMentions()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        string mention = ExtractCurrentMention(this.PromptEditorText);
        this.CurrentMentionQuery = mention;
        this.MentionSuggestions.Clear();

        if (string.IsNullOrWhiteSpace(mention))
        {
            this.OnPropertyChanged(nameof(this.HasMentionSuggestions));
            return;
        }

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            foreach (string file in this._solutionContextService.FindSolutionFiles(mention))
            {
                this.MentionSuggestions.Add(file);
            }

            this.OnPropertyChanged(nameof(this.HasMentionSuggestions));
        });
    }

    private void RefreshDetectedPromptSkills()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        this.ApplyRawPrompt(this._prompt);
    }

    private void InsertSelectedMention()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(this.SelectedMention))
        {
            return;
        }

        string mention = ExtractCurrentMention(this.PromptEditorText);
        if (string.IsNullOrWhiteSpace(mention))
        {
            return;
        }

        string suffix = "@" + mention;
        int idx = this.PromptEditorText.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return;
        }

        this.PromptEditorText = this.PromptEditorText.Substring(0, idx) + "@" + this.SelectedMention + " " + this.PromptEditorText.Substring(idx + suffix.Length);
        this.MentionSuggestions.Clear();
        this.CurrentMentionQuery = string.Empty;
    }

    private void ReuseHistoryPrompt()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        string? selectedHistoryPrompt = this.SelectedHistoryPrompt;
        if (!string.IsNullOrWhiteSpace(selectedHistoryPrompt))
        {
            this.Prompt = selectedHistoryPrompt!;
            this.ShowHistoryPanel = false;
        }
    }

    private async Task<JToken?> HandleApprovalRequestAsync(CodexApprovalRequest request)
    {
        ApprovalPromptViewModel prompt = this.BuildApprovalPrompt(request);
        TaskCompletionSource<JToken?> decisionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        RunOnUiThread(() =>
        {
            this._approvalDecisionTcs = decisionTcs;
            this.CurrentApprovalPrompt = prompt;
        });

        return await decisionTcs.Task.ConfigureAwait(false);
    }

    private async Task<JObject?> HandleUserInputRequestAsync(CodexUserInputRequest request)
    {
        UserInputPromptViewModel prompt = this.BuildUserInputPrompt(request);
        TaskCompletionSource<JObject?> decisionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        RunOnUiThread(() =>
        {
            this._userInputDecisionTcs = decisionTcs;
            this.CurrentUserInputPrompt = prompt;
        });

        return await decisionTcs.Task.ConfigureAwait(false);
    }

    private ApprovalPromptViewModel BuildApprovalPrompt(CodexApprovalRequest request)
    {
        ApprovalPromptViewModel prompt = new()
        {
            Title = string.Equals(request.Method, "item/fileChange/requestApproval", StringComparison.Ordinal)
                ? this.Localization.ApprovalFileChangeTitle
                : this.Localization.ApprovalCommandTitle,
            Subtitle = request.ProposedExecpolicyLabel,
            Command = request.Command,
            WorkingDirectory = request.WorkingDirectory,
            Reason = request.Reason,
            GrantRoot = request.GrantRoot
        };

        foreach (CodexApprovalOption option in request.Options)
        {
            bool isDanger = string.Equals(option.Key, "decline", StringComparison.Ordinal) || string.Equals(option.Key, "cancel", StringComparison.Ordinal);
            bool isPrimary = string.Equals(option.Key, "accept", StringComparison.Ordinal) || string.Equals(option.Key, "acceptForSession", StringComparison.Ordinal);
            prompt.Options.Add(new ApprovalOptionViewModel(
                this.Localization.GetApprovalOptionLabel(option.Key),
                option.Decision.DeepClone(),
                isPrimary,
                isDanger));
        }

        if (prompt.Options.Count == 0)
        {
            prompt.Options.Add(new ApprovalOptionViewModel(this.Localization.ApprovalDecline, JValue.CreateString("decline"), isDanger: true));
        }

        return prompt;
    }

    private void ResolveApproval(object? parameter)
    {
        if (parameter is not ApprovalOptionViewModel option)
        {
            return;
        }

        TaskCompletionSource<JToken?>? tcs = this._approvalDecisionTcs;
        this.CurrentApprovalPrompt = null;
        this._approvalDecisionTcs = null;
        _ = (tcs?.TrySetResult(option.Decision.DeepClone()));
    }

    private UserInputPromptViewModel BuildUserInputPrompt(CodexUserInputRequest request)
    {
        UserInputPromptViewModel prompt = new()
        {
            Title = this.Localization.UserInputTitle
        };

        foreach (CodexUserInputQuestion question in request.Questions)
        {
            UserInputQuestionViewModel item = new()
            {
                Header = question.Header,
                Id = question.Id,
                Question = question.Question,
                IsSecret = question.IsSecret,
                AcceptsText = question.IsOther || question.Options.Count == 0
            };

            foreach (CodexUserInputOption option in question.Options)
            {
                item.Options.Add(new SelectionOption(option.Label, option.Label));
            }

            if (!item.AcceptsText && item.Options.Count > 0)
            {
                item.SelectedOptionValue = item.Options[0].Value;
            }

            prompt.Questions.Add(item);
        }

        return prompt;
    }

    private void HandleManagedMcpServersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.OnPropertyChanged(nameof(this.HasManagedMcpServers));
    }

    private void HandleRateLimitsUpdated(CodexRateLimitSummary summary)
    {
        RunOnUiThread(() => this.RateLimitSummary = summary);
    }

    private void HandleAccountUpdated()
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            if (!this.IsCodexReady)
            {
                return;
            }

            await this.RefreshServerSurfacesAsync(forceSkillReload: false).ConfigureAwait(false);
        });
    }

    private void HandleThreadsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.OnPropertyChanged(nameof(this.HasThreads));
        this.OnPropertyChanged(nameof(this.VisibleThreads));
        this.OnPropertyChanged(nameof(this.HasVisibleThreads));
        this.OnPropertyChanged(nameof(this.RecentThreadsPreview));
        this.OnPropertyChanged(nameof(this.HasMoreThreadsThanPreview));
        this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
    }

    private void HandleMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.OnPropertyChanged(nameof(this.ShowRecentTasksPreview));
    }

    private void HandleMcpServersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.SyncMcpShortcutSelections();
        this.OnPropertyChanged(nameof(this.HasDetectedMcpServers));
        this.OnPropertyChanged(nameof(this.HasPreferredMcpServers));
        this.OnPropertyChanged(nameof(this.PreferredMcpServers));
    }

    private void HandleSkillsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RunOnUiThread(() =>
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            this.OnPropertyChanged(nameof(this.HasSkills));
            this.RefreshDetectedPromptSkills();
            this.RefreshDisplayedUserMessages();
            this.OnPropertyChanged(nameof(this.VisibleSkills));
        });
    }

    private void SyncMcpShortcutSelections()
    {
        HashSet<string> selectedServers = new(this.Settings.PreferredMcpServers ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (CodexMcpServerSummary server in this.McpServers)
        {
            server.IsShortcutSelected = selectedServers.Contains(server.Name);
        }
    }

    private static CodexManagedMcpServer CloneManagedMcpServer(CodexManagedMcpServer? server)
    {
        if (server is null)
        {
            return new CodexManagedMcpServer();
        }

        return new CodexManagedMcpServer
        {
            Enabled = server.Enabled,
            Name = server.Name ?? string.Empty,
            TransportType = string.IsNullOrWhiteSpace(server.TransportType) ? "stdio" : server.TransportType,
            Command = server.Command ?? string.Empty,
            Arguments = server.Arguments ?? string.Empty,
            Url = server.Url ?? string.Empty
        };
    }

    private ChatMessage CreateDisplayMessage(bool isUser, string text, bool isEvent = false, string? title = null, string? detail = null, bool? supportsMarkdownText = null, bool supportsMarkdownDetail = false)
    {
        ChatMessage message = new(isUser, text, isEvent, title, detail, supportsMarkdownText, supportsMarkdownDetail);
        this.DecorateUserMessageDisplay(message);
        return message;
    }

    private ChatMessage CreateDisplayMessage(ChatMessage source)
    {
        ChatMessage message = this.CreateDisplayMessage(
            source.IsUser,
            source.Text,
            source.IsEvent,
            source.Title,
            source.Detail,
            source.SupportsMarkdownText,
            source.SupportsMarkdownDetail);
        message.RenderMarkdown = source.RenderMarkdown;
        return message;
    }

    private void RefreshDisplayedUserMessages()
    {
        RunOnUiThread(() =>
        {
            foreach (ChatMessage message in this.Messages)
            {
                this.DecorateUserMessageDisplay(message);
            }
        });
    }

    private void DecorateUserMessageDisplay(ChatMessage message)
    {
        if (!message.IsUser || message.IsEvent)
        {
            message.ApplyPromptSkillDisplay(System.Array.Empty<string>(), message.Text);
            return;
        }

        HashSet<string> availableSkillNames = new(
            this.Skills.Where(skill => skill.IsEnabled && !string.IsNullOrWhiteSpace(skill.Name))
                .Select(skill => skill.Name!),
            StringComparer.OrdinalIgnoreCase);

        (IReadOnlyList<string> SkillNames, string DisplayText) formattedPrompt = FormatPromptSkillDisplay(message.Text, availableSkillNames);
        message.ApplyPromptSkillDisplay(formattedPrompt.SkillNames, formattedPrompt.DisplayText);
    }

    private void ResolveUserInput()
    {
        UserInputPromptViewModel? prompt = this.CurrentUserInputPrompt;
        TaskCompletionSource<JObject?>? tcs = this._userInputDecisionTcs;
        if (prompt is null)
        {
            return;
        }

        JObject answers = [];
        foreach (UserInputQuestionViewModel question in prompt.Questions)
        {
            string? answer = question.ResolvedAnswer;
            if (string.IsNullOrWhiteSpace(answer))
            {
                continue;
            }

            answers[question.Id] = new JObject
            {
                ["answers"] = new JArray(answer)
            };
        }

        this.CurrentUserInputPrompt = null;
        this._userInputDecisionTcs = null;
        _ = (tcs?.TrySetResult(new JObject
        {
            ["answers"] = answers
        }));
    }

    internal void DismissUserInputPrompt()
    {
        TaskCompletionSource<JObject?>? tcs = this._userInputDecisionTcs;
        this.CurrentUserInputPrompt = null;
        this._userInputDecisionTcs = null;
        _ = (tcs?.TrySetResult(new JObject
        {
            ["answers"] = new JObject()
        }));
    }

    private void AddPromptToHistory(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        _ = this.Settings.PromptHistory.RemoveAll(p => string.Equals(p, prompt, StringComparison.Ordinal));
        this.Settings.PromptHistory.Add(prompt);
        while (this.Settings.PromptHistory.Count > 50)
        {
            this.Settings.PromptHistory.RemoveAt(0);
        }

        this.PromptHistory.Clear();
        foreach (string item in this.GetRecentPromptHistory())
        {
            this.PromptHistory.Add(item);
        }
    }

    private System.Collections.Generic.IEnumerable<string> GetRecentPromptHistory()
    {
        List<string> promptHistory = this.Settings.PromptHistory ?? [];
        int skip = Math.Max(0, promptHistory.Count - 30);
        return promptHistory.Skip(skip).Reverse();
    }

    private void AddUserMessage(string text)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            this.Messages.Add(this.CreateDisplayMessage(true, text));
        });
    }

    private void AddAssistantMessage(string text)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            this.Messages.Add(this.CreateDisplayMessage(false, text));
        });
    }

    private void AddRuntimeEventMessage(ChatMessage message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (this.IsPlanEvent(message))
            {
                if (this._currentPlanMessage is null || !this.Messages.Contains(this._currentPlanMessage))
                {
                    this._currentPlanMessage = this.CreateDisplayMessage(message);
                    this.Messages.Add(this._currentPlanMessage);
                }
                else
                {
                    this._currentPlanMessage.Title = message.Title;
                    this._currentPlanMessage.Text = message.Text;
                    this._currentPlanMessage.Detail = message.Detail;
                }

                return;
            }

            if (this._currentTransientStatusMessage is null)
            {
                this._currentTransientStatusMessage = this.CreateDisplayMessage(message);
                this.Messages.Add(this._currentTransientStatusMessage);
            }
            else
            {
                this._currentTransientStatusMessage.Title = message.Title;
                this._currentTransientStatusMessage.Text = message.Text;
                this._currentTransientStatusMessage.Detail = message.Detail;
            }
        });
    }

    private void AppendAssistantOutput(string text, long conversationStateVersion)
    {
        string chunk = text.Replace("\r", string.Empty);
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        bool shouldScheduleFlush = false;
        lock (this._assistantOutputSync)
        {
            if (this._assistantOutputBufferVersion != conversationStateVersion)
            {
                _ = this._assistantOutputBuffer.Clear();
                this._assistantOutputBufferVersion = conversationStateVersion;
            }

            _ = this._assistantOutputBuffer.Append(chunk);
            if (!this._assistantOutputFlushScheduled)
            {
                this._assistantOutputFlushScheduled = true;
                shouldScheduleFlush = true;
            }
        }

        if (!shouldScheduleFlush)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(AssistantOutputFlushDelayMilliseconds).ConfigureAwait(false);
            await this.FlushPendingAssistantOutputAsync().ConfigureAwait(false);
        });
    }

    private Task FlushPendingAssistantOutputAsync()
    {
        Dispatcher dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            this.FlushPendingAssistantOutput();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(this.FlushPendingAssistantOutput, System.Windows.Threading.DispatcherPriority.Background).Task;
    }

    private void FlushPendingAssistantOutput()
    {
        string chunk;
        long conversationStateVersion;
        lock (this._assistantOutputSync)
        {
            chunk = this._assistantOutputBuffer.ToString();
            conversationStateVersion = this._assistantOutputBufferVersion;
            _ = this._assistantOutputBuffer.Clear();
            this._assistantOutputFlushScheduled = false;
        }

        if (string.IsNullOrEmpty(chunk) || !this.IsConversationStateCurrent(conversationStateVersion))
        {
            return;
        }

        this.ClearTransientStatusMessage();
        if (this._currentAssistantMessage is null)
        {
            this._currentAssistantMessage = this.CreateDisplayMessage(false, string.Empty);
            this.Messages.Add(this._currentAssistantMessage);
        }

        this._currentAssistantMessage.Text += chunk;
    }

    private void ClearPendingAssistantOutput()
    {
        lock (this._assistantOutputSync)
        {
            if (this._assistantOutputBuffer.Length == 0)
            {
                this._assistantOutputFlushScheduled = false;
                return;
            }

            _ = this._assistantOutputBuffer.Clear();
            this._assistantOutputFlushScheduled = false;
        }
    }

    private void AppendStderr(string text)
    {
        this.AppendOutput("[" + this.Localization.OutputTagStderr + "] " + text);
    }

    private void UpdateTokenUsage(long tokensInContextWindow, long? contextWindow)
    {
        if (contextWindow.HasValue && contextWindow.Value > 0)
        {
            this._contextTokenBudget = contextWindow.Value;
        }

        double clampedTokens = Math.Max(0d, tokensInContextWindow);

        RunOnUiThread(() =>
        {
            this._lastKnownContextTokensInWindow = clampedTokens;
            this._lastKnownRemainingTokens = GetContextRemainingTokenCount(clampedTokens, this._contextTokenBudget);
            this.OnPropertyChanged(nameof(this.ContextTokensLabel));
            this.OnPropertyChanged(nameof(this.ContextWindowDetail));
        });

        this.SetContextRemainingRatio(GetContextRemainingRatio(clampedTokens, this._contextTokenBudget));
    }

    private void UpdateContextEstimate()
    {
        double estimatedPromptTokens = Math.Max(1d, this.Prompt.Length / 4d);
        double estimatedImageTokens = this.AttachedImages.Count * 1200d;
        double estimated = estimatedPromptTokens + estimatedImageTokens;
        RunOnUiThread(() =>
        {
            this._lastKnownContextTokensInWindow = estimated;
            this._lastKnownRemainingTokens = GetContextRemainingTokenCount(estimated, this._contextTokenBudget);
            this.OnPropertyChanged(nameof(this.ContextTokensLabel));
            this.OnPropertyChanged(nameof(this.ContextWindowDetail));
        });
        this.SetContextRemainingRatio(GetContextRemainingRatio(estimated, this._contextTokenBudget));
    }

    private void SetContextRemainingRatio(double ratio)
    {
        this.ContextRingGeometry = BuildRingGeometry(ratio);
    }

    private static double GetContextRemainingRatio(double tokensInContextWindow, double contextWindow)
    {
        if (contextWindow <= ContextWindowBaselineTokens)
        {
            return 0d;
        }

        double effectiveWindow = contextWindow - ContextWindowBaselineTokens;
        double used = Math.Max(0d, tokensInContextWindow - ContextWindowBaselineTokens);
        double remaining = Math.Max(0d, effectiveWindow - used);
        return Math.Max(0d, Math.Min(1d, remaining / effectiveWindow));
    }

    private static double GetContextRemainingTokenCount(double tokensInContextWindow, double contextWindow)
    {
        if (contextWindow <= ContextWindowBaselineTokens)
        {
            return 0d;
        }

        double effectiveWindow = contextWindow - ContextWindowBaselineTokens;
        double used = Math.Max(0d, tokensInContextWindow - ContextWindowBaselineTokens);
        return Math.Max(0d, effectiveWindow - used);
    }

    private static double GetContextUsedRatio(double tokensInContextWindow, double contextWindow)
    {
        return 1d - GetContextRemainingRatio(tokensInContextWindow, contextWindow);
    }

    private static Geometry BuildRingGeometry(double ratio)
    {
        double clampedRatio = Math.Max(0d, Math.Min(1d, ratio));
        if (clampedRatio >= 0.9995d)
        {
            Geometry fullCircle = Geometry.Parse("M 8,1 A 7,7 0 1 1 7.99,1");
            if (fullCircle.CanFreeze)
            {
                fullCircle.Freeze();
            }

            return fullCircle;
        }

        if (clampedRatio <= 0d)
        {
            return Geometry.Empty;
        }

        const double center = 8d;
        const double radius = 7d;
        double sweepAngle = clampedRatio * 359.999d;
        double startAngle = -90d;
        double endAngle = startAngle + sweepAngle;
        Point startPoint = PointOnCircle(center, radius, startAngle);
        Point endPoint = PointOnCircle(center, radius, endAngle);

        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(startPoint, isFilled: false, isClosed: false);
            context.ArcTo(
                endPoint,
                new Size(radius, radius),
                rotationAngle: 0d,
                isLargeArc: sweepAngle > 180d,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        if (geometry.CanFreeze)
        {
            geometry.Freeze();
        }

        return geometry;
    }

    private static Point PointOnCircle(double center, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180d;
        return new Point(
            center + (Math.Cos(radians) * radius),
            center + (Math.Sin(radians) * radius));
    }

    private void ClearTransientStatusMessage()
    {
        RunOnUiThread(() =>
        {
            if (this._currentTransientStatusMessage is null)
            {
                return;
            }

            _ = this.Messages.Remove(this._currentTransientStatusMessage);
            this._currentTransientStatusMessage = null;
        });
    }

    private void ClearPersistedEventMessages()
    {
        RunOnUiThread(() =>
        {
            for (int index = this.Messages.Count - 1; index >= 0; index--)
            {
                if (this.Messages[index].IsEvent && !this.IsPlanEvent(this.Messages[index]))
                {
                    this.Messages.RemoveAt(index);
                }
            }
        });
    }

    private bool IsPlanEvent(ChatMessage message)
    {
        return message.IsEvent
            && string.Equals(message.Title, this.Localization.EventPlanTitle, StringComparison.CurrentCulture);
    }

    private void AppendFileReferenceToPrompt(string filePath)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        string fileReference = " @" + filePath;
        this.PromptEditorText = string.IsNullOrWhiteSpace(this.PromptEditorText)
            ? ("@" + filePath)
            : (this.PromptEditorText.TrimEnd() + fileReference);
    }

    private void ApplyRawPrompt(string? rawPrompt)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        IReadOnlyDictionary<string, CodexSkillSummary> availableSkills = this.GetAvailableSkillsByName();
        (IReadOnlyList<string> SkillNames, string DisplayText) formattedPrompt = FormatPromptSkillDisplay(rawPrompt ?? string.Empty, new HashSet<string>(availableSkills.Keys, StringComparer.OrdinalIgnoreCase), preserveWhitespace: true);

        this._promptEditorText = formattedPrompt.DisplayText;
        this.SyncDetectedPromptSkills(formattedPrompt.SkillNames, availableSkills);
        this.PromptDisplayText = this._promptEditorText;
        this._prompt = this.BuildEffectivePrompt();

        this.OnPropertyChanged(nameof(this.Prompt));
        this.OnPropertyChanged(nameof(this.PromptEditorText));
        this.RefreshSlashCommandSuggestions();
        this.RefreshMentions();
        this.UpdateContextEstimate();
        this.SendCommand.RaiseCanExecuteChanged();
    }

    private void ApplyPromptEditorText(string? editorText)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        IReadOnlyDictionary<string, CodexSkillSummary> availableSkills = this.GetAvailableSkillsByName();
        (IReadOnlyList<string> SkillNames, string DisplayText) formattedPrompt = FormatPromptSkillDisplay(editorText ?? string.Empty, new HashSet<string>(availableSkills.Keys, StringComparer.OrdinalIgnoreCase), preserveWhitespace: true);
        string[] mergedSkillNames = this.DetectedPromptSkills
            .Where(skill => skill.IsEnabled && !string.IsNullOrWhiteSpace(skill.Name))
            .Select(skill => skill.Name)
            .Concat(formattedPrompt.SkillNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        this._promptEditorText = formattedPrompt.DisplayText;
        this.SyncDetectedPromptSkills(mergedSkillNames, availableSkills);
        this.PromptDisplayText = this._promptEditorText;
        this._prompt = this.BuildEffectivePrompt();

        this.OnPropertyChanged(nameof(this.Prompt));
        this.OnPropertyChanged(nameof(this.PromptEditorText));
        this.RefreshSlashCommandSuggestions();
        this.RefreshMentions();
        this.UpdateContextEstimate();
        this.SendCommand.RaiseCanExecuteChanged();
    }

    private IReadOnlyDictionary<string, CodexSkillSummary> GetAvailableSkillsByName()
    {
        return this.Skills
            .Where(skill => skill.IsEnabled && !string.IsNullOrWhiteSpace(skill.Name))
            .ToDictionary(skill => skill.Name!, StringComparer.OrdinalIgnoreCase);
    }

    private void SyncDetectedPromptSkills(IEnumerable<string> skillNames, IReadOnlyDictionary<string, CodexSkillSummary> availableSkills)
    {
        List<CodexSkillSummary> selectedSkills = [];
        HashSet<string> uniqueSkillNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (string skillName in skillNames)
        {
            if (string.IsNullOrWhiteSpace(skillName) || !uniqueSkillNames.Add(skillName))
            {
                continue;
            }

            if (availableSkills.TryGetValue(skillName, out CodexSkillSummary? skill))
            {
                selectedSkills.Add(skill);
            }
        }

        this.DetectedPromptSkills.Clear();
        foreach (CodexSkillSummary skill in selectedSkills)
        {
            this.DetectedPromptSkills.Add(skill);
        }

        this.OnPropertyChanged(nameof(this.HasDetectedPromptSkills));
    }

    private string BuildEffectivePrompt()
    {
        return this.BuildEffectivePrompt(this._promptEditorText);
    }

    private string BuildEffectivePrompt(string editorText)
    {
        string skillPrefix = string.Join(
            " ",
            this.DetectedPromptSkills
                .Where(skill => skill.IsEnabled && !string.IsNullOrWhiteSpace(skill.Name))
                .Select(skill => "$" + skill.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(skillPrefix))
        {
            return editorText;
        }

        if (string.IsNullOrWhiteSpace(editorText))
        {
            return skillPrefix;
        }

        return skillPrefix + " " + editorText.TrimStart();
    }

    private void RemoveDetectedPromptSkill(object? parameter)
    {
        if (parameter is not CodexSkillSummary skillToRemove)
        {
            return;
        }

        string[] remainingSkills = this.DetectedPromptSkills
            .Where(skill => !string.Equals(skill.Name, skillToRemove.Name, StringComparison.OrdinalIgnoreCase))
            .Select(skill => skill.Name)
            .ToArray();

        this.SyncDetectedPromptSkills(remainingSkills, this.GetAvailableSkillsByName());
        this._prompt = this.BuildEffectivePrompt();
        this.OnPropertyChanged(nameof(this.Prompt));
        this.UpdateContextEstimate();
        this.SendCommand.RaiseCanExecuteChanged();
    }

    private static bool IsImageFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatCompactTokenCount(double tokens)
    {
        if (tokens >= 1000d)
        {
            return (tokens / 1000d).ToString("0.#", CultureInfo.CurrentUICulture) + "k";
        }

        return Math.Round(tokens).ToString(CultureInfo.CurrentUICulture);
    }

    private static string FormatPercent(double value)
    {
        return Math.Round(value * 100d).ToString("0", CultureInfo.CurrentUICulture) + "%";
    }

    private static string ExtractCurrentMention(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }

        int atIndex = prompt.LastIndexOf('@');
        if (atIndex < 0)
        {
            return string.Empty;
        }

        string tail = prompt.Substring(atIndex + 1);
        if (tail.Contains(" ") || tail.Contains(Environment.NewLine))
        {
            return string.Empty;
        }

        return tail.Trim();
    }

    private static IReadOnlyList<string> ExtractPromptSkillNames(string prompt)
    {
        List<string> detectedNames = [];
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return detectedNames;
        }

        HashSet<string> uniqueNames = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < prompt.Length; index++)
        {
            if (prompt[index] != '$')
            {
                continue;
            }

            if (index > 0 && !char.IsWhiteSpace(prompt[index - 1]))
            {
                continue;
            }

            int start = index + 1;
            int end = start;
            while (end < prompt.Length && IsSkillNameCharacter(prompt[end]))
            {
                end++;
            }

            if (end <= start)
            {
                continue;
            }

            string skillName = prompt.Substring(start, end - start);
            if (uniqueNames.Add(skillName))
            {
                detectedNames.Add(skillName);
            }
        }

        return detectedNames;
    }

    private static (IReadOnlyList<string> SkillNames, string DisplayText) FormatPromptSkillDisplay(string prompt, ISet<string> availableSkillNames, bool preserveWhitespace = false)
    {
        List<string> detectedNames = [];
        if (string.IsNullOrWhiteSpace(prompt) || availableSkillNames.Count == 0)
        {
            return (detectedNames, prompt);
        }

        HashSet<string> uniqueNames = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder builder = new(prompt.Length);

        for (int index = 0; index < prompt.Length; index++)
        {
            if (prompt[index] == '$'
                && (index == 0 || char.IsWhiteSpace(prompt[index - 1])))
            {
                int start = index + 1;
                int end = start;
                while (end < prompt.Length && IsSkillNameCharacter(prompt[end]))
                {
                    end++;
                }

                if (end > start)
                {
                    string skillName = prompt.Substring(start, end - start);
                    if (availableSkillNames.Contains(skillName))
                    {
                        if (uniqueNames.Add(skillName))
                        {
                            detectedNames.Add(skillName);
                        }

                        index = preserveWhitespace && end < prompt.Length && (prompt[end] == ' ' || prompt[end] == '\t')
                            ? end
                            : end - 1;
                        continue;
                    }
                }
            }

            _ = builder.Append(prompt[index]);
        }

        return (detectedNames, preserveWhitespace ? builder.ToString() : CleanupPromptDisplayText(builder.ToString()));
    }

    private void RefreshSlashCommandSuggestions()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        string editorText = this._promptEditorText ?? string.Empty;
        string trimmed = editorText.TrimStart();
        this.SlashCommandSuggestions.Clear();

        if (!trimmed.StartsWith("/", StringComparison.Ordinal)
            || trimmed.IndexOfAny(new[] { '\r', '\n' }) >= 0
            || trimmed.Any(char.IsWhiteSpace))
        {
            this.OnPropertyChanged(nameof(this.HasSlashCommandSuggestions));
            return;
        }

        foreach (CodexSlashCommand command in AvailableSlashCommands.Where(command =>
            command.CommandText.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            this.SlashCommandSuggestions.Add(command);
        }

        this.OnPropertyChanged(nameof(this.HasSlashCommandSuggestions));
    }

    private void InsertSlashCommand(object? parameter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (parameter is not CodexSlashCommand command)
        {
            return;
        }

        this.PromptEditorText = command.CommandText + (command.AcceptsArguments ? " " : string.Empty);
    }

    private static string CleanupPromptDisplayText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Replace("\r\n", "\n");
        string[] lines = normalized.Split('\n');
        string[] cleanedLines = lines
            .Select(CollapseInlineWhitespace)
            .ToArray();

        return string.Join(Environment.NewLine, cleanedLines).Trim();
    }

    private static string CollapseInlineWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new(text.Length);
        bool previousWasWhitespace = false;

        foreach (char character in text)
        {
            if (character is ' ' or '\t')
            {
                if (!previousWasWhitespace)
                {
                    _ = builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            _ = builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static bool IsSkillNameCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '-' || value == '_' || value == '.';
    }

    private static string SaveBitmapToTempPng(BitmapSource bitmapSource)
    {
        string directory = Path.Combine(Path.GetTempPath(), "CodexVsixImages");
        _ = Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private void AppendOutput(string text)
    {
        RunOnUiThread(() => this._output += text);
        RunOnUiThread(() => this.OnPropertyChanged(nameof(this.Output)));
    }

    private long CaptureConversationStateVersion()
    {
        return Interlocked.Read(ref this._conversationStateVersion);
    }

    private long BeginConversationStateChange()
    {
        this.ClearPendingAssistantOutput();
        return Interlocked.Increment(ref this._conversationStateVersion);
    }

    private bool IsConversationStateCurrent(long version)
    {
        return this.CaptureConversationStateVersion() == version;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static void RunOnUiThread(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}
