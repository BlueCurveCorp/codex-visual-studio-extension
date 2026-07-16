using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using CodexVsix.Models;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

public sealed class CodexProcessService : IDisposable
{
    private static readonly Regex MentionRegex = new(@"(?<!\S)@(?<value>\S+)", RegexOptions.Compiled);
    private static readonly Regex SkillRegex = new(@"(?<!\S)\$(?<value>[A-Za-z0-9][A-Za-z0-9._-]*)", RegexOptions.Compiled);
    private static readonly Regex TrailingPromptAfterPathRegex = new(@"(?:\.[A-Za-z0-9]{1,8})(?<prompt>[\p{L}\p{N}@#\$""'(\[].*)$", RegexOptions.Compiled);
    private static readonly string[] ExtensionContextPrefixes = CreateLocalizedSet(localization => localization.ExtensionContextPrefix);
    private static readonly string[] PreferredMcpPrefixes = CreateLocalizedSet(localization => localization.PreferredMcpPrefix);
    private static readonly string[] IdeContextPrefixes = CreateLocalizedSet(localization => localization.IdeContextPrefix);
    private static readonly string[] SyntheticUserContextPrefixes =
    {
        "# AGENTS.md instructions",
        "<environment_context>",
        "<permissions instructions>",
        "<apps_instructions>",
        "<skills_instructions>",
        "<plugins_instructions>",
        "<collaboration_mode>",
        "<turn_aborted>"
    };

    private static readonly string[] IdeContextLinePrefixes = CreateLocalizedSet(
        localization => localization.IdeContextSolutionLabel,
        localization => localization.IdeContextActiveDocumentLabel,
        localization => localization.IdeContextSelectedItemsLabel,
        localization => localization.IdeContextOpenFilesLabel,
        localization => localization.IdeContextSelectionLabel);

    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly object _syncRoot = new();
    private readonly object _writeLock = new();
    private readonly Dictionary<long, TaskCompletionSource<JToken?>> _pendingRequests = [];
    private readonly Dictionary<string, string> _skillsByName = new(StringComparer.OrdinalIgnoreCase);

    private Process? _serverProcess;
    private StreamWriter? _serverInput;
    private TaskCompletionSource<bool>? _initializedTcs;
    private ActiveTurnState? _activeTurn;
    private string? _threadId;
    private string? _threadConfigKey;
    private string? _serverConfigKey;
    private string? _skillsCacheKey;
    private string? _languageOverride;
    private bool _threadLoaded;
    private long _nextRequestId;

    public Func<CodexApprovalRequest, Task<JToken?>>? ApprovalRequestHandler { get; set; }
    public Func<CodexUserInputRequest, Task<JObject?>>? UserInputRequestHandler { get; set; }
    public event Action? ThreadCatalogChanged;
    public event Action<CodexRateLimitSummary>? RateLimitsUpdated;
    public event Action? AccountUpdated;

    public string? CurrentThreadId
    {
        get
        {
            lock (this._syncRoot)
            {
                return this._threadId;
            }
        }
    }

    public async Task<int> ExecuteAsync(
        string prompt,
        CodexExtensionSettings settings,
        IEnumerable<string> imagePaths,
        string ideContextSummary,
        JObject? reviewTarget,
        Action<string> onOutput,
        Action<string> onError,
        Action<ChatMessage>? onEventMessage,
        Action<long, long?>? onTokenUsage,
        CancellationToken cancellationToken)
    {
        await this._executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
            await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);
            await this.EnsureThreadReadyAsync(settings, workingDirectory, settings.CurrentThreadId, cancellationToken).ConfigureAwait(false);
            await this.RefreshSkillsAsync(workingDirectory, cancellationToken).ConfigureAwait(false);

            ActiveTurnState turnState = new(onOutput, onError, onEventMessage, onTokenUsage);
            lock (this._syncRoot)
            {
                this._activeTurn = turnState;
            }

            using (cancellationToken.Register(() => _ = this.InterruptActiveTurnAsync()))
            {
                try
                {
                    JToken? turnResult;
                    if (reviewTarget is null)
                    {
                        turnResult = await this.StartTurnWithFallbackAsync(
                            turnState,
                            this._threadId!,
                            prompt,
                            settings,
                            workingDirectory,
                            imagePaths,
                            ideContextSummary,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        turnResult = await this.SendRequestAsync(
                            "review/start",
                            new JObject
                            {
                                ["threadId"] = this._threadId,
                                ["target"] = reviewTarget.DeepClone(),
                                ["delivery"] = "inline"
                            },
                            cancellationToken).ConfigureAwait(false);
                    }

                    turnState.TurnId = turnResult?["turn"]?["id"]?.Value<string>();
                    int exitCode = await turnState.Completion.Task.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return exitCode;
                }
                finally
                {
                    lock (this._syncRoot)
                    {
                        if (ReferenceEquals(this._activeTurn, turnState))
                        {
                            this._activeTurn = null;
                        }
                    }
                }
            }
        }
        finally
        {
            _ = this._executionGate.Release();
        }
    }

    public void Dispose()
    {
        lock (this._syncRoot)
        {
            this._activeTurn?.TrySetResult(1);
            this._activeTurn = null;
        }

        this.RestartServer(clearConfig: true);
        this._executionGate.Dispose();
    }

    public void ResetThread()
    {
        lock (this._syncRoot)
        {
            this._threadId = null;
            this._threadConfigKey = null;
            this._threadLoaded = false;
        }
    }

    public void CancelActiveTurn()
    {
        ActiveTurnState? turnState;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
        }

        turnState?.TrySetResult(1);
        this.RestartServer(clearConfig: false);
    }

    public async Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(CodexExtensionSettings settings, CancellationToken cancellationToken)
    {
        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);

        string currentThreadId = this.CurrentThreadId ?? settings.CurrentThreadId;
        JArray? items = await this.RequestThreadListItemsAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        List<CodexThreadSummary> threads = [];
        if (items is null)
        {
            return threads;
        }

        foreach (JToken item in items)
        {
            CodexThreadSummary? summary = ParseThreadSummary(item, currentThreadId);
            if (summary is not null)
            {
                threads.Add(summary);
            }
        }

        return threads;
    }

    private async Task<JArray?> RequestThreadListItemsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        foreach (string candidate in GetThreadListWorkingDirectoryCandidates(workingDirectory))
        {
            JArray? items = await this.SendThreadListRequestAsync(candidate, 200, cancellationToken).ConfigureAwait(false);
            if (items is { Count: > 0 })
            {
                return items;
            }
        }

        JArray? allItems = await this.SendThreadListRequestAsync(null, 500, cancellationToken).ConfigureAwait(false);
        if (allItems is null || allItems.Count == 0)
        {
            return allItems;
        }

        JArray filteredItems = [];
        foreach (JToken item in allItems)
        {
            if (ThreadMatchesWorkingDirectory(item, workingDirectory))
            {
                filteredItems.Add(item.DeepClone());
            }
        }

        return filteredItems;
    }

    private async Task<JArray?> SendThreadListRequestAsync(string? workingDirectory, int limit, CancellationToken cancellationToken)
    {
        JObject parameters = new()
        {
            ["limit"] = limit,
            ["archived"] = false,
            ["sortKey"] = "updated_at"
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            parameters["cwd"] = workingDirectory;
        }

        JToken? response = await this.SendRequestAsync("thread/list", parameters, cancellationToken).ConfigureAwait(false);
        return response?["data"] as JArray;
    }

    private static IEnumerable<string> GetThreadListWorkingDirectoryCandidates(string workingDirectory)
    {
        string normalized = NormalizeComparablePath(workingDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        yield return normalized;

        string devicePath = ToWindowsDevicePath(normalized);
        if (!string.IsNullOrWhiteSpace(devicePath) && !string.Equals(devicePath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return devicePath;
        }
    }

    private static bool ThreadMatchesWorkingDirectory(JToken item, string workingDirectory)
    {
        string threadWorkingDirectory = NormalizeComparablePath(item?["cwd"]?.Value<string>());
        string normalizedWorkingDirectory = NormalizeComparablePath(workingDirectory);
        return !string.IsNullOrWhiteSpace(threadWorkingDirectory)
            && !string.IsNullOrWhiteSpace(normalizedWorkingDirectory)
            && string.Equals(threadWorkingDirectory, normalizedWorkingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CodexThreadConversation?> LoadThreadConversationAsync(CodexExtensionSettings settings, string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);

        JToken? resumed = await this.SendRequestAsync(
            "thread/resume",
            BuildThreadResumeParams(threadId, settings, workingDirectory),
            cancellationToken).ConfigureAwait(false);

        lock (this._syncRoot)
        {
            this._threadId = resumed?["thread"]?["id"]?.Value<string>() ?? threadId;
            this._threadConfigKey = BuildThreadConfigKey(settings, workingDirectory);
            this._threadLoaded = true;
        }

        JToken? readResponse = await this.SendRequestAsync(
            "thread/read",
            new
            {
                threadId,
                includeTurns = true
            },
            cancellationToken).ConfigureAwait(false);

        JToken? thread = readResponse?["thread"] ?? resumed?["thread"];
        if (thread is null)
        {
            return null;
        }

        CodexThreadSummary summary = ParseThreadSummary(thread, threadId) ?? new CodexThreadSummary { ThreadId = threadId };
        string? sessionPath = thread["path"]?.Value<string>() ?? FindSessionPathForThread(summary.ThreadId);
        return new CodexThreadConversation
        {
            Thread = summary,
            Messages = ParseThreadMessages(thread, summary.ThreadId, sessionPath)
        };
    }

    public async Task<IReadOnlyList<SelectionOption>> ListModelsAsync(CodexExtensionSettings settings, CancellationToken cancellationToken, bool includeHidden = false)
    {
        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);

        JToken? response = await this.SendRequestAsync("model/list", new { }, cancellationToken).ConfigureAwait(false);
        List<SelectionOption> models = [];
        if (response?["data"] is not JArray items)
        {
            return models;
        }

        foreach (JToken? item in items)
        {
            if (!includeHidden && item?["hidden"]?.Value<bool>() == true)
            {
                continue;
            }

            string? value = item?["model"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string? label = item?["displayName"]?.Value<string>();
            models.Add(new SelectionOption(string.IsNullOrWhiteSpace(label) ? value! : label!, value!));
        }

        return models;
    }

    public async Task RenameThreadAsync(CodexExtensionSettings settings, string threadId, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);
        _ = await this.SendRequestAsync(
            "thread/name/set",
            new
            {
                threadId,
                name = name.Trim()
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ArchiveThreadAsync(CodexExtensionSettings settings, string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);
        _ = await this.SendRequestAsync(
            "thread/archive",
            new
            {
                threadId
            },
            cancellationToken).ConfigureAwait(false);

        lock (this._syncRoot)
        {
            if (string.Equals(this._threadId, threadId, StringComparison.Ordinal))
            {
                this._threadId = null;
                this._threadConfigKey = null;
                this._threadLoaded = false;
            }
        }
    }

    public async Task DeleteThreadAsync(CodexExtensionSettings settings, string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);
        _ = await this.SendRequestAsync(
            "thread/delete",
            new
            {
                threadId
            },
            cancellationToken).ConfigureAwait(false);

        this.ResetLoadedThread(threadId);
    }

    public async Task CompactThreadAsync(CodexExtensionSettings settings, string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("A saved Codex thread is required before it can be compacted.");
        }

        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);
        _ = await this.SendRequestAsync(
            "thread/compact/start",
            new
            {
                threadId
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ForkThreadAsync(CodexExtensionSettings settings, string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("A saved Codex thread is required before it can be forked.");
        }

        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);
        JToken? response = await this.SendRequestAsync(
            "thread/fork",
            new
            {
                threadId,
                cwd = workingDirectory,
                model = string.IsNullOrWhiteSpace(settings.DefaultModel) ? null : settings.DefaultModel.Trim(),
                approvalPolicy = NormalizeApprovalPolicy(settings.ApprovalPolicy),
                sandbox = NormalizeSandboxMode(settings.SandboxMode),
                serviceTier = NormalizeServiceTier(settings.ServiceTier),
                ephemeral = false,
                excludeTurns = false
            },
            cancellationToken).ConfigureAwait(false);

        string? forkedThreadId = response?["thread"]?["id"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(forkedThreadId))
        {
            throw new InvalidOperationException("Codex did not return an id for the forked thread.");
        }

        lock (this._syncRoot)
        {
            this._threadId = forkedThreadId;
            this._threadConfigKey = null;
            this._threadLoaded = true;
        }

        return forkedThreadId!;
    }

    private void ResetLoadedThread(string threadId)
    {
        lock (this._syncRoot)
        {
            if (string.Equals(this._threadId, threadId, StringComparison.Ordinal))
            {
                this._threadId = null;
                this._threadConfigKey = null;
                this._threadLoaded = false;
            }
        }
    }

    public async Task<IReadOnlyList<CodexAppSummary>> ListAppsAsync(CodexExtensionSettings settings, CancellationToken cancellationToken)
    {
        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);

        JToken? response = await this.SendRequestAsync(
            "app/list",
            new
            {
                limit = 20,
                forceRefetch = false,
                threadId = this.CurrentThreadId
            },
            cancellationToken).ConfigureAwait(false);

        List<CodexAppSummary> apps = [];
        if (response?["data"] is not JArray items)
        {
            return apps;
        }

        foreach (JToken? item in items)
        {
            string? name = item?["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string? description = item?["description"]?.Value<string>();
            apps.Add(new CodexAppSummary
            {
                Name = name!,
                Description = description ?? string.Empty
            });
        }

        return apps;
    }

    public async Task<IReadOnlyList<CodexMcpServerSummary>> ListMcpServersAsync(CodexExtensionSettings settings, CancellationToken cancellationToken)
    {
        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);

        JToken? response = await this.SendRequestAsync(
            "mcpServerStatus/list",
            new
            {
                limit = 20
            },
            cancellationToken).ConfigureAwait(false);

        List<CodexMcpServerSummary> servers = [];
        if (response?["data"] is not JArray items)
        {
            return servers;
        }

        foreach (JToken? item in items)
        {
            string? name = item?["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string[] tools = item?["tools"]?.Children<JProperty>().Select(property => property.Name).Take(4).ToArray() ?? new string[0];
            string toolsLabel = tools.Length == 0 ? string.Empty : string.Join(", ", tools);
            servers.Add(new CodexMcpServerSummary
            {
                Name = name!,
                AuthStatus = item?["authStatus"]?.Value<string>() ?? string.Empty,
                ToolsLabel = toolsLabel
            });
        }

        return servers;
    }

    public async Task<IReadOnlyList<CodexSkillSummary>> ListSkillsAsync(CodexExtensionSettings settings, CancellationToken cancellationToken, bool forceReload = false)
    {
        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);

        JToken? response = await this.SendRequestAsync(
            "skills/list",
            new { cwds = new[] { workingDirectory }, forceReload },
            cancellationToken).ConfigureAwait(false);

        string homeSkillsDirectory = Path.Combine(
            CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables),
            "skills");

        List<CodexSkillSummary> summaries = [];
        if (response?["data"] is not JArray entries)
        {
            return summaries;
        }

        foreach (JToken entry in entries)
        {
            if (entry["skills"] is not JArray skills)
            {
                continue;
            }

            foreach (JToken skill in skills)
            {
                string? name = skill["name"]?.Value<string>();
                string? path = skill["path"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                bool isSystem = path.IndexOf(".system", StringComparison.OrdinalIgnoreCase) >= 0;
                summaries.Add(new CodexSkillSummary
                {
                    Name = name!,
                    DisplayName = skill["interface"]?["displayName"]?.Value<string>() ?? name!,
                    Description = skill["description"]?.Value<string>() ?? string.Empty,
                    ShortDescription = skill["interface"]?["shortDescription"]?.Value<string>()
                        ?? skill["shortDescription"]?.Value<string>()
                        ?? string.Empty,
                    Path = path!,
                    IsEnabled = skill["enabled"]?.Value<bool>() ?? true,
                    IsSystem = isSystem,
                    ScopeLabel = BuildSkillScopeLabel(path!, workingDirectory, homeSkillsDirectory, isSystem)
                });
            }
        }

        return summaries
            .OrderBy(skill => skill.IsSystem)
            .ThenBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<CodexRemoteSkillSummary>> ListRemoteSkillsAsync(CodexExtensionSettings settings, CancellationToken cancellationToken)
    {
        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);

        JToken? response = await this.SendRequestAsync(
            "skills/remote/list",
            new
            {
                hazelnutScope = "all-shared",
                productSurface = "codex",
                enabled = true
            },
            cancellationToken).ConfigureAwait(false);

        List<CodexRemoteSkillSummary> summaries = [];
        if (response?["data"] is not JArray items)
        {
            return summaries;
        }

        foreach (JToken? item in items)
        {
            string? id = item?["id"]?.Value<string>();
            string? name = item?["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            summaries.Add(new CodexRemoteSkillSummary
            {
                Id = id!,
                Name = name!,
                Description = item?["description"]?.Value<string>() ?? string.Empty
            });
        }

        return summaries
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> InstallRemoteSkillAsync(CodexExtensionSettings settings, string remoteSkillId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remoteSkillId))
        {
            return null;
        }

        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);

        JToken? response = await this.SendRequestAsync(
            "skills/remote/export",
            new
            {
                hazelnutId = remoteSkillId
            },
            cancellationToken).ConfigureAwait(false);

        return response?["path"]?.Value<string>();
    }

    public async Task<bool> SetSkillEnabledAsync(CodexExtensionSettings settings, string path, bool enabled, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return enabled;
        }

        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);

        JToken? response = await this.SendRequestAsync(
            "skills/config/write",
            new
            {
                path,
                enabled
            },
            cancellationToken).ConfigureAwait(false);

        return response?["effectiveEnabled"]?.Value<bool>() ?? enabled;
    }

    public async Task<CodexRateLimitSummary> GetAccountRateLimitsAsync(CodexExtensionSettings settings, CancellationToken cancellationToken)
    {
        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);

        JToken? response = await this.SendRequestAsync("account/rateLimits/read", new { }, cancellationToken).ConfigureAwait(false);
        return this.BuildRateLimitSummary(response);
    }

    public void InvalidateSkillsCache()
    {
        lock (this._syncRoot)
        {
            this._skillsCacheKey = null;
            this._skillsByName.Clear();
        }
    }

    private async Task EnsureServerReadyAsync(CodexExtensionSettings settings, string workingDirectory, CancellationToken cancellationToken)
    {
        this._languageOverride = settings.LanguageOverride;
        string desiredServerConfig = BuildServerConfigKey(settings);
        bool shouldStart = false;
        bool needsRestart = false;

        lock (this._syncRoot)
        {
            if (this._serverProcess is null || this._serverProcess.HasExited || !string.Equals(this._serverConfigKey, desiredServerConfig, StringComparison.Ordinal))
            {
                shouldStart = true;
                needsRestart = true;
                this._serverConfigKey = desiredServerConfig;
                this._initializedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (needsRestart)
        {
            this.RestartServer(clearConfig: false);
            lock (this._syncRoot)
            {
                this._serverConfigKey = desiredServerConfig;
                this._initializedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            shouldStart = true;
        }

        if (shouldStart)
        {
            this.StartServerProcess(settings, workingDirectory);
            await this.InitializeServerAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Task<bool>? initTask = this._initializedTcs?.Task;
        if (initTask is not null)
        {
            _ = await initTask.ConfigureAwait(false);
        }
    }

    private async Task InitializeServerAsync(CancellationToken cancellationToken)
    {
        _ = await this.SendRequestAsync(
            "initialize",
            new
            {
                clientInfo = new { name = "codex-vsix", version = "1.0" },
                capabilities = new { experimentalApi = true }
            },
            cancellationToken).ConfigureAwait(false);

        await this.SendNotificationAsync("initialized", new { }).ConfigureAwait(false);

        lock (this._syncRoot)
        {
            _ = (this._initializedTcs?.TrySetResult(true));
        }
    }

    private async Task EnsureThreadReadyAsync(CodexExtensionSettings settings, string workingDirectory, string? requestedThreadId, CancellationToken cancellationToken)
    {
        string desiredThreadConfig = BuildThreadConfigKey(settings, workingDirectory);
        if (!string.IsNullOrWhiteSpace(requestedThreadId))
        {
            if (string.Equals(this.CurrentThreadId, requestedThreadId, StringComparison.Ordinal) && this._threadLoaded && string.Equals(this._threadConfigKey, desiredThreadConfig, StringComparison.Ordinal))
            {
                return;
            }

            JToken? resumed = await this.SendRequestAsync(
                "thread/resume",
                BuildThreadResumeParams(requestedThreadId, settings, workingDirectory),
                cancellationToken).ConfigureAwait(false);

            lock (this._syncRoot)
            {
                this._threadId = resumed?["thread"]?["id"]?.Value<string>() ?? requestedThreadId;
                this._threadConfigKey = desiredThreadConfig;
                this._threadLoaded = true;
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(this._threadId) && this._threadLoaded && string.Equals(this._threadConfigKey, desiredThreadConfig, StringComparison.Ordinal))
        {
            return;
        }

        JToken? result = await this.SendRequestAsync(
            "thread/start",
            this.BuildThreadStartParams(settings, workingDirectory),
            cancellationToken).ConfigureAwait(false);

        lock (this._syncRoot)
        {
            this._threadId = result?["thread"]?["id"]?.Value<string>();
            this._threadConfigKey = desiredThreadConfig;
            this._threadLoaded = true;
        }
    }

    private async Task RefreshSkillsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (string.Equals(this._skillsCacheKey, workingDirectory, StringComparison.OrdinalIgnoreCase) && this._skillsByName.Count > 0)
        {
            return;
        }

        try
        {
            JToken? response = await this.SendRequestAsync(
                "skills/list",
                new { cwds = new[] { workingDirectory }, forceReload = false },
                cancellationToken).ConfigureAwait(false);

            JArray? entries = response?["data"] as JArray;
            lock (this._syncRoot)
            {
                this._skillsByName.Clear();

                if (entries is not null)
                {
                    foreach (JToken entry in entries)
                    {
                        if (entry["skills"] is not JArray skills)
                        {
                            continue;
                        }

                        foreach (JToken skill in skills)
                        {
                            bool enabled = skill["enabled"]?.Value<bool>() ?? true;
                            string? name = skill["name"]?.Value<string>();
                            string? path = skill["path"]?.Value<string>();
                            if (enabled && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
                            {
                                this._skillsByName[name!] = path!;
                            }
                        }
                    }
                }

                this._skillsCacheKey = workingDirectory;
            }
        }
        catch
        {
            lock (this._syncRoot)
            {
                this._skillsByName.Clear();
                this._skillsCacheKey = workingDirectory;
            }
        }
    }

    private void StartServerProcess(CodexExtensionSettings settings, string workingDirectory)
    {
        string executablePath = CodexExecutableResolver.ResolveExecutableLocation(settings.CodexExecutablePath, settings.EnvironmentVariables);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = CodexExecutableResolver.NormalizeConfiguredExecutablePath(settings.CodexExecutablePath);
        }

        string arguments = BuildServerArguments(settings);
        ProcessStartInfo startInfo = BuildStartInfo(executablePath, arguments, workingDirectory);

        ProcessStartInfo psi = new()
        {
            FileName = startInfo.FileName,
            WorkingDirectory = startInfo.WorkingDirectory,
            Arguments = startInfo.Arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        ApplyEnvironmentVariables(psi, settings.EnvironmentVariables);

        Process process = new() { StartInfo = psi, EnableRaisingEvents = true };
        _ = process.Start();
        process.Exited += (_, _) => this.FailPendingOperations(this.GetLocalization().AppServerClosedUnexpectedly);

        StreamWriter serverInput = new(process.StandardInput.BaseStream, new UTF8Encoding(false), 1024, true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        lock (this._syncRoot)
        {
            this._serverProcess = process;
            this._serverInput = serverInput;
        }

        _ = Task.Run(() => this.ReadStdoutLoopAsync(process));
        _ = Task.Run(() => this.ReadStderrLoopAsync(process));
    }

    private async Task ReadStdoutLoopAsync(Process process)
    {
        try
        {
            while (!process.StandardOutput.EndOfStream)
            {
                string line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    this.HandleServerMessage(line);
                }
                catch (Exception ex)
                {
                    this.PublishError("[" + this.GetLocalization().OutputTagAppServer + "] " + ex.Message + Environment.NewLine);
                }
            }
        }
        catch (Exception ex)
        {
            this.FailPendingOperations(ex.Message);
        }
    }

    private async Task ReadStderrLoopAsync(Process process)
    {
        try
        {
            while (!process.StandardError.EndOfStream)
            {
                string? line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is not null)
                {
                    this.PublishError(line + Environment.NewLine);
                }
            }
        }
        catch (Exception ex)
        {
            this.PublishError("[" + this.GetLocalization().OutputTagStderr + "] " + ex.Message + Environment.NewLine);
        }
    }

    private void HandleServerMessage(string rawMessage)
    {
        JToken parsedMessage;
        try
        {
            parsedMessage = JToken.Parse(rawMessage);
        }
        catch
        {
            this.PublishError("[" + this.GetLocalization().OutputTagAppServer + "] " + rawMessage + Environment.NewLine);
            return;
        }

        if (parsedMessage is not JObject message)
        {
            this.PublishError("[" + this.GetLocalization().OutputTagAppServer + "] " + rawMessage + Environment.NewLine);
            return;
        }

        if (message["id"] is not null && (message["result"] is not null || message["error"] is not null) && message["method"] is null)
        {
            this.ResolvePendingRequest(message);
            return;
        }

        if (message["id"] is not null && message["method"] is not null)
        {
            _ = this.HandleServerRequestAsync(message);
            return;
        }

        this.HandleNotification(message);
    }

    private void ResolvePendingRequest(JObject message)
    {
        long id = message["id"]?.Value<long>() ?? 0L;
        TaskCompletionSource<JToken?>? tcs;
        lock (this._syncRoot)
        {
            _ = this._pendingRequests.TryGetValue(id, out tcs);
            if (tcs is not null)
            {
                _ = this._pendingRequests.Remove(id);
            }
        }

        if (tcs is null)
        {
            return;
        }

        if (message["error"] is not null)
        {
            string errorMessage = GetNestedString(message["error"], "message")
                ?? message["error"]?.Value<string>()
                ?? this.GetLocalization().AppServerRequestFailed;
            _ = tcs.TrySetException(new InvalidOperationException(errorMessage));
            return;
        }

        _ = tcs.TrySetResult(message["result"]);
    }

    private async Task HandleServerRequestAsync(JObject message)
    {
        JToken? id = message["id"];
        string method = message["method"]?.Value<string>() ?? string.Empty;
        JObject? parameters = message["params"] as JObject;
        if (string.Equals(method, "item/commandExecution/requestApproval", StringComparison.Ordinal) ||
            string.Equals(method, "item/fileChange/requestApproval", StringComparison.Ordinal))
        {
            CodexApprovalRequest approvalRequest = this.BuildApprovalRequest(method, parameters);
            JToken decision = await this.ResolveApprovalDecisionAsync(approvalRequest).ConfigureAwait(false);
            await this.SendResponseAsync(
                id,
                new JObject
                {
                    ["decision"] = decision
                }).ConfigureAwait(false);
            return;
        }

        if (string.Equals(method, "item/tool/requestUserInput", StringComparison.Ordinal))
        {
            CodexUserInputRequest userInputRequest = BuildUserInputRequest(parameters);
            JObject? response = await this.ResolveUserInputRequestAsync(userInputRequest).ConfigureAwait(false);
            await this.SendResponseAsync(id, response ?? new JObject { ["answers"] = new JObject() }).ConfigureAwait(false);
            return;
        }

        await this.SendResponseAsync(id, []).ConfigureAwait(false);
    }

    private void HandleNotification(JObject message)
    {
        string method = message["method"]?.Value<string>() ?? string.Empty;
        JObject? parameters = message["params"] as JObject;

        switch (method)
        {
            case "turn/started":
                this.HandleTurnStarted(parameters);
                break;

            case "item/agentMessage/delta":
                this.HandleAgentMessageDelta(parameters);
                break;

            case "item/plan/delta":
                this.HandlePlanDelta(parameters);
                break;

            case "item/completed":
                this.HandleCompletedItem(parameters);
                break;

            case "turn/plan/updated":
                this.HandleTurnPlanUpdated(parameters);
                break;

            case "turn/diff/updated":
                this.HandleTurnDiffUpdated(parameters);
                break;

            case "turn/completed":
                this.HandleTurnCompleted(parameters);
                break;

            case "codex/event/agent_message":
                this.HandleAgentMessageEvent(parameters);
                break;

            case "codex/event/task_complete":
                this.HandleTaskCompleteEvent(parameters);
                break;

            case "codex/event/token_count":
                this.HandleTokenCountEvent(parameters);
                break;

            case "thread/tokenUsage/updated":
                this.HandleTokenUsageUpdated(parameters);
                break;

            case "item/mcpToolCall/progress":
                this.HandleMcpToolCallProgress(parameters);
                break;

            case "thread/started":
            case "thread/archived":
            case "thread/deleted":
            case "thread/unarchived":
            case "thread/compacted":
            case "thread/closed":
            case "thread/name/updated":
            case "thread/status/changed":
                this.NotifyThreadCatalogChanged();
                break;

            case "account/rateLimits/updated":
                this.PublishRateLimitsUpdate(parameters);
                break;

            case "account/updated":
                AccountUpdated?.Invoke();
                break;

            case "skills/changed":
                lock (this._syncRoot)
                {
                    this._skillsCacheKey = null;
                }
                break;

            case "error":
                string? errorMessage = GetNestedString(parameters?["error"], "message")
                    ?? parameters?["error"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    this.PublishError(errorMessage + Environment.NewLine);
                }
                break;
        }
    }

    private void HandleTurnStarted(JToken? parameters)
    {
        string? turnId = parameters?["turn"]?["id"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        lock (this._syncRoot)
        {
            if (this._activeTurn is not null && string.IsNullOrWhiteSpace(this._activeTurn.TurnId))
            {
                this._activeTurn.TurnId = turnId;
            }
        }
    }

    private void HandleAgentMessageDelta(JToken? parameters)
    {
        if (!this.MatchesActiveTurn(parameters?["turnId"]?.Value<string>()))
        {
            return;
        }

        string? itemId = parameters?["itemId"]?.Value<string>();
        string? delta = parameters?["delta"]?.Value<string>();

        ActiveTurnState? turnState;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
            if (turnState is not null && !string.IsNullOrWhiteSpace(itemId))
            {
                _ = turnState.StreamedItemIds.Add(itemId!);
            }
        }

        if (!string.IsNullOrWhiteSpace(delta))
        {
            turnState!.HasAssistantOutput = true;
            turnState?.OnOutput(delta!);
        }
    }

    private void HandlePlanDelta(JToken? parameters)
    {
        if (!this.MatchesActiveTurn(parameters?["turnId"]?.Value<string>()))
        {
            return;
        }

        string? delta = parameters?["delta"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(delta))
        {
            return;
        }

        string? itemId = parameters?["itemId"]?.Value<string>();
        string accumulatedPlan;

        ActiveTurnState? turnState;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
            if (turnState is null)
            {
                return;
            }

            accumulatedPlan = turnState.AppendPlanDelta(itemId, delta!);
        }

        turnState.OnEventMessage?.Invoke(CreatePlanEventMessage(accumulatedPlan));
    }

    private void HandleAgentMessageEvent(JToken? parameters)
    {
        if (!this.MatchesActiveTurnEvent(parameters))
        {
            return;
        }

        ActiveTurnState? turnState;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
        }

        if (turnState is null)
        {
            return;
        }

        JToken? payload = parameters?["msg"];
        string? message = payload?["message"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string? phase = payload?["phase"]?.Value<string>();
        if (string.Equals(phase, "commentary", StringComparison.OrdinalIgnoreCase))
        {
            turnState.OnEventMessage?.Invoke(new ChatMessage(false, message.Trim(), isEvent: true, title: this.GetLocalization().EventCommentaryTitle));
            return;
        }

        PublishAssistantOutput(turnState, message, skipIfAlreadyPublished: true);
    }

    private void HandleTurnPlanUpdated(JToken? parameters)
    {
        if (!this.MatchesActiveTurn(parameters?["turnId"]?.Value<string>()))
        {
            return;
        }

        ActiveTurnState? turnState;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
        }

        if (turnState is null)
        {
            return;
        }

        string? planMarkdown = BuildStructuredPlanMarkdown(
            parameters?["explanation"]?.Value<string>(),
            parameters?["plan"] as JArray,
            this.GetLocalization());
        if (string.IsNullOrWhiteSpace(planMarkdown))
        {
            return;
        }

        lock (this._syncRoot)
        {
            turnState.SetPlanText(null, planMarkdown);
        }

        turnState.OnEventMessage?.Invoke(CreatePlanEventMessage(planMarkdown));
    }

    private void HandleTaskCompleteEvent(JToken? parameters)
    {
        if (!this.MatchesActiveTurnEvent(parameters))
        {
            return;
        }

        ActiveTurnState? turnState;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
        }

        if (turnState is null)
        {
            return;
        }

        string? lastAgentMessage = parameters?["msg"]?["last_agent_message"]?.Value<string>();
        PublishAssistantOutput(turnState, lastAgentMessage, skipIfAlreadyPublished: true);
    }

    private void HandleTokenCountEvent(JToken? parameters)
    {
        if (!this.MatchesActiveTurnEvent(parameters))
        {
            return;
        }

        JToken? info = parameters?["msg"]?["info"];
        long tokensInContextWindow = GetNestedToken(info, "last_token_usage", "total_tokens")?.Value<long?>()
            ?? GetNestedToken(info, "lastTokenUsage", "totalTokens")?.Value<long?>()
            ?? GetNestedToken(info, "total_token_usage", "total_tokens")?.Value<long?>()
            ?? GetNestedToken(info, "totalTokenUsage", "totalTokens")?.Value<long?>()
            ?? 0L;
        long? contextWindow = GetNestedToken(info, "model_context_window")?.Value<long?>()
            ?? GetNestedToken(info, "modelContextWindow")?.Value<long?>();

        lock (this._syncRoot)
        {
            this._activeTurn?.OnTokenUsage?.Invoke(tokensInContextWindow, contextWindow);
        }
    }

    private void HandleMcpToolCallProgress(JToken? parameters)
    {
        if (!this.MatchesActiveTurn(parameters?["turnId"]?.Value<string>()))
        {
            return;
        }

        ActiveTurnState? turnState;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
        }

        string? message = parameters?["message"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(message))
        {
            turnState?.OnEventMessage?.Invoke(new ChatMessage(false, message.Trim(), isEvent: true, title: this.GetLocalization().EventMcpProgressTitle));
        }
    }

    private void HandleCompletedItem(JToken? parameters)
    {
        if (!this.MatchesActiveTurn(parameters?["turnId"]?.Value<string>()))
        {
            return;
        }

        JToken? item = parameters?["item"];
        string? itemType = item?["type"]?.Value<string>();

        ActiveTurnState? turnState;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
        }

        if (turnState is null)
        {
            return;
        }

        if (string.Equals(itemType, "plan", StringComparison.OrdinalIgnoreCase))
        {
            string? itemId = item?["id"]?.Value<string>();
            string? planText;
            lock (this._syncRoot)
            {
                planText = turnState.GetPlanText(itemId);
            }

            if (string.IsNullOrWhiteSpace(planText))
            {
                planText = NormalizeDetail(item?["text"]?.Value<string>(), maxLength: null);
            }

            turnState.OnEventMessage?.Invoke(CreatePlanEventMessage(planText));
            return;
        }

        if (!string.Equals(itemType, "agentMessage", StringComparison.OrdinalIgnoreCase))
        {
            ChatMessage? eventMessage = BuildThreadEventMessage(item);
            if (eventMessage is not null && eventMessage.IsEvent)
            {
                turnState.OnEventMessage?.Invoke(eventMessage);
            }

            return;
        }

        string? agentItemId = item?["id"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(agentItemId) && turnState.StreamedItemIds.Contains(agentItemId!))
        {
            return;
        }

        string? text = item?["text"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = ExtractText(item?["content"]);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            PublishAssistantOutput(turnState, text, skipIfAlreadyPublished: false);
        }
    }

    private void HandleTurnCompleted(JToken? parameters)
    {
        if (!this.MatchesActiveTurn(parameters?["turn"]?["id"]?.Value<string>()))
        {
            return;
        }

        string? status = parameters?["turn"]?["status"]?.Value<string>();
        string? errorMessage = GetNestedString(parameters?["turn"], "error", "message");
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            this.PublishError(errorMessage + Environment.NewLine);
        }

        ActiveTurnState? turnState;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
        }

        string? latestDiff = turnState?.LatestDiff;
        if (!string.IsNullOrWhiteSpace(latestDiff))
        {
            turnState?.OnEventMessage?.Invoke(CreateDiffEventMessage(latestDiff!));
        }

        lock (this._syncRoot)
        {
            turnState?.TrySetResult(string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }
    }

    private void HandleTurnDiffUpdated(JToken? parameters)
    {
        if (!this.MatchesActiveTurn(parameters?["turnId"]?.Value<string>()))
        {
            return;
        }

        string? diff = parameters?["diff"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(diff))
        {
            return;
        }

        lock (this._syncRoot)
        {
            if (this._activeTurn is not null)
            {
                this._activeTurn.LatestDiff = diff;
            }
        }
    }

    private void HandleTokenUsageUpdated(JToken? parameters)
    {
        if (!this.MatchesActiveTurn(parameters?["turnId"]?.Value<string>()))
        {
            return;
        }

        JToken? tokenUsage = parameters?["tokenUsage"];
        long tokensInContextWindow = GetNestedToken(tokenUsage, "last", "totalTokens")?.Value<long?>()
            ?? GetNestedToken(tokenUsage, "lastTokenUsage", "totalTokens")?.Value<long?>()
            ?? GetNestedToken(tokenUsage, "last_token_usage", "total_tokens")?.Value<long?>()
            ?? GetNestedToken(tokenUsage, "total", "totalTokens")?.Value<long?>()
            ?? GetNestedToken(tokenUsage, "totalTokenUsage", "totalTokens")?.Value<long?>()
            ?? GetNestedToken(tokenUsage, "total_token_usage", "total_tokens")?.Value<long?>()
            ?? 0L;
        long? contextWindow = GetNestedToken(parameters?["tokenUsage"], "modelContextWindow")?.Value<long?>();

        lock (this._syncRoot)
        {
            this._activeTurn?.OnTokenUsage?.Invoke(tokensInContextWindow, contextWindow);
        }
    }

    private bool MatchesActiveTurn(string? turnId)
    {
        lock (this._syncRoot)
        {
            return this._activeTurn is not null && (string.IsNullOrWhiteSpace(this._activeTurn.TurnId) || string.Equals(this._activeTurn.TurnId, turnId, StringComparison.Ordinal));
        }
    }

    private bool MatchesActiveTurnEvent(JToken? parameters)
    {
        string? turnId = parameters?["id"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(turnId))
        {
            turnId = parameters?["msg"]?["turn_id"]?.Value<string>();
        }

        return this.MatchesActiveTurn(turnId);
    }

    private static void PublishAssistantOutput(ActiveTurnState turnState, string? text, bool skipIfAlreadyPublished)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (skipIfAlreadyPublished && turnState.HasAssistantOutput)
        {
            return;
        }

        turnState.HasAssistantOutput = true;
        turnState.OnOutput(text);
    }

    private async Task InterruptActiveTurnAsync()
    {
        ActiveTurnState? turnState;
        string? threadId;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
            threadId = this._threadId;
        }

        if (turnState is null || string.IsNullOrWhiteSpace(turnState.TurnId) || string.IsNullOrWhiteSpace(threadId) || turnState.InterruptRequested)
        {
            return;
        }

        turnState.InterruptRequested = true;

        try
        {
            _ = await this.SendRequestAsync(
                "turn/interrupt",
                new { threadId, turnId = turnState.TurnId },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private Task<JToken?> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref this._nextRequestId);
        TaskCompletionSource<JToken?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (this._syncRoot)
        {
            this._pendingRequests[id] = tcs;
        }

        if (cancellationToken.CanBeCanceled)
        {
            _ = cancellationToken.Register(() =>
            {
                lock (this._syncRoot)
                {
                    if (this._pendingRequests.Remove(id))
                    {
                        _ = tcs.TrySetCanceled(cancellationToken);
                    }
                }
            });
        }

        this.WriteMessage(new JObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters is JObject requestParams ? requestParams : JObject.FromObject(parameters)
        });

        return tcs.Task;
    }

    private Task SendNotificationAsync(string method, object parameters)
    {
        this.WriteMessage(new JObject
        {
            ["method"] = method,
            ["params"] = parameters is JObject notificationParams ? notificationParams : JObject.FromObject(parameters)
        });

        return Task.CompletedTask;
    }

    private Task SendResponseAsync(JToken? id, JObject result)
    {
        this.WriteMessage(new JObject
        {
            ["id"] = id,
            ["result"] = result
        });

        return Task.CompletedTask;
    }

    private void WriteMessage(JObject message)
    {
        StreamWriter? writer;
        lock (this._syncRoot)
        {
            writer = this._serverInput;
        }

        if (writer is null)
        {
            throw new InvalidOperationException(this.GetLocalization().AppServerUnavailable);
        }

        string json = message.ToString(Formatting.None);
        lock (this._writeLock)
        {
            writer.WriteLine(json);
            writer.Flush();
        }
    }

    private object BuildThreadStartParams(CodexExtensionSettings settings, string workingDirectory)
    {
        return new
        {
            cwd = workingDirectory,
            approvalPolicy = NormalizeApprovalPolicy(settings.ApprovalPolicy),
            sandbox = NormalizeSandboxMode(settings.SandboxMode),
            model = string.IsNullOrWhiteSpace(settings.DefaultModel) ? null : settings.DefaultModel,
            serviceTier = NormalizeServiceTier(settings.ServiceTier),
            personality = "pragmatic",
            persistExtendedHistory = true
        };
    }

    private object BuildTurnStartParams(string threadId, string prompt, CodexExtensionSettings settings, string workingDirectory, IEnumerable<string> imagePaths, string ideContextSummary)
    {
        return this.BuildTurnStartParams(threadId, prompt, settings, workingDirectory, imagePaths, ideContextSummary, includeExecutionOverrides: true, includeCollaborationMode: true, includeVerbosity: true);
    }

    private JObject BuildTurnStartParams(
        string threadId,
        string prompt,
        CodexExtensionSettings settings,
        string workingDirectory,
        IEnumerable<string> imagePaths,
        string ideContextSummary,
        bool includeExecutionOverrides,
        bool includeCollaborationMode,
        bool includeVerbosity)
    {
        object[] input = this.BuildUserInput(prompt, settings, workingDirectory, imagePaths, ideContextSummary);
        JObject result = new()
        {
            ["threadId"] = threadId,
            ["cwd"] = workingDirectory,
            ["input"] = JArray.FromObject(input)
        };

        if (!includeExecutionOverrides)
        {
            return result;
        }

        AddIfNotBlank(result, "model", settings.DefaultModel);
        AddIfNotBlank(result, "effort", settings.ReasoningEffort);
        result["approvalPolicy"] = NormalizeApprovalPolicy(settings.ApprovalPolicy);
        result["sandboxPolicy"] = JToken.FromObject(BuildSandboxPolicy(settings.SandboxMode));
        AddIfNotBlank(result, "serviceTier", NormalizeServiceTier(settings.ServiceTier));

        if (includeVerbosity)
        {
            AddIfNotBlank(result, "verbosity", settings.ModelVerbosity);
        }

        if (includeCollaborationMode)
        {
            result["collaborationMode"] = BuildCollaborationMode(settings, includeVerbosity);
        }

        return result;
    }

    private async Task<JToken?> StartTurnWithFallbackAsync(
        ActiveTurnState turnState,
        string threadId,
        string prompt,
        CodexExtensionSettings settings,
        string workingDirectory,
        IEnumerable<string> imagePaths,
        string ideContextSummary,
        CancellationToken cancellationToken)
    {
        List<(string Label, bool IncludeExecutionOverrides, bool IncludeCollaborationMode, bool IncludeVerbosity)> attempts =
        [
            ("full", true, true, true),
            ("no-verbosity", true, true, false),
            ("no-collaboration-mode", true, false, false),
            ("minimal", false, false, false)
        ];

        Exception? lastError = null;
        for (int index = 0; index < attempts.Count; index++)
        {
            (string Label, bool IncludeExecutionOverrides, bool IncludeCollaborationMode, bool IncludeVerbosity) attempt = attempts[index];
            try
            {
                JObject requestParams = this.BuildTurnStartParams(
                    threadId,
                    prompt,
                    settings,
                    workingDirectory,
                    imagePaths,
                    ideContextSummary,
                    attempt.IncludeExecutionOverrides,
                    attempt.IncludeCollaborationMode,
                    attempt.IncludeVerbosity);
                return await this.SendRequestAsync("turn/start", requestParams, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                if (index < attempts.Count - 1)
                {
                    turnState.OnError("[turn/start fallback:" + attempt.Label + "] " + ex.Message + Environment.NewLine);
                    this.RestartServer(clearConfig: false);
                    await this.EnsureServerReadyAsync(settings, workingDirectory, cancellationToken).ConfigureAwait(false);
                    await this.EnsureThreadReadyAsync(settings, workingDirectory, settings.CurrentThreadId, cancellationToken).ConfigureAwait(false);
                    threadId = this._threadId ?? settings.CurrentThreadId ?? threadId;
                }
            }
        }

        throw lastError ?? new InvalidOperationException(new LocalizationService(settings.LanguageOverride).StartTurnFailedMessage);
    }

    private static object BuildThreadResumeParams(string threadId, CodexExtensionSettings settings, string workingDirectory)
    {
        return new
        {
            threadId,
            cwd = workingDirectory,
            approvalPolicy = NormalizeApprovalPolicy(settings.ApprovalPolicy),
            sandbox = NormalizeSandboxMode(settings.SandboxMode),
            model = string.IsNullOrWhiteSpace(settings.DefaultModel) ? null : settings.DefaultModel,
            serviceTier = NormalizeServiceTier(settings.ServiceTier),
            personality = "pragmatic",
            persistExtendedHistory = true
        };
    }

    private static JObject? BuildCollaborationMode(CodexExtensionSettings settings, bool includeVerbosity)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultModel))
        {
            return null;
        }

        JObject modeSettings = new()
        {
            ["model"] = settings.DefaultModel
        };

        AddIfNotBlank(modeSettings, "reasoning_effort", settings.ReasoningEffort);
        if (includeVerbosity)
        {
            AddIfNotBlank(modeSettings, "verbosity", settings.ModelVerbosity);
        }

        // Default must be sent explicitly, otherwise an existing thread can stay in Plan mode.
        return new JObject
        {
            ["mode"] = settings.PlanModeEnabled ? "plan" : "default",
            ["settings"] = modeSettings
        };
    }

    private static void AddIfNotBlank(JObject target, string propertyName, string? value)
    {
        value = (value ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[propertyName] = value;
        }
    }

    private static object BuildSandboxPolicy(string sandboxMode)
    {
        return NormalizeSandboxMode(sandboxMode) switch
        {
            "read-only" => new
            {
                type = "readOnly",
                networkAccess = false
            },
            "workspace-write" => new
            {
                type = "workspaceWrite",
                networkAccess = false
            },
            _ => new
            {
                type = "dangerFullAccess"
            },
        };
    }

    private object[] BuildUserInput(string prompt, CodexExtensionSettings settings, string workingDirectory, IEnumerable<string> imagePaths, string ideContextSummary)
    {
        LocalizationService localization = new(settings.LanguageOverride);
        List<object> inputs = [];

        string baseContext = localization.ExtensionContextPrefix + Path.GetFullPath(workingDirectory) + "\".";
        inputs.Add(new
        {
            type = "text",
            text = baseContext
        });

        if (!string.IsNullOrWhiteSpace(ideContextSummary))
        {
            inputs.Add(new
            {
                type = "text",
                text = ideContextSummary
            });
        }

        string preferredMcpContext = BuildPreferredMcpContext(settings, localization);
        if (!string.IsNullOrWhiteSpace(preferredMcpContext))
        {
            inputs.Add(new
            {
                type = "text",
                text = preferredMcpContext
            });
        }

        inputs.Add(new
        {
            type = "text",
            text = prompt
        });

        foreach (object mention in this.ExtractMentionInputs(prompt, workingDirectory))
        {
            inputs.Add(mention);
        }

        foreach (object skill in this.ExtractSkillInputs(prompt))
        {
            inputs.Add(skill);
        }

        foreach (string? imagePath in imagePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            inputs.Add(new
            {
                type = "localImage",
                path = Path.GetFullPath(imagePath)
            });
        }

        return inputs.ToArray();
    }

    private static string BuildPreferredMcpContext(CodexExtensionSettings settings, LocalizationService localization)
    {
        string[]? preferredServers = settings.PreferredMcpServers?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return preferredServers is { Length: > 0 }
            ? localization.PreferredMcpPrefix + " " + string.Join(", ", preferredServers) + "."
            : string.Empty;
    }

    private void PublishRateLimitsUpdate(JObject? parameters)
    {
        RateLimitsUpdated?.Invoke(this.BuildRateLimitSummary(parameters));
    }

    private CodexRateLimitSummary BuildRateLimitSummary(JToken? response)
    {
        IReadOnlyList<JObject> snapshots = ResolveRateLimitSnapshots(response);
        if (snapshots.Count == 0)
        {
            return new CodexRateLimitSummary();
        }

        LocalizationService localization = new(this._languageOverride);

        return new CodexRateLimitSummary
        {
            Entries = BuildRateLimitEntries(snapshots, localization)
        };
    }

    private static IReadOnlyList<JObject> ResolveRateLimitSnapshots(JToken? response)
    {
        List<JObject> snapshots = [];
        HashSet<string> seenKeys = new(StringComparer.OrdinalIgnoreCase);

        AppendRateLimitSnapshotsFromMap(snapshots, seenKeys, response?["rateLimitsByLimitId"]);
        AppendRateLimitSnapshotsFromMap(snapshots, seenKeys, response?["rate_limits_by_limit_id"]);
        AppendRateLimitSnapshotsFromMap(snapshots, seenKeys, response?["account"]?["rateLimitsByLimitId"]);
        AppendRateLimitSnapshotsFromMap(snapshots, seenKeys, response?["account"]?["rate_limits_by_limit_id"]);

        AddRateLimitSnapshot(snapshots, seenKeys, response?["rateLimits"]);
        AddRateLimitSnapshot(snapshots, seenKeys, response?["rate_limits"]);
        AddRateLimitSnapshot(snapshots, seenKeys, response?["account"]?["rateLimits"]);
        AddRateLimitSnapshot(snapshots, seenKeys, response?["account"]?["rate_limits"]);
        AddRateLimitSnapshot(snapshots, seenKeys, response);

        return snapshots;
    }

    private static void AppendRateLimitSnapshotsFromMap(List<JObject> snapshots, HashSet<string> seenKeys, JToken? token)
    {
        if (token is not JObject snapshotMap)
        {
            return;
        }

        foreach (JProperty property in snapshotMap.Properties())
        {
            AddRateLimitSnapshot(snapshots, seenKeys, property.Value, property.Name);
        }
    }

    private static void AddRateLimitSnapshot(List<JObject> snapshots, HashSet<string> seenKeys, JToken? token, string? fallbackKey = null)
    {
        if (token is not JObject snapshot || !LooksLikeRateLimitSnapshot(snapshot))
        {
            return;
        }

        string identity = ReadString(snapshot, "limitId", "limit_id", "limitName", "limit_name")
            ?? fallbackKey
            ?? snapshot.ToString(Formatting.None);

        if (seenKeys.Add(identity))
        {
            snapshots.Add(snapshot);
        }
    }

    private static bool LooksLikeRateLimitSnapshot(JObject snapshot)
    {
        return snapshot["primary"] is not null
            || snapshot["primaryWindow"] is not null
            || snapshot["primary_window"] is not null
            || snapshot["secondary"] is not null
            || snapshot["secondaryWindow"] is not null
            || snapshot["secondary_window"] is not null
            || snapshot["windows"] is not null
            || snapshot["credits"] is not null
            || snapshot["creditBalance"] is not null
            || snapshot["credit_balance"] is not null
            || snapshot["planType"] is not null
            || snapshot["plan_type"] is not null
            || snapshot["limitId"] is not null
            || snapshot["limit_id"] is not null
            || snapshot["limitName"] is not null
            || snapshot["limit_name"] is not null;
    }

    private static IReadOnlyList<CodexRateLimitWindowSummary> BuildRateLimitEntries(
        IReadOnlyList<JObject> snapshots,
        LocalizationService localization)
    {
        List<CodexRateLimitWindowSummary> entries = [];

        foreach (JObject? snapshot in snapshots
                     .OrderBy(GetRateLimitSortPriority)
                     .ThenBy(GetRateLimitDisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            string limitDisplayName = GetRateLimitDisplayName(snapshot);
            CodexRateLimitWindowSummary primary = BuildRateLimitWindowEntry(
                limitDisplayName,
                ResolveRateLimitWindow(snapshot, "primary", "primaryWindow", "primary_window", "short"),
                localization);
            if (primary.HasData)
            {
                entries.Add(primary);
            }

            CodexRateLimitWindowSummary secondary = BuildRateLimitWindowEntry(
                limitDisplayName,
                ResolveRateLimitWindow(snapshot, "secondary", "secondaryWindow", "secondary_window", "long"),
                localization);
            if (secondary.HasData)
            {
                entries.Add(secondary);
            }
        }

        JObject? planSnapshot = snapshots.FirstOrDefault(snapshot => !string.IsNullOrWhiteSpace(ReadString(snapshot, "planType", "plan_type", "plan", "tier")));
        if (planSnapshot is not null)
        {
            CodexRateLimitWindowSummary planEntry = BuildPlanEntry(planSnapshot, localization);
            if (planEntry.HasData)
            {
                entries.Add(planEntry);
            }
        }

        JObject? creditsSnapshot = snapshots.FirstOrDefault(snapshot => ResolveCreditsToken(snapshot) is not null);
        if (creditsSnapshot is not null)
        {
            CodexRateLimitWindowSummary creditsEntry = BuildCreditsEntry(ResolveCreditsToken(creditsSnapshot), localization);
            if (creditsEntry.HasData)
            {
                entries.Add(creditsEntry);
            }
        }

        return entries;
    }

    private static int GetRateLimitSortPriority(JObject snapshot)
    {
        string? rawName = ReadString(snapshot, "limitName", "limit_name", "limitId", "limit_id");
        return string.IsNullOrWhiteSpace(rawName) || string.Equals(rawName, "codex", StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;
    }

    private static string GetRateLimitDisplayName(JObject snapshot)
    {
        string? rawName = ReadString(snapshot, "limitName", "limit_name", "limitId", "limit_id");
        if (string.IsNullOrWhiteSpace(rawName)
            || string.Equals(rawName, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return rawName.Replace('_', ' ').Trim();
    }

    private static JToken? ResolveRateLimitWindow(JObject snapshot, params string[] candidateKeys)
    {
        foreach (string key in candidateKeys)
        {
            if (snapshot[key] is JToken direct)
            {
                return direct;
            }
        }

        if (snapshot["windows"] is JArray windows)
        {
            foreach (string key in candidateKeys)
            {
                JObject? match = windows
                    .OfType<JObject>()
                    .FirstOrDefault(window => MatchesRateLimitWindow(window, key));
                if (match is not null)
                {
                    return match;
                }
            }

            if (candidateKeys.Contains("primary", StringComparer.OrdinalIgnoreCase))
            {
                return windows.FirstOrDefault();
            }

            if (candidateKeys.Contains("secondary", StringComparer.OrdinalIgnoreCase))
            {
                return windows.Skip(1).FirstOrDefault();
            }
        }

        return null;
    }

    private static bool MatchesRateLimitWindow(JObject window, string key)
    {
        string name = ReadString(window, "name", "title", "id", "kind") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(name)
            && name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static CodexRateLimitWindowSummary BuildRateLimitWindowEntry(string limitDisplayName, JToken? window, LocalizationService localization)
    {
        if (window is not JObject windowObject)
        {
            return new CodexRateLimitWindowSummary();
        }

        string title = BuildRateLimitWindowTitle(limitDisplayName, windowObject, localization);
        double? usedPercent = ReadPercent(windowObject, "usedPercent", "used_percent", "usagePercent", "usage_percent");
        double? remainingPercent = ReadPercent(windowObject, "remainingPercent", "remaining_percent", "percentRemaining", "percent_remaining");
        DateTimeOffset? resetsAt = ReadResetTime(windowObject);
        decimal? remaining = ReadDecimal(windowObject, "remaining", "remaining_requests", "remainingTokens", "remaining_tokens");
        decimal? limit = ReadDecimal(windowObject, "limit", "total", "max", "quota");
        double? effectiveRemainingPercent = remainingPercent
            ?? (usedPercent.HasValue ? Math.Max(0d, 100d - usedPercent.Value) : null);
        List<string> detailParts = [];

        if (remaining.HasValue || limit.HasValue)
        {
            if (remaining.HasValue && limit.HasValue)
            {
                detailParts.Add(
                    remaining.Value.ToString("0.##", CultureInfo.CurrentUICulture)
                    + " / "
                    + limit.Value.ToString("0.##", CultureInfo.CurrentUICulture));
            }
            else
            {
                decimal? value = remaining ?? limit;
                detailParts.Add(value?.ToString("0.##", CultureInfo.CurrentUICulture) ?? string.Empty);
            }
        }

        if (effectiveRemainingPercent.HasValue)
        {
            detailParts.Add(Math.Round(effectiveRemainingPercent.Value).ToString("0", CultureInfo.CurrentUICulture) + "% " + localization.RateLimitRemainingSuffix);
        }

        if (resetsAt.HasValue)
        {
            detailParts.Add(localization.RateLimitResetsPrefix + " " + resetsAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture));
        }

        return new CodexRateLimitWindowSummary
        {
            Title = title?.Trim() ?? string.Empty,
            Detail = string.Join("   ", detailParts.Where(part => !string.IsNullOrWhiteSpace(part)))
        };
    }

    private static string BuildRateLimitWindowTitle(string limitDisplayName, JObject window, LocalizationService localization)
    {
        string? explicitTitle = ReadString(window, "title", "name");
        string windowLabel = !string.IsNullOrWhiteSpace(explicitTitle) && !IsGenericRateLimitWindowName(explicitTitle)
            ? explicitTitle.Trim()
            : BuildRateLimitWindowDurationLabel(ReadDurationMinutes(window), localization);

        if (string.IsNullOrWhiteSpace(limitDisplayName))
        {
            return windowLabel;
        }

        if (string.IsNullOrWhiteSpace(windowLabel))
        {
            return limitDisplayName;
        }

        return limitDisplayName + " " + windowLabel;
    }

    private static bool IsGenericRateLimitWindowName(string value)
    {
        return string.Equals(value, "primary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "secondary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "short", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "long", StringComparison.OrdinalIgnoreCase);
    }

    private static CodexRateLimitWindowSummary BuildPlanEntry(JObject snapshot, LocalizationService localization)
    {
        string? planType = ReadString(snapshot, "planType", "plan_type", "plan", "tier");
        return string.IsNullOrWhiteSpace(planType)
            ? new CodexRateLimitWindowSummary()
            : new CodexRateLimitWindowSummary
            {
                Title = localization.PlanLabelShort,
                Detail = planType
            };
    }

    private static CodexRateLimitWindowSummary BuildCreditsEntry(JToken? credits, LocalizationService localization)
    {
        if (credits is not JObject creditsObject)
        {
            return new CodexRateLimitWindowSummary();
        }

        bool? hasCredits = ReadBoolean(creditsObject, "hasCredits", "has_credits");
        bool? unlimited = ReadBoolean(creditsObject, "unlimited");
        decimal? balance = ReadDecimal(creditsObject, "balance", "available", "available_credits");

        if (unlimited == true)
        {
            return new CodexRateLimitWindowSummary
            {
                Title = localization.CreditsLabel,
                Detail = localization.RateLimitUnlimitedLabel
            };
        }

        if (balance.HasValue)
        {
            return new CodexRateLimitWindowSummary
            {
                Title = localization.CreditsLabel,
                Detail = balance.Value.ToString("0.##", CultureInfo.CurrentUICulture)
            };
        }

        return hasCredits == true
            ? new CodexRateLimitWindowSummary
            {
                Title = localization.CreditsLabel
            }
            : new CodexRateLimitWindowSummary();
    }

    private static JToken? ResolveCreditsToken(JObject snapshot)
    {
        return snapshot["credits"]
            ?? snapshot["creditBalance"]
            ?? snapshot["credit_balance"];
    }

    private static double? ReadPercent(JObject source, params string[] keys)
    {
        foreach (string key in keys)
        {
            double? value = source[key]?.Value<double?>();
            if (!value.HasValue)
            {
                continue;
            }

            return value.Value <= 1d ? value.Value * 100d : value.Value;
        }

        return null;
    }

    private static decimal? ReadDecimal(JToken? source, params string[] keys)
    {
        if (source is not JObject sourceObject)
        {
            return null;
        }

        foreach (string key in keys)
        {
            JToken? token = sourceObject[key];
            if (token is null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                continue;
            }

            if (token.Type is JTokenType.Integer or JTokenType.Float)
            {
                return token.Value<decimal?>();
            }

            string? text = token.Value<string>();
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal invariantValue)
                || decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentUICulture, out invariantValue))
            {
                return invariantValue;
            }
        }

        return null;
    }

    private static bool? ReadBoolean(JToken? source, params string[] keys)
    {
        if (source is not JObject sourceObject)
        {
            return null;
        }

        foreach (string key in keys)
        {
            JToken? token = sourceObject[key];
            if (token is null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                continue;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            string? text = token.Value<string>();
            if (bool.TryParse(text, out bool parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? ReadString(JToken? source, params string[] keys)
    {
        if (source is not JObject sourceObject)
        {
            return null;
        }

        foreach (string key in keys)
        {
            string? value = sourceObject[key]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadResetTime(JObject source)
    {
        long? unixSeconds = source["resetsAt"]?.Value<long?>()
            ?? source["resets_at"]?.Value<long?>()
            ?? source["resetAt"]?.Value<long?>()
            ?? source["reset_at"]?.Value<long?>();
        if (unixSeconds.HasValue && unixSeconds.Value > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value);
        }

        string? resetText = source["resetsAt"]?.Value<string>()
            ?? source["resets_at"]?.Value<string>()
            ?? source["resetAt"]?.Value<string>()
            ?? source["reset_at"]?.Value<string>();
        if (DateTimeOffset.TryParse(resetText, out DateTimeOffset resetAt))
        {
            return resetAt;
        }

        return null;
    }

    private static int? ReadDurationMinutes(JObject source)
    {
        return source["windowDurationMins"]?.Value<int?>()
            ?? source["window_duration_mins"]?.Value<int?>()
            ?? source["durationMinutes"]?.Value<int?>()
            ?? source["duration_minutes"]?.Value<int?>()
            ?? source["windowMinutes"]?.Value<int?>()
            ?? source["window_minutes"]?.Value<int?>();
    }

    private static string BuildRateLimitWindowDurationLabel(int? durationMinutes, LocalizationService localization)
    {
        if (!durationMinutes.HasValue || durationMinutes.Value <= 0)
        {
            return string.Empty;
        }

        if (durationMinutes.Value == 10080)
        {
            return localization.RateLimitWeeklyLabel;
        }

        if (durationMinutes.Value >= 60 && durationMinutes.Value % 60 == 0)
        {
            return (durationMinutes.Value / 60) + "h";
        }

        return durationMinutes.Value + "m";
    }

    private IEnumerable<object> ExtractMentionInputs(string prompt, string workingDirectory)
    {
        foreach (Match match in MentionRegex.Matches(prompt ?? string.Empty))
        {
            string rawValue = match.Groups["value"].Value.Trim();
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            if (rawValue.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
            {
                yield return new
                {
                    type = "mention",
                    name = rawValue,
                    path = rawValue
                };

                continue;
            }

            string? candidatePath = ResolveMentionPath(rawValue, workingDirectory);
            if (candidatePath is null)
            {
                continue;
            }

            yield return new
            {
                type = "mention",
                name = Path.GetFileName(candidatePath),
                path = candidatePath
            };
        }
    }

    private IEnumerable<object> ExtractSkillInputs(string prompt)
    {
        Dictionary<string, string> skillsSnapshot;
        lock (this._syncRoot)
        {
            skillsSnapshot = new Dictionary<string, string>(this._skillsByName, StringComparer.OrdinalIgnoreCase);
        }

        foreach (Match match in SkillRegex.Matches(prompt ?? string.Empty))
        {
            string skillName = match.Groups["value"].Value.Trim();
            if (!skillsSnapshot.TryGetValue(skillName, out string? skillPath))
            {
                continue;
            }

            yield return new
            {
                type = "skill",
                name = skillName,
                path = skillPath
            };
        }
    }

    private static string? ResolveMentionPath(string rawValue, string workingDirectory)
    {
        try
        {
            string candidate = Path.IsPathRooted(rawValue)
                ? rawValue
                : Path.Combine(workingDirectory, rawValue.Replace('/', Path.DirectorySeparatorChar));

            string fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ExtractText(JToken? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (JToken node in content.SelectTokens("$..text"))
        {
            string? value = node.Value<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                _ = builder.Append(value);
            }
        }

        return builder.ToString();
    }

    private static JToken? GetNestedToken(JToken? token, params string[] path)
    {
        JToken? current = token;
        foreach (string segment in path)
        {
            if (current is not JObject obj)
            {
                return null;
            }

            current = obj[segment];
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static string? GetNestedString(JToken? token, params string[] path)
    {
        JToken? current = GetNestedToken(token, path);
        if (current is null)
        {
            return null;
        }

        return current.Type switch
        {
            JTokenType.String => current.Value<string>(),
            JTokenType.Null => null,
            JTokenType.Undefined => null,
            _ => current.ToString(Formatting.None)
        };
    }

    private void NotifyThreadCatalogChanged()
    {
        try
        {
            ThreadCatalogChanged?.Invoke();
        }
        catch
        {
        }
    }

    private static CodexThreadSummary? ParseThreadSummary(JToken? thread, string? activeThreadId)
    {
        string? threadId = thread?["id"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        string preview = (thread?["preview"]?.Value<string>() ?? string.Empty).Trim();
        string? name = thread?["name"]?.Value<string>();
        string? sessionPath = thread?["path"]?.Value<string>();
        long updatedAt = thread?["updatedAt"]?.Value<long?>() ?? 0L;
        string status = GetNestedString(thread, "status", "type") ?? string.Empty;
        string sanitizedPreview = TryReadThreadTitleFromSession(sessionPath);
        if (string.IsNullOrWhiteSpace(sanitizedPreview))
        {
            sanitizedPreview = SanitizeThreadPreview(preview);
        }

        return new CodexThreadSummary
        {
            ThreadId = threadId,
            Name = name,
            Preview = string.IsNullOrWhiteSpace(sanitizedPreview)
                ? (string.IsNullOrWhiteSpace(name) ? threadId : string.Empty)
                : sanitizedPreview,
            UpdatedAt = updatedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(updatedAt).ToLocalTime() : DateTimeOffset.MinValue,
            Status = status,
            IsActive = string.Equals(threadId, activeThreadId, StringComparison.Ordinal)
        };
    }

    private static string TryReadThreadTitleFromSession(string? sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
        {
            return string.Empty;
        }

        try
        {
            using StreamReader reader = new(sessionPath);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JObject entry = JObject.Parse(line);
                string userMessagePrompt = ExtractPromptFromSessionEvent(entry);
                if (!string.IsNullOrWhiteSpace(userMessagePrompt))
                {
                    return BuildSummary(userMessagePrompt, userMessagePrompt);
                }

                if (!string.Equals(entry["type"]?.Value<string>(), "response_item", StringComparison.Ordinal))
                {
                    continue;
                }

                JToken? payload = entry["payload"];
                if (!string.Equals(payload?["type"]?.Value<string>(), "message", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.Equals(payload?["role"]?.Value<string>(), "user", StringComparison.Ordinal))
                {
                    continue;
                }

                string prompt = ExtractPromptFromSessionMessage(payload?["content"] as JArray);
                return string.IsNullOrWhiteSpace(prompt) ? string.Empty : BuildSummary(prompt, prompt);
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ExtractPromptFromSessionEvent(JObject entry)
    {
        if (!string.Equals(entry["type"]?.Value<string>(), "event_msg", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        JToken? payload = entry["payload"];
        if (!string.Equals(payload?["type"]?.Value<string>(), "user_message", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return SanitizeThreadPreview(payload?["message"]?.Value<string>() ?? string.Empty);
    }

    private static string ExtractPromptFromSessionMessage(JArray? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        List<string> segments = [];
        foreach (JToken? item in content)
        {
            if (!string.Equals(item?["type"]?.Value<string>(), "input_text", StringComparison.Ordinal))
            {
                continue;
            }

            string? text = item?["text"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            string trimmed = text.Trim();
            if (StartsWithAny(trimmed, ExtensionContextPrefixes)
                || StartsWithAny(trimmed, IdeContextPrefixes)
                || StartsWithAny(trimmed, PreferredMcpPrefixes))
            {
                continue;
            }

            segments.Add(trimmed);
        }

        return string.Join(" ", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
    }

    private static string SanitizeThreadPreview(string preview)
    {
        string trimmed = preview?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (!ContainsInjectedContext(trimmed))
        {
            return BuildSummary(trimmed, trimmed);
        }

        string extractedPrompt = TryExtractPromptFromThreadPreview(trimmed);
        if (!string.IsNullOrWhiteSpace(extractedPrompt))
        {
            return BuildSummary(extractedPrompt, extractedPrompt);
        }

        return string.Empty;
    }

    private static bool ContainsInjectedContext(string text)
    {
        return ContainsAny(text, ExtensionContextPrefixes)
            || ContainsAny(text, IdeContextPrefixes)
            || ContainsAny(text, PreferredMcpPrefixes);
    }

    private static bool StartsWithAny(string text, IEnumerable<string> prefixes)
    {
        return prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool ContainsAny(string text, IEnumerable<string> prefixes)
    {
        return prefixes.Any(prefix => text.IndexOf(prefix, StringComparison.Ordinal) >= 0);
    }

    private static string[] CreateLocalizedSet(params Func<LocalizationService, string>[] selectors)
    {
        string[] languages = new[] { "pt-BR", "en", "es", "fr", "de" };
        return languages
            .SelectMany(language =>
            {
                LocalizationService localization = new(language);
                return selectors.Select(selector => selector(localization));
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string TryExtractPromptFromThreadPreview(string preview)
    {
        string text = preview.Trim();
        text = RemoveLeadingExtensionContext(text);
        text = RemoveLeadingPreferredMcpContext(text);
        text = RemoveLeadingIdeContextPrefix(text);

        while (TryStripLeadingIdeContextLine(ref text))
        {
        }

        if (StartsWithIdeContextLine(text))
        {
            string trailingPrompt = ExtractPromptFromTrailingContextLine(text);
            return CompactSingleLine(trailingPrompt);
        }

        return CompactSingleLine(text);
    }

    private static string RemoveLeadingExtensionContext(string text)
    {
        foreach (string prefix in ExtensionContextPrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            int closingQuoteIndex = text.IndexOf('"', prefix.Length);
            if (closingQuoteIndex < 0)
            {
                return string.Empty;
            }

            int nextIndex = closingQuoteIndex + 1;
            if (nextIndex < text.Length && text[nextIndex] == '.')
            {
                nextIndex++;
            }

            return text.Substring(nextIndex).TrimStart();
        }

        return text;
    }

    private static string RemoveLeadingPreferredMcpContext(string text)
    {
        foreach (string prefix in PreferredMcpPrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            int sentenceEndIndex = text.IndexOf('.');
            if (sentenceEndIndex < 0)
            {
                return string.Empty;
            }

            return text.Substring(sentenceEndIndex + 1).TrimStart();
        }

        return text;
    }

    private static string RemoveLeadingIdeContextPrefix(string text)
    {
        foreach (string prefix in IdeContextPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return text.Substring(prefix.Length).TrimStart();
            }
        }

        return text;
    }

    private static bool TryStripLeadingIdeContextLine(ref string text)
    {
        foreach (string prefix in IdeContextLinePrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            int newlineIndex = text.IndexOf('\n');
            if (newlineIndex < 0)
            {
                return false;
            }

            text = text.Substring(newlineIndex + 1).TrimStart();
            return true;
        }

        return false;
    }

    private static bool StartsWithIdeContextLine(string text)
    {
        return IdeContextLinePrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string ExtractPromptFromTrailingContextLine(string text)
    {
        foreach (string prefix in IdeContextLinePrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string remainder = text.Substring(prefix.Length).TrimStart();
            Match match = TrailingPromptAfterPathRegex.Match(remainder);
            if (match.Success)
            {
                return match.Groups["prompt"].Value.Trim();
            }

            return string.Empty;
        }

        return text;
    }

    private static IReadOnlyList<ChatMessage> ParseThreadMessages(JToken thread, string? threadId, string? sessionPath)
    {
        IReadOnlyList<ChatMessage> sessionMessages = ReadMessagesFromSession(sessionPath);
        if (sessionMessages.Any(message => message.IsUser))
        {
            return sessionMessages;
        }

        List<ChatMessage> messages = [];
        if (thread["turns"] is not JArray turns)
        {
            return sessionMessages.Count > 0 ? sessionMessages : messages;
        }

        IReadOnlyList<string> fallbackPrompts = ReadPromptHistoryForThread(threadId);
        int fallbackPromptIndex = 0;

        foreach (JToken turn in turns)
        {
            if (turn["items"] is not JArray items)
            {
                continue;
            }

            List<ChatMessage> turnMessages = [];
            foreach (JToken item in items)
            {
                ChatMessage? message = ParseThreadMessage(item);
                if (message is not null)
                {
                    turnMessages.Add(message);
                }
            }

            if (!turnMessages.Any(message => message.IsUser) && fallbackPromptIndex < fallbackPrompts.Count)
            {
                messages.Add(new ChatMessage(true, fallbackPrompts[fallbackPromptIndex]));
                fallbackPromptIndex++;
            }
            else if (turnMessages.Any(message => message.IsUser))
            {
                fallbackPromptIndex++;
            }

            messages.AddRange(turnMessages);
        }

        while (fallbackPromptIndex < fallbackPrompts.Count)
        {
            messages.Add(new ChatMessage(true, fallbackPrompts[fallbackPromptIndex]));
            fallbackPromptIndex++;
        }

        return messages;
    }

    private static IReadOnlyList<ChatMessage> ReadMessagesFromSession(string? sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
        {
            return Array.Empty<ChatMessage>();
        }

        List<ChatMessage> responseMessages = [];
        List<ChatMessage> eventMessages = [];
        try
        {
            using StreamReader reader = new(sessionPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JObject entry = JObject.Parse(line);
                ChatMessage? responseMessage = ParseSessionResponseMessage(entry);
                if (responseMessage is not null)
                {
                    responseMessages.Add(responseMessage);
                    continue;
                }

                ChatMessage? eventMessage = ParseSessionEventMessage(entry);
                if (eventMessage is not null)
                {
                    eventMessages.Add(eventMessage);
                }
            }
        }
        catch
        {
        }

        return responseMessages.Count > 0 ? responseMessages : eventMessages;
    }

    private static ChatMessage? ParseSessionResponseMessage(JObject entry)
    {
        if (!string.Equals(entry["type"]?.Value<string>(), "response_item", StringComparison.Ordinal))
        {
            return null;
        }

        JToken? payload = entry["payload"];
        if (!string.Equals(payload?["type"]?.Value<string>(), "message", StringComparison.Ordinal))
        {
            return null;
        }

        return ParseRoleMessage(payload);
    }

    private static ChatMessage? ParseSessionEventMessage(JObject entry)
    {
        if (!string.Equals(entry["type"]?.Value<string>(), "event_msg", StringComparison.Ordinal))
        {
            return null;
        }

        JToken? payload = entry["payload"];
        string? payloadType = payload?["type"]?.Value<string>();
        switch (payloadType)
        {
            case "user_message":
                string userText = NormalizeUserMessageTextSegment(payload?["message"]?.Value<string>());
                return string.IsNullOrWhiteSpace(userText) ? null : new ChatMessage(true, userText);

            case "agent_message":
                string? agentText = payload?["message"]?.Value<string>();
                return string.IsNullOrWhiteSpace(agentText) ? null : new ChatMessage(false, agentText);

            default:
                return null;
        }
    }

    private static string? FindSessionPathForThread(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        string sessionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");

        if (!Directory.Exists(sessionsRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).IndexOf(threadId, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadPromptHistoryForThread(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return Array.Empty<string>();
        }

        string historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "history.jsonl");

        if (!File.Exists(historyPath))
        {
            return Array.Empty<string>();
        }

        List<string> prompts = [];
        try
        {
            using StreamReader reader = new(historyPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JObject entry = JObject.Parse(line);
                if (!string.Equals(entry["session_id"]?.Value<string>(), threadId, StringComparison.Ordinal))
                {
                    continue;
                }

                string prompt = NormalizeUserMessageTextSegment(entry["text"]?.Value<string>());
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    prompts.Add(prompt);
                }
            }
        }
        catch
        {
        }

        return prompts;
    }

    private static ChatMessage? ParseThreadMessage(JToken? item)
    {
        string? itemType = item?["type"]?.Value<string>();
        switch (itemType)
        {
            case "message":
                return ParseRoleMessage(item);

            case "userMessage":
                string userText = ExtractUserMessageText(item?["content"]);
                if (string.IsNullOrWhiteSpace(userText))
                {
                    userText = ExtractUserMessageText(item?["text"] ?? item?["message"]);
                }

                return string.IsNullOrWhiteSpace(userText) ? null : new ChatMessage(true, userText);

            case "agentMessage":
                string agentText = ExtractAgentMessageText(item);
                return string.IsNullOrWhiteSpace(agentText) ? null : new ChatMessage(false, agentText);

            case "plan":
                return BuildThreadEventMessage(item);

            case "reasoning":
            case "commandExecution":
            case "fileChange":
            case "mcpToolCall":
            case "dynamicToolCall":
            case "collabAgentToolCall":
            case "webSearch":
            case "imageView":
            case "imageGeneration":
            case "enteredReviewMode":
            case "exitedReviewMode":
            case "contextCompaction":
                return null;

            default:
                return null;
        }
    }

    private static ChatMessage? ParseRoleMessage(JToken? item)
    {
        string? role = item?["role"]?.Value<string>();
        if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
        {
            string userText = ExtractUserMessageText(item?["content"]);
            if (string.IsNullOrWhiteSpace(userText))
            {
                userText = ExtractUserMessageText(item?["text"] ?? item?["message"]);
            }

            return string.IsNullOrWhiteSpace(userText) ? null : new ChatMessage(true, userText);
        }

        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "agent", StringComparison.OrdinalIgnoreCase))
        {
            string agentText = ExtractAgentMessageText(item);
            return string.IsNullOrWhiteSpace(agentText) ? null : new ChatMessage(false, agentText);
        }

        return null;
    }

    private static string ExtractAgentMessageText(JToken? item)
    {
        string? text = item?["text"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = ExtractText(item?["content"]);
        }

        return text ?? string.Empty;
    }

    private static string ExtractUserMessageText(JToken? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content.Type == JTokenType.String)
        {
            return NormalizeUserMessageTextSegment(content.Value<string>());
        }

        if (content is JObject obj)
        {
            return ExtractUserMessageText(new JArray(obj));
        }

        if (content is not JArray items)
        {
            return string.Empty;
        }

        List<string> segments = [];
        foreach (JToken? item in items)
        {
            switch (item?["type"]?.Value<string>())
            {
                case "text":
                case "input_text":
                    string? text = item?["text"]?.Value<string>();
                    string normalizedText = NormalizeUserMessageTextSegment(text);
                    if (!string.IsNullOrWhiteSpace(normalizedText))
                    {
                        segments.Add(normalizedText);
                    }
                    break;

                case "localImage":
                case "image":
                case "input_image":
                    segments.Add("[image]");
                    break;

                case "mention":
                    string? mention = item?["name"]?.Value<string>() ?? item?["path"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(mention))
                    {
                        segments.Add("@" + mention);
                    }
                    break;

                case "skill":
                    string? skill = item?["name"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(skill))
                    {
                        segments.Add("$" + skill);
                    }
                    break;

                default:
                    string fallbackText = NormalizeUserMessageTextSegment(item?["text"]?.Value<string>());
                    if (!string.IsNullOrWhiteSpace(fallbackText))
                    {
                        segments.Add(fallbackText);
                    }
                    break;
            }
        }

        return string.Join(" ", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
    }

    private static string NormalizeUserMessageTextSegment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmed = text.Trim();
        if (StartsWithAny(trimmed, ExtensionContextPrefixes)
            || StartsWithAny(trimmed, IdeContextPrefixes)
            || StartsWithAny(trimmed, PreferredMcpPrefixes)
            || StartsWithAny(trimmed, SyntheticUserContextPrefixes))
        {
            return string.Empty;
        }

        return trimmed;
    }

    private static ChatMessage? BuildThreadEventMessage(JToken? item)
    {
        string? itemType = item?["type"]?.Value<string>();
        switch (itemType)
        {
            case "plan":
                string? planText = NormalizeDetail(item?["text"]?.Value<string>(), maxLength: null);
                return CreatePlanEventMessage(planText);

            default:
                LocalizationService localization = new();
                switch (itemType)
                {
                    case "reasoning":
                        string? reasoningText = NormalizeDetail(JoinTextArray(item?["summary"]));
                        return CreateEventMessage(localization.EventReasoningTitle, BuildSummary(reasoningText, localization.EventReasoningUpdated), reasoningText);

                    case "commandExecution":
                        string? command = item?["command"]?.Value<string>();
                        string? status = item?["status"]?.Value<string>();
                        int? exitCode = item?["exitCode"]?.Value<int?>();
                        long? durationMs = item?["durationMs"]?.Value<long?>();
                        string? aggregatedOutput = NormalizeDetail(item?["aggregatedOutput"]?.Value<string>());
                        string? cwd = item?["cwd"]?.Value<string>();
                        return CreateEventMessage(
                            localization.EventCommandTitle,
                            BuildCommandSummary(command, status, exitCode, durationMs, localization),
                            BuildDetailSections(
                                string.IsNullOrWhiteSpace(cwd) ? null : localization.EventWorkingDirectoryLabel + Environment.NewLine + cwd.Trim(),
                                string.IsNullOrWhiteSpace(aggregatedOutput) ? null : localization.EventOutputLabel + Environment.NewLine + aggregatedOutput));

                    case "fileChange":
                        JArray? changes = item?["changes"] as JArray;
                        return CreateEventMessage(localization.EventFileChangesTitle, BuildFileChangeSummary(changes, localization), BuildFileChangeDetail(changes, localization));

                    case "mcpToolCall":
                        string? server = item?["server"]?.Value<string>();
                        string? tool = item?["tool"]?.Value<string>();
                        string? toolLabel = string.IsNullOrWhiteSpace(server) ? tool : server + "." + tool;
                        string? toolStatus = item?["status"]?.Value<string>();
                        long? toolDurationMs = item?["durationMs"]?.Value<long?>();
                        string? errorMessage = GetNestedString(item, "error", "message");
                        bool? mcpSuccess = null;
                        if (!string.IsNullOrWhiteSpace(errorMessage))
                        {
                            mcpSuccess = false;
                        }
                        else if (string.Equals(toolStatus, "completed", StringComparison.OrdinalIgnoreCase))
                        {
                            mcpSuccess = true;
                        }
                        else if (string.Equals(toolStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(toolStatus, "canceled", StringComparison.OrdinalIgnoreCase))
                        {
                            mcpSuccess = false;
                        }

                        return CreateEventMessage(
                            localization.EventMcpToolTitle,
                            BuildToolSummary(toolLabel, toolStatus, toolDurationMs, localization, mcpSuccess),
                            BuildDetailSections(
                                BuildNamedJsonBlock(localization.EventArgumentsLabel, item?["arguments"]),
                                string.IsNullOrWhiteSpace(errorMessage) ? null : localization.EventErrorLabel + Environment.NewLine + errorMessage.Trim(),
                                BuildNamedTextBlock(localization.EventResultLabel, ExtractContentText(GetNestedToken(item, "result", "content")) ?? SerializeStructuredValue(item?["result"]))));

                    case "dynamicToolCall":
                        string? dynamicTool = item?["tool"]?.Value<string>();
                        bool? success = item?["success"]?.Value<bool?>();
                        return CreateEventMessage(
                            localization.EventToolTitle,
                            BuildToolSummary(dynamicTool, item?["status"]?.Value<string>(), item?["durationMs"]?.Value<long?>(), localization, success),
                            BuildDetailSections(
                                BuildNamedJsonBlock(localization.EventArgumentsLabel, item?["arguments"]),
                                BuildNamedTextBlock(localization.EventOutputLabel, ExtractContentText(item?["contentItems"]) ?? SerializeStructuredValue(item?["contentItems"]))));

                    case "collabAgentToolCall":
                        string? collabTool = item?["tool"]?.Value<string>();
                        string receiverIds = item?["receiverThreadIds"] is JArray receivers
                            ? string.Join(", ", receivers.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)))
                            : string.Empty;
                        return CreateEventMessage(
                            localization.EventAgentToolTitle,
                            BuildSummary(string.IsNullOrWhiteSpace(receiverIds) ? collabTool : collabTool + " -> " + receiverIds, localization.EventAgentToolUsed),
                            BuildDetailSections(
                                BuildNamedTextBlock(localization.EventPromptLabel, NormalizeDetail(item?["prompt"]?.Value<string>())),
                                BuildNamedJsonBlock(localization.EventArgumentsLabel, item?["arguments"])));

                    case "webSearch":
                        string? query = item?["query"]?.Value<string>();
                        return CreateEventMessage(
                            localization.EventWebSearchTitle,
                            BuildSummary(query, localization.EventWebSearchTitle),
                            BuildNamedTextBlock(localization.EventResultLabel, ExtractContentText(GetNestedToken(item, "result", "content")) ?? SerializeStructuredValue(item?["result"])));

                    case "imageView":
                        return CreateEventMessage(localization.EventImageViewTitle, BuildSummary(item?["path"]?.Value<string>(), localization.EventImageViewed));

                    case "imageGeneration":
                        return CreateEventMessage(
                            localization.EventImageGenerationTitle,
                            BuildSummary(item?["status"]?.Value<string>(), localization.EventImageGenerated),
                            BuildNamedTextBlock(localization.EventPromptLabel, NormalizeDetail(item?["prompt"]?.Value<string>())));

                    case "enteredReviewMode":
                        return CreateEventMessage(localization.EventReviewModeTitle, BuildSummary(item?["review"]?.Value<string>(), localization.EventEnteredReviewMode));

                    case "exitedReviewMode":
                        return CreateEventMessage(localization.EventReviewModeTitle, BuildSummary(item?["review"]?.Value<string>(), localization.EventExitedReviewMode));

                    case "contextCompaction":
                        return CreateEventMessage(localization.EventContextTitle, localization.EventConversationContextCompacted);

                    default:
                        return null;
                }
        }
    }

    private static ChatMessage CreatePlanEventMessage(string? planText)
    {
        LocalizationService localization = new();
        return CreateEventMessage(
            localization.EventPlanTitle,
            BuildSummary(planText, localization.EventPlanUpdated),
            planText,
            detailMaxLength: null,
            supportsMarkdownDetail: true);
    }

    private static ChatMessage CreateDiffEventMessage(string diff)
    {
        LocalizationService localization = new();
        return CreateEventMessage(
            localization.EventFileChangesTitle,
            BuildSummary(diff, localization.EventUpdatedFiles),
            "```diff" + Environment.NewLine + diff.Trim() + Environment.NewLine + "```",
            detailMaxLength: null,
            supportsMarkdownDetail: true);
    }

    private static ChatMessage? CreateEventMessage(
        string title,
        string? summary,
        string? detail = null,
        int? detailMaxLength = 2200,
        bool supportsMarkdownDetail = false)
    {
        string normalizedSummary = BuildSummary(summary, title);
        string? normalizedDetail = NormalizeDetail(detail, detailMaxLength);
        if (string.Equals(normalizedSummary, CompactSingleLine(normalizedDetail), StringComparison.Ordinal))
        {
            normalizedDetail = null;
        }

        return new ChatMessage(
            false,
            normalizedSummary,
            isEvent: true,
            title: title,
            detail: normalizedDetail,
            supportsMarkdownText: false,
            supportsMarkdownDetail: supportsMarkdownDetail);
    }

    private static string? BuildStructuredPlanMarkdown(string? explanation, JArray? plan, LocalizationService localization)
    {
        List<string> sections = [];
        string? normalizedExplanation = NormalizeDetail(explanation, maxLength: null);
        if (!string.IsNullOrWhiteSpace(normalizedExplanation))
        {
            sections.Add(normalizedExplanation);
        }

        if (plan is not null)
        {
            List<string> items = [];
            foreach (JToken? step in plan)
            {
                string? stepText = NormalizeDetail(step?["step"]?.Value<string>(), maxLength: null);
                if (string.IsNullOrWhiteSpace(stepText))
                {
                    continue;
                }

                items.Add(FormatPlanStep(stepText, step?["status"]?.Value<string>(), localization));
            }

            if (items.Count > 0)
            {
                sections.Add(string.Join(Environment.NewLine, items));
            }
        }

        return sections.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static string FormatPlanStep(string stepText, string? status, LocalizationService localization)
    {
        return NormalizePlanStatus(status) switch
        {
            "completed" => "- [x] " + stepText,
            "inprogress" => "- [ ] **" + localization.EventInProgressStatus + ":** " + stepText,
            "pending" => "- [ ] " + stepText,
            _ => "- " + stepText
        };
    }

    private static string NormalizePlanStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return string.Empty;
        }

        return status
            .Trim()
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Replace(" ", string.Empty)
            .ToLowerInvariant();
    }

    private static string BuildCommandSummary(string? command, string? status, int? exitCode, long? durationMs, LocalizationService localization)
    {
        List<string> parts = [];
        string compactCommand = CompactSingleLine(command);
        if (!string.IsNullOrWhiteSpace(compactCommand))
        {
            parts.Add(Truncate(compactCommand, 120));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            parts.Add("[" + status!.Trim() + "]");
        }

        if (exitCode.HasValue)
        {
            parts.Add("exit " + exitCode.Value);
        }

        string durationSuffix = BuildDurationSuffix(durationMs);
        if (!string.IsNullOrWhiteSpace(durationSuffix))
        {
            parts.Add(durationSuffix);
        }

        return parts.Count == 0 ? localization.EventCommandExecuted : string.Join(" ", parts);
    }

    private static string BuildFileChangeSummary(JArray? changes, LocalizationService localization)
    {
        if (changes is null || changes.Count == 0)
        {
            return localization.EventUpdatedFiles;
        }

        List<string> parts = [];
        foreach (JToken? change in changes.Take(3))
        {
            string? path = change?["path"]?.Value<string>();
            string? kind = GetNestedString(change, "kind", "type");
            if (!string.IsNullOrWhiteSpace(path))
            {
                parts.Add(string.IsNullOrWhiteSpace(kind) ? path! : kind + " " + path);
            }
        }

        if (changes.Count > 3)
        {
            parts.Add(string.Format(CultureInfo.CurrentUICulture, localization.EventMoreFormat, changes.Count - 3));
        }

        return string.Join(", ", parts);
    }

    private static string? BuildFileChangeDetail(JArray? changes, LocalizationService localization)
    {
        if (changes is null || changes.Count == 0)
        {
            return null;
        }

        List<string> details = [];
        foreach (JToken? change in changes.Take(6))
        {
            string? path = change?["path"]?.Value<string>();
            string? kind = GetNestedString(change, "kind", "type");
            string header = BuildSummary(string.IsNullOrWhiteSpace(kind) ? path : kind + " " + path, localization.EventFileUpdated);
            string? diff = NormalizeDetail(change?["diff"]?.Value<string>());
            details.Add(string.IsNullOrWhiteSpace(diff) ? header : header + Environment.NewLine + Truncate(diff, 700));
        }

        if (changes.Count > 6)
        {
            details.Add(string.Format(CultureInfo.CurrentUICulture, localization.EventMoreFilesFormat, changes.Count - 6));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, details);
    }

    private static string JoinTextArray(JToken? token)
    {
        if (token is not JArray values)
        {
            return string.Empty;
        }

        return string.Join(" ", values.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildToolSummary(string? label, string? status, long? durationMs, LocalizationService localization, bool? success = null)
    {
        List<string> parts = [];
        string compactLabel = CompactSingleLine(label);
        if (!string.IsNullOrWhiteSpace(compactLabel))
        {
            parts.Add(Truncate(compactLabel, 120));
        }

        if (success.HasValue)
        {
            parts.Add(success.Value ? "[" + localization.EventCompletedStatus + "]" : "[" + localization.EventFailedStatus + "]");
        }
        else if (!string.IsNullOrWhiteSpace(status))
        {
            parts.Add("[" + status!.Trim() + "]");
        }

        string durationSuffix = BuildDurationSuffix(durationMs);
        if (!string.IsNullOrWhiteSpace(durationSuffix))
        {
            parts.Add(durationSuffix);
        }

        return parts.Count == 0 ? localization.EventToolCall : string.Join(" ", parts);
    }

    private static string BuildSummary(string? value, string fallback)
    {
        string compact = CompactSingleLine(value);
        return string.IsNullOrWhiteSpace(compact) ? fallback : Truncate(compact, 180);
    }

    private static string CompactSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string compact = Regex.Replace(value.Replace("\r", " ").Replace("\n", " "), @"\s+", " ");
        return compact.Trim();
    }

    private static string? NormalizeDetail(string? value, int? maxLength = 2200)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return maxLength.HasValue ? Truncate(normalized, maxLength.Value) : normalized;
    }

    private static string? BuildNamedTextBlock(string label, string? value)
    {
        string? normalized = NormalizeDetail(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : label + Environment.NewLine + normalized;
    }

    private static string? BuildNamedJsonBlock(string label, JToken? value)
    {
        string? serialized = SerializeStructuredValue(value);
        return string.IsNullOrWhiteSpace(serialized) ? null : label + Environment.NewLine + serialized;
    }

    private static string? SerializeStructuredValue(JToken? value)
    {
        if (value is null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined)
        {
            return null;
        }

        if (value.Type == JTokenType.String)
        {
            return NormalizeDetail(value.Value<string>());
        }

        return NormalizeDetail(value.ToString(Formatting.Indented));
    }

    private static string? ExtractContentText(JToken? content)
    {
        if (content is null)
        {
            return null;
        }

        List<string> textParts = content.SelectTokens("$..text")
            .Values<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct()
            .ToList();

        if (textParts.Count == 0)
        {
            return null;
        }

        return NormalizeDetail(string.Join(Environment.NewLine, textParts));
    }

    private static string? BuildDetailSections(params string?[] sections)
    {
        List<string> parts = sections
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .Select(section => section!.Trim())
            .ToList();

        return parts.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private static string BuildDurationSuffix(long? durationMs)
    {
        if (!durationMs.HasValue || durationMs.Value <= 0)
        {
            return string.Empty;
        }

        TimeSpan duration = TimeSpan.FromMilliseconds(durationMs.Value);
        if (duration.TotalSeconds < 1)
        {
            return durationMs.Value + " ms";
        }

        if (duration.TotalMinutes < 1)
        {
            return duration.TotalSeconds.ToString("0.0") + " s";
        }

        if (duration.TotalHours < 1)
        {
            return duration.Minutes + "m " + duration.Seconds.ToString("00") + "s";
        }

        return ((int)duration.TotalHours) + "h " + duration.Minutes.ToString("00") + "m";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value.Substring(0, maxLength - 3).TrimEnd() + "...";
    }

    private void PublishError(string text)
    {
        ActiveTurnState? turnState;
        lock (this._syncRoot)
        {
            turnState = this._activeTurn;
        }

        turnState?.OnError(text);
    }

    private void FailPendingOperations(string message)
    {
        List<TaskCompletionSource<JToken?>> pendingRequests;
        ActiveTurnState? turnState;

        lock (this._syncRoot)
        {
            pendingRequests = this._pendingRequests.Values.ToList();
            this._pendingRequests.Clear();
            turnState = this._activeTurn;
            this._threadId = null;
            this._threadConfigKey = null;
            this._threadLoaded = false;
            this._skillsCacheKey = null;
            this._serverInput = null;
            this._serverProcess = null;
        }

        foreach (TaskCompletionSource<JToken?> pendingRequest in pendingRequests)
        {
            _ = pendingRequest.TrySetException(new InvalidOperationException(message));
        }

        turnState?.OnError(message + Environment.NewLine);
        turnState?.TrySetResult(1);
    }

    private void RestartServer(bool clearConfig)
    {
        Process? process;
        StreamWriter? input;

        lock (this._syncRoot)
        {
            process = this._serverProcess;
            input = this._serverInput;
            this._serverProcess = null;
            this._serverInput = null;
            this._initializedTcs = null;
            this._threadId = null;
            this._threadConfigKey = null;
            this._threadLoaded = false;
            this._skillsCacheKey = null;
            this._skillsByName.Clear();

            if (clearConfig)
            {
                this._serverConfigKey = null;
            }
        }

        try
        {
            input?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
        }
        finally
        {
            process?.Dispose();
        }
    }

    private CodexApprovalRequest BuildApprovalRequest(string method, JToken? parameters)
    {
        IReadOnlyList<CodexApprovalOption> options = string.Equals(method, "item/commandExecution/requestApproval", StringComparison.Ordinal)
            ? BuildCommandApprovalOptions(parameters?["availableDecisions"] as JArray, parameters?["proposedExecpolicyAmendment"] as JArray)
            : BuildFileChangeApprovalOptions();

        return new CodexApprovalRequest
        {
            Method = method,
            ThreadId = parameters?["threadId"]?.Value<string>() ?? string.Empty,
            TurnId = parameters?["turnId"]?.Value<string>() ?? string.Empty,
            ItemId = parameters?["itemId"]?.Value<string>() ?? string.Empty,
            ApprovalId = parameters?["approvalId"]?.Value<string>(),
            Command = parameters?["command"]?.Value<string>(),
            WorkingDirectory = parameters?["cwd"]?.Value<string>(),
            Reason = parameters?["reason"]?.Value<string>(),
            GrantRoot = parameters?["grantRoot"]?.Value<string>(),
            ProposedExecpolicyLabel = parameters?["proposedExecpolicyAmendment"]?.Type == JTokenType.Array
                ? string.Join(" ", parameters["proposedExecpolicyAmendment"]!.Values<string>())
                : null,
            Options = options
        };
    }

    private static CodexUserInputRequest BuildUserInputRequest(JToken? parameters)
    {
        CodexUserInputRequest request = new()
        {
            ThreadId = parameters?["threadId"]?.Value<string>() ?? string.Empty,
            TurnId = parameters?["turnId"]?.Value<string>() ?? string.Empty,
            ItemId = parameters?["itemId"]?.Value<string>() ?? string.Empty
        };

        if (parameters?["questions"] is not JArray questions)
        {
            return request;
        }

        List<CodexUserInputQuestion> items = [];
        foreach (JToken? question in questions)
        {
            JArray? options = question?["options"] as JArray;
            List<CodexUserInputOption> mappedOptions = [];
            if (options is not null)
            {
                foreach (JToken? option in options)
                {
                    mappedOptions.Add(new CodexUserInputOption
                    {
                        Label = option?["label"]?.Value<string>() ?? string.Empty,
                        Description = option?["description"]?.Value<string>() ?? string.Empty
                    });
                }
            }

            items.Add(new CodexUserInputQuestion
            {
                Header = question?["header"]?.Value<string>() ?? string.Empty,
                Id = question?["id"]?.Value<string>() ?? string.Empty,
                Question = question?["question"]?.Value<string>() ?? string.Empty,
                IsOther = question?["isOther"]?.Value<bool>() ?? false,
                IsSecret = question?["isSecret"]?.Value<bool>() ?? false,
                Options = mappedOptions
            });
        }

        request.Questions = items;
        return request;
    }

    private async Task<JToken> ResolveApprovalDecisionAsync(CodexApprovalRequest request)
    {
        string info = JsonConvert.SerializeObject(request, Formatting.None);
        if (!string.IsNullOrWhiteSpace(info))
        {
            this.PublishError("[" + this.GetLocalization().OutputTagApproval + "] " + info + Environment.NewLine);
        }

        if (this.ApprovalRequestHandler is null)
        {
            return GetDefaultDeclineDecision(request.Method);
        }

        try
        {
            JToken? decision = await this.ApprovalRequestHandler.Invoke(request).ConfigureAwait(false);
            return decision ?? GetDefaultDeclineDecision(request.Method);
        }
        catch (Exception ex)
        {
            this.PublishError("[" + this.GetLocalization().OutputTagApproval + "] " + ex.Message + Environment.NewLine);
            return GetDefaultDeclineDecision(request.Method);
        }
    }

    private async Task<JObject?> ResolveUserInputRequestAsync(CodexUserInputRequest request)
    {
        if (this.UserInputRequestHandler is null)
        {
            return new JObject { ["answers"] = new JObject() };
        }

        try
        {
            return await this.UserInputRequestHandler.Invoke(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.PublishError("[" + this.GetLocalization().OutputTagUserInput + "] " + ex.Message + Environment.NewLine);
            return new JObject { ["answers"] = new JObject() };
        }
    }

    private LocalizationService GetLocalization()
    {
        return new LocalizationService(this._languageOverride);
    }

    private static IReadOnlyList<CodexApprovalOption> BuildCommandApprovalOptions(JArray? availableDecisions, JArray? proposedExecpolicyAmendment)
    {
        List<CodexApprovalOption> options = [];
        if (availableDecisions is not null)
        {
            foreach (JToken decision in availableDecisions)
            {
                CodexApprovalOption? option = CreateCommandApprovalOption(decision, proposedExecpolicyAmendment);
                if (option is not null)
                {
                    options.Add(option);
                }
            }
        }

        if (options.Count == 0)
        {
            options.Add(new CodexApprovalOption("accept", JValue.CreateString("accept")));
            options.Add(new CodexApprovalOption("decline", JValue.CreateString("decline")));
            options.Add(new CodexApprovalOption("cancel", JValue.CreateString("cancel")));
        }

        return options;
    }

    private static CodexApprovalOption? CreateCommandApprovalOption(JToken decision, JArray? proposedExecpolicyAmendment)
    {
        if (decision.Type == JTokenType.String)
        {
            string? key = decision.Value<string>();
            return string.IsNullOrWhiteSpace(key) ? null : new CodexApprovalOption(key!, JValue.CreateString(key!));
        }

        if (decision["acceptWithExecpolicyAmendment"] is not null && proposedExecpolicyAmendment is not null)
        {
            return new CodexApprovalOption(
                "acceptWithExecpolicyAmendment",
                new JObject
                {
                    ["acceptWithExecpolicyAmendment"] = new JObject
                    {
                        ["execpolicy_amendment"] = proposedExecpolicyAmendment.DeepClone()
                    }
                });
        }

        if (decision["applyNetworkPolicyAmendment"] is not null)
        {
            return new CodexApprovalOption("applyNetworkPolicyAmendment", decision.DeepClone());
        }

        return null;
    }

    private static IReadOnlyList<CodexApprovalOption> BuildFileChangeApprovalOptions()
    {
        return
        [
            new CodexApprovalOption("accept", JValue.CreateString("accept")),
            new CodexApprovalOption("decline", JValue.CreateString("decline")),
            new CodexApprovalOption("cancel", JValue.CreateString("cancel"))
        ];
    }

    private static JToken GetDefaultDeclineDecision(string method)
    {
        return JValue.CreateString(string.Equals(method, "item/fileChange/requestApproval", StringComparison.Ordinal) ? "decline" : "cancel");
    }

    private static string BuildServerArguments(CodexExtensionSettings settings)
    {
        List<string> args = ["app-server", "--listen", "stdio://"];

        if (!HasProfileArgument(settings.AdditionalArguments) && !string.IsNullOrWhiteSpace(settings.Profile))
        {
            args.Add("--profile");
            args.Add(settings.Profile.Trim());
        }

        foreach (string line in BuildManagedMcpOverrideLines(settings))
        {
            args.Add("-c");
            args.Add(line);
        }

        if (!string.IsNullOrWhiteSpace(settings.RawTomlOverrides))
        {
            foreach (string? line in settings.RawTomlOverrides.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                args.Add("-c");
                args.Add(line.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.AdditionalArguments))
        {
            foreach (string token in SplitArguments(settings.AdditionalArguments))
            {
                args.Add(token);
            }
        }

        return JoinArguments(args);
    }

    private static string BuildServerConfigKey(CodexExtensionSettings settings)
    {
        string resolvedExecutablePath = CodexExecutableResolver.ResolveExecutableLocation(settings.CodexExecutablePath, settings.EnvironmentVariables);
        return string.Join("\n", new[]
        {
            string.IsNullOrWhiteSpace(resolvedExecutablePath)
                ? CodexExecutableResolver.NormalizeConfiguredExecutablePath(settings.CodexExecutablePath)
                : resolvedExecutablePath,
            settings.Profile ?? string.Empty,
            string.Join("\n", BuildManagedMcpOverrideLines(settings)),
            settings.RawTomlOverrides ?? string.Empty,
            settings.AdditionalArguments ?? string.Empty,
            settings.EnvironmentVariables ?? string.Empty
        });
    }

    private static IEnumerable<string> BuildManagedMcpOverrideLines(CodexExtensionSettings settings)
    {
        if (settings.ManagedMcpServers is null)
        {
            yield break;
        }

        foreach (CodexManagedMcpServer? server in settings.ManagedMcpServers)
        {
            if (server is null || !server.Enabled)
            {
                continue;
            }

            string name = (server.Name ?? string.Empty).Trim();
            if (!IsValidManagedMcpName(name))
            {
                continue;
            }

            if (string.Equals(server.TransportType, "url", StringComparison.OrdinalIgnoreCase))
            {
                string url = (server.Url ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                yield return "[mcp_servers." + name + "]";
                yield return "url = " + EncodeTomlString(url);
                continue;
            }

            string command = (server.Command ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            yield return "[mcp_servers." + name + "]";
            yield return "command = " + EncodeTomlString(command);

            List<string> args = SplitManagedMcpArguments(server.Arguments).ToList();
            if (args.Count > 0)
            {
                yield return "args = [" + string.Join(", ", args.Select(EncodeTomlString)) + "]";
            }
        }
    }

    private static bool IsValidManagedMcpName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-');
    }

    private static IEnumerable<string> SplitManagedMcpArguments(string? text)
    {
        return (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static string EncodeTomlString(string value)
    {
        return "\"" + (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"") + "\"";
    }

    private static string BuildSkillScopeLabel(string path, string workingDirectory, string homeSkillsDirectory, bool isSystem)
    {
        if (isSystem)
        {
            return "System";
        }

        if (path.StartsWith(homeSkillsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return "Global";
        }

        if (path.StartsWith(workingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return "Workspace";
        }

        return "External";
    }

    private static string BuildThreadConfigKey(CodexExtensionSettings settings, string workingDirectory)
    {
        return string.Join("\n", new[]
        {
            workingDirectory,
            settings.DefaultModel ?? string.Empty,
            NormalizeServiceTier(settings.ServiceTier) ?? string.Empty,
            NormalizeApprovalPolicy(settings.ApprovalPolicy),
            NormalizeSandboxMode(settings.SandboxMode)
        });
    }

    private static string NormalizeApprovalPolicy(string approvalPolicy)
    {
        return string.IsNullOrWhiteSpace(approvalPolicy) ? "never" : approvalPolicy;
    }

    private static string NormalizeSandboxMode(string sandboxMode)
    {
        return string.IsNullOrWhiteSpace(sandboxMode) ? "danger-full-access" : sandboxMode;
    }

    private static string? NormalizeServiceTier(string? serviceTier)
    {
        string normalized = (serviceTier ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized switch
        {
            "fast" => "fast",
            "flex" => "flex",
            _ => normalized
        };
    }

    private static ProcessStartInfo BuildStartInfo(string executablePath, string arguments, string workingDirectory)
    {
        string resolvedWorkingDirectory = ResolveWorkingDirectory(workingDirectory);
        if (IsPowerShellScript(executablePath))
        {
            return new ProcessStartInfo
            {
                FileName = ResolvePowerShellHost(),
                WorkingDirectory = resolvedWorkingDirectory,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(executablePath) + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments)
            };
        }

        if (!RequiresCommandShell(executablePath))
        {
            return new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = resolvedWorkingDirectory,
                Arguments = arguments
            };
        }

        string commandShell = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandShell))
        {
            commandShell = "cmd.exe";
        }

        return new ProcessStartInfo
        {
            FileName = commandShell,
            WorkingDirectory = resolvedWorkingDirectory,
            Arguments = "/d /s /c \"" + QuoteForCommandShell(executablePath) + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments) + "\""
        };
    }

    private static string ResolveWorkingDirectory(string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            return workingDirectory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string NormalizeComparablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string normalized = path.Trim();
        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = @"\\" + normalized.Substring(@"\\?\UNC\".Length);
        }
        else if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(@"\\?\".Length);
        }

        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
        }

        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ToWindowsDevicePath(string path)
    {
        if (!IsWindows() || string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path.Substring(2);
        }

        return @"\\?\" + path;
    }

    private static void ApplyEnvironmentVariables(ProcessStartInfo psi, string environmentVariables)
    {
        foreach (string? line in environmentVariables.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = line.Substring(0, separatorIndex).Trim();
            string value = line.Substring(separatorIndex + 1).Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                psi.EnvironmentVariables[key] = value;
            }
        }
    }

    private static string JoinArguments(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (!value.Any(ch => char.IsWhiteSpace(ch) || ch == '"' || ch == '\\'))
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static bool RequiresCommandShell(string executablePath)
    {
        if (!IsWindows())
        {
            return false;
        }

        string extension = Path.GetExtension(executablePath);
        return string.IsNullOrEmpty(extension)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPowerShellScript(string executablePath)
    {
        return IsWindows()
            && Path.GetExtension(executablePath).Equals(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteForCommandShell(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string ResolvePowerShellHost()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string windowsPowerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell)
            ? windowsPowerShell
            : "powershell.exe";
    }

    private static bool IsWindows()
    {
        return Environment.OSVersion.Platform == PlatformID.Win32NT;
    }

    private static IEnumerable<string> SplitArguments(string commandLine)
    {
        StringBuilder current = new();
        bool inQuotes = false;

        foreach (char ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    _ = current.Clear();
                }

                continue;
            }

            _ = current.Append(ch);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static bool HasProfileArgument(string? commandLine)
    {
        foreach (string token in SplitArguments(commandLine ?? string.Empty))
        {
            if (string.Equals(token, "--profile", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-p", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ActiveTurnState
    {
        public ActiveTurnState(Action<string> onOutput, Action<string> onError, Action<ChatMessage>? onEventMessage, Action<long, long?>? onTokenUsage)
        {
            this.OnOutput = onOutput;
            this.OnError = onError;
            this.OnEventMessage = onEventMessage;
            this.OnTokenUsage = onTokenUsage;
            this.Completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource<int> Completion { get; }
        public Action<string> OnOutput { get; }
        public Action<string> OnError { get; }
        public Action<ChatMessage>? OnEventMessage { get; }
        public Action<long, long?>? OnTokenUsage { get; }
        public HashSet<string> StreamedItemIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, StringBuilder> PlanTextByItemId { get; } = new(StringComparer.Ordinal);
        public string? TurnId { get; set; }
        public string? LatestDiff { get; set; }
        public bool HasAssistantOutput { get; set; }
        public bool InterruptRequested { get; set; }

        public string AppendPlanDelta(string? itemId, string delta)
        {
            string key = string.IsNullOrWhiteSpace(itemId) ? "__plan__" : itemId!;
            if (!this.PlanTextByItemId.TryGetValue(key, out StringBuilder? builder))
            {
                builder = new StringBuilder();
                this.PlanTextByItemId[key] = builder;
            }

            _ = builder.Append(delta);
            return builder.ToString();
        }

        public void SetPlanText(string? itemId, string planText)
        {
            string key = string.IsNullOrWhiteSpace(itemId) ? "__plan__" : itemId!;
            this.PlanTextByItemId[key] = new StringBuilder(planText ?? string.Empty);
        }

        public string? GetPlanText(string? itemId)
        {
            string key = string.IsNullOrWhiteSpace(itemId) ? "__plan__" : itemId!;
            if (this.PlanTextByItemId.TryGetValue(key, out StringBuilder? builder) && builder.Length > 0)
            {
                return builder.ToString();
            }

            if (!string.Equals(key, "__plan__", StringComparison.Ordinal)
                && this.PlanTextByItemId.TryGetValue("__plan__", out StringBuilder? sharedBuilder)
                && sharedBuilder.Length > 0)
            {
                return sharedBuilder.ToString();
            }

            if (this.PlanTextByItemId.Count == 1)
            {
                return this.PlanTextByItemId.Values.First().ToString();
            }

            return null;
        }

        public void TrySetResult(int result)
        {
            _ = this.Completion.TrySetResult(result);
        }
    }
}
