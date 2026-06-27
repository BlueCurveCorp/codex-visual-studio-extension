using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using CodexVsix.Models;

using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

public sealed class CodexEnvironmentService
{
    public const string FallbackInstallCommand = "npm install -g @openai/codex";

    public async Task<CodexEnvironmentStatus> InspectAsync(CodexExtensionSettings settings, CancellationToken cancellationToken)
    {
        LocalizationService localization = new(settings.LanguageOverride);
        string configuredExecutablePath = CodexExecutableResolver.NormalizeConfiguredExecutablePath(settings.CodexExecutablePath);
        CodexEnvironmentStatus status = new()
        {
            Stage = CodexSetupStage.Checking,
            ConfiguredExecutablePath = configuredExecutablePath,
            AuthFilePath = this.GetAuthFilePath(settings.EnvironmentVariables)
        };

        try
        {
            string resolvedExecutablePath = CodexExecutableResolver.ResolveExecutableLocation(configuredExecutablePath, settings.EnvironmentVariables);
            if (string.IsNullOrWhiteSpace(resolvedExecutablePath))
            {
                status.Stage = CodexSetupStage.MissingExecutable;
                return status;
            }

            status.ResolvedExecutablePath = resolvedExecutablePath;

            (bool Success, string Detail) version = await TryGetVersionAsync(resolvedExecutablePath, localization, cancellationToken).ConfigureAwait(false);
            if (!version.Success)
            {
                status.Stage = CodexSetupStage.Error;
                status.ErrorDetail = version.Detail;
                return status;
            }

            status.Version = version.Detail;
            (bool Success, string Detail) appServer = await TryValidateAppServerAsync(resolvedExecutablePath, localization, cancellationToken).ConfigureAwait(false);
            if (!appServer.Success)
            {
                status.Stage = CodexSetupStage.Error;
                status.ErrorDetail = appServer.Detail;
                return status;
            }

            AuthFileInspection authFileInspection = InspectAuthFile(status.AuthFilePath);
            AppServerAuthInspection appServerAuth = await TryReadAppServerAuthStateAsync(resolvedExecutablePath, settings, cancellationToken).ConfigureAwait(false);
            ConfigProviderInspection providerInspection = InspectConfiguredProvider(settings);
            bool hasManagedLogin = string.Equals(appServerAuth.AccountType, "chatgpt", StringComparison.OrdinalIgnoreCase);

            status.HasAuthFile = authFileInspection.HasUsableAuthFile;
            status.HasApiKey = HasOpenAiApiKey(settings.EnvironmentVariables)
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
                || authFileInspection.HasEmbeddedApiKey
                || string.Equals(appServerAuth.AccountType, "apiKey", StringComparison.OrdinalIgnoreCase);
            status.AccountEmail = FirstNonEmptyString(appServerAuth.AccountEmail, authFileInspection.AccountEmail);
            status.RequiresOpenaiAuth = appServerAuth.Success
                ? appServerAuth.RequiresOpenaiAuth
                : providerInspection.RequiresOpenaiAuthFallback;
            status.AuthenticationLabel = BuildAuthenticationLabel(localization, appServerAuth, providerInspection, hasManagedLogin);

            bool hasOpenaiAuthentication = status.HasApiKey || status.HasAuthFile || hasManagedLogin;
            bool hasProviderAuthentication = providerInspection.IsReady;

            status.Stage = status.RequiresOpenaiAuth
                ? (hasOpenaiAuthentication ? CodexSetupStage.Ready : CodexSetupStage.MissingAuthentication)
                : (hasProviderAuthentication ? CodexSetupStage.Ready : CodexSetupStage.MissingAuthentication);

            return status;
        }
        catch (Exception ex)
        {
            status.Stage = CodexSetupStage.Error;
            status.ErrorDetail = ex.Message;
            return status;
        }
    }

    public void LaunchLoginTerminal(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        if (IsPowerShellScript(executablePath))
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = ResolvePowerShellHost(),
                Arguments = "-NoExit -ExecutionPolicy Bypass -File " + QuoteArgument(executablePath) + " login",
                UseShellExecute = true
            });
            return;
        }

        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            string commandShell = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(commandShell))
            {
                commandShell = "cmd.exe";
            }

            _ = Process.Start(new ProcessStartInfo
            {
                FileName = commandShell,
                Arguments = "/k \"" + QuoteForCommandShell(executablePath) + " login\"",
                UseShellExecute = true
            });
            return;
        }

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "login",
            UseShellExecute = true
        });
    }

    public void DeleteAuthFile(string? authFilePath = null)
    {
        string path = string.IsNullOrWhiteSpace(authFilePath)
            ? this.GetAuthFilePath()
            : authFilePath!;

        if (!File.Exists(path))
        {
            return;
        }

        File.Delete(path);
    }

    public string GetAuthFilePath(string? environmentVariables = null)
    {
        return Path.Combine(CodexEnvironmentPathHelper.GetCodexHomeDirectory(environmentVariables), "auth.json");
    }

    private static string BuildAuthenticationLabel(LocalizationService localization, AppServerAuthInspection appServerAuth, ConfigProviderInspection providerInspection, bool hasManagedLogin)
    {
        if (hasManagedLogin)
        {
            return localization.SetupManagedLoginLabel;
        }

        if (appServerAuth.Success && !appServerAuth.RequiresOpenaiAuth)
        {
            return BuildConfigProviderLabel(localization, providerInspection);
        }

        if (!appServerAuth.Success && providerInspection.HasActiveProvider && !providerInspection.RequiresOpenaiAuthFallback)
        {
            return BuildConfigProviderLabel(localization, providerInspection);
        }

        return string.Empty;
    }

    private static string BuildConfigProviderLabel(LocalizationService localization, ConfigProviderInspection providerInspection)
    {
        return !string.IsNullOrWhiteSpace(providerInspection.SelectedProfile)
            ? string.Format(localization.Culture, localization.SetupConfigProfileLabelFormat, providerInspection.SelectedProfile)
            : localization.SetupConfigProviderLabel;
    }

    private static async Task<(bool Success, string Detail)> TryGetVersionAsync(string executablePath, LocalizationService localization, CancellationToken cancellationToken)
    {
        (bool Success, string Detail) probe = await RunProbeAsync(executablePath, "--version", localization.SetupErrorSummary, cancellationToken).ConfigureAwait(false);
        if (!probe.Success)
        {
            return probe;
        }

        string versionText = FirstNonEmptyLine(probe.Detail);
        return (true, string.IsNullOrWhiteSpace(versionText) ? localization.CodexDetectedLabel : versionText);
    }

    private static async Task<(bool Success, string Detail)> TryValidateAppServerAsync(string executablePath, LocalizationService localization, CancellationToken cancellationToken)
    {
        (bool Success, string Detail) probe = await RunProbeAsync(
            executablePath,
            "app-server --help",
            localization.AppServerValidationFailed,
            cancellationToken).ConfigureAwait(false);

        if (probe.Success)
        {
            return probe;
        }

        return (false, localization.AppServerUnsupported + Environment.NewLine + probe.Detail);
    }

    private static async Task<(bool Success, string Detail)> RunProbeAsync(string executablePath, string arguments, string fallbackError, CancellationToken cancellationToken)
    {
        using Process process = new()
        {
            StartInfo = CreateProbeStartInfo(executablePath, arguments)
        };

        try
        {
            if (!process.Start())
            {
                return (false, fallbackError);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        bool exited = await WaitForExitAsync(process, 5000, cancellationToken).ConfigureAwait(false);

        if (!exited)
        {
            TryTerminateProcess(process);
            return (false, fallbackError);
        }

        string output = await outputTask.ConfigureAwait(false);
        string errorText = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            string error = string.IsNullOrWhiteSpace(errorText) ? output : errorText;
            return (false, string.IsNullOrWhiteSpace(error) ? fallbackError : error.Trim());
        }

        return (true, string.IsNullOrWhiteSpace(output) ? errorText : output);
    }

    private static async Task<AppServerAuthInspection> TryReadAppServerAuthStateAsync(string executablePath, CodexExtensionSettings settings, CancellationToken cancellationToken)
    {
        using Process process = new()
        {
            StartInfo = CreateServerProbeStartInfo(executablePath, settings)
        };

        ApplyEnvironmentVariables(process.StartInfo, settings.EnvironmentVariables);

        try
        {
            if (!process.Start())
            {
                return AppServerAuthInspection.Empty;
            }
        }
        catch
        {
            return AppServerAuthInspection.Empty;
        }

        try
        {
            using StreamWriter writer = new(process.StandardInput.BaseStream, new UTF8Encoding(false), 1024, true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            await WriteJsonRpcMessageAsync(writer, new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "codex-vsix", version = "1.0" },
                    capabilities = new { experimentalApi = true }
                }
            }).ConfigureAwait(false);

            JObject? initializeResponse = await ReadJsonRpcResponseAsync(process.StandardOutput, 1, cancellationToken).ConfigureAwait(false);
            if (initializeResponse is null || initializeResponse["error"] is not null)
            {
                return AppServerAuthInspection.Empty;
            }

            await WriteJsonRpcMessageAsync(writer, new
            {
                method = "initialized",
                @params = new { }
            }).ConfigureAwait(false);

            await WriteJsonRpcMessageAsync(writer, new
            {
                id = 2,
                method = "account/read",
                @params = new { refreshToken = false }
            }).ConfigureAwait(false);

            JObject? accountResponse = await ReadJsonRpcResponseAsync(process.StandardOutput, 2, cancellationToken).ConfigureAwait(false);
            if (accountResponse?["result"] is not JObject result)
            {
                return AppServerAuthInspection.Empty;
            }

            JObject? account = result["account"] as JObject;
            return new AppServerAuthInspection(
                success: true,
                requiresOpenaiAuth: result["requiresOpenaiAuth"]?.Value<bool>() ?? true,
                accountType: account?["type"]?.Value<string>() ?? string.Empty,
                accountEmail: account?["email"]?.Value<string>() ?? string.Empty);
        }
        catch
        {
            return AppServerAuthInspection.Empty;
        }
        finally
        {
            TryTerminateProcess(process);
        }
    }

    private static async Task WriteJsonRpcMessageAsync(StreamWriter writer, object payload)
    {
        string json = JObject.FromObject(payload).ToString(Newtonsoft.Json.Formatting.None);
        await writer.WriteLineAsync(json).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<JObject?> ReadJsonRpcResponseAsync(StreamReader reader, int requestId, CancellationToken cancellationToken)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(5);
        while (true)
        {
            string? line = await ReadLineWithTimeoutAsync(reader, timeout, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JObject? message;
            try
            {
                message = JObject.Parse(line);
            }
            catch
            {
                continue;
            }

            if (message["id"]?.Value<int?>() == requestId)
            {
                return message;
            }
        }
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<string> readTask = reader.ReadLineAsync();
        Task timeoutTask = Task.Delay(timeout, cancellationToken);
        Task completedTask = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
        if (completedTask != readTask)
        {
            return null;
        }

        return await readTask.ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateProbeStartInfo(string executablePath, string arguments)
    {
        if (IsPowerShellScript(executablePath))
        {
            return new ProcessStartInfo
            {
                FileName = ResolvePowerShellHost(),
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(executablePath) + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        if (!RequiresCommandShell(executablePath))
        {
            return new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
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
            Arguments = "/d /s /c \"" + QuoteForCommandShell(executablePath) + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments) + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static ProcessStartInfo CreateServerProbeStartInfo(string executablePath, CodexExtensionSettings settings)
    {
        string arguments = BuildServerProbeArguments(settings);
        string workingDirectory = ResolveWorkingDirectory(settings.WorkingDirectory);
        if (IsPowerShellScript(executablePath))
        {
            return new ProcessStartInfo
            {
                FileName = ResolvePowerShellHost(),
                WorkingDirectory = workingDirectory,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(executablePath) + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        if (!RequiresCommandShell(executablePath))
        {
            return new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
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
            WorkingDirectory = workingDirectory,
            Arguments = "/d /s /c \"" + QuoteForCommandShell(executablePath) + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments) + "\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    private static string BuildServerProbeArguments(CodexExtensionSettings settings)
    {
        List<string> args = ["app-server", "--listen", "stdio://"];

        if (!HasProfileArgument(settings.AdditionalArguments) && !string.IsNullOrWhiteSpace(settings.Profile))
        {
            args.Add("--profile");
            args.Add(settings.Profile.Trim());
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

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => process.WaitForExit(timeoutMilliseconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            throw;
        }
    }

    private static ConfigProviderInspection InspectConfiguredProvider(CodexExtensionSettings settings)
    {
        ParsedCodexConfig parsedConfig = ParseEffectiveConfig(settings);
        string selectedProfile = ResolveSelectedProfile(settings, parsedConfig.DefaultProfile);
        string providerId = string.Empty;

        if (!string.IsNullOrWhiteSpace(selectedProfile)
            && parsedConfig.Profiles.TryGetValue(selectedProfile, out ParsedProfileConfig? profileConfig)
            && !string.IsNullOrWhiteSpace(profileConfig.ModelProvider))
        {
            providerId = profileConfig.ModelProvider;
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            providerId = parsedConfig.RootModelProvider;
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new ConfigProviderInspection(string.Empty, selectedProfile, hasActiveProvider: false, hasExplicitCredentialRequirement: false, hasConfiguredCredentials: true);
        }

        if (!parsedConfig.Providers.TryGetValue(providerId, out ParsedProviderConfig? providerConfig))
        {
            return new ConfigProviderInspection(providerId, selectedProfile, hasActiveProvider: true, hasExplicitCredentialRequirement: false, hasConfiguredCredentials: true);
        }

        bool hasExplicitCredentialRequirement =
            !string.IsNullOrWhiteSpace(providerConfig.EnvKey)
            || !string.IsNullOrWhiteSpace(providerConfig.ApiKey)
            || providerConfig.HasAuthSection
            || providerConfig.EnvHeaderVariables.Count > 0;

        if (!hasExplicitCredentialRequirement)
        {
            return new ConfigProviderInspection(providerId, selectedProfile, hasActiveProvider: true, hasExplicitCredentialRequirement: false, hasConfiguredCredentials: true);
        }

        bool envRequirementsSatisfied =
            (string.IsNullOrWhiteSpace(providerConfig.EnvKey) || IsEnvironmentVariableConfigured(providerConfig.EnvKey, settings.EnvironmentVariables))
            && providerConfig.EnvHeaderVariables.All(envVar => IsEnvironmentVariableConfigured(envVar, settings.EnvironmentVariables));

        bool hasConfiguredCredentials = envRequirementsSatisfied
            && (providerConfig.HasAuthSection
                || !string.IsNullOrWhiteSpace(providerConfig.ApiKey)
                || !string.IsNullOrWhiteSpace(providerConfig.EnvKey)
                || providerConfig.EnvHeaderVariables.Count > 0);

        return new ConfigProviderInspection(providerId, selectedProfile, hasActiveProvider: true, hasExplicitCredentialRequirement: true, hasConfiguredCredentials: hasConfiguredCredentials);
    }

    private static ParsedCodexConfig ParseEffectiveConfig(CodexExtensionSettings settings)
    {
        ParsedCodexConfig parsedConfig = new();
        string configPath = Path.Combine(CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables), "config.toml");
        if (File.Exists(configPath))
        {
            ParseTomlInto(parsedConfig, File.ReadAllLines(configPath));
        }

        if (!string.IsNullOrWhiteSpace(settings.RawTomlOverrides))
        {
            ParseTomlInto(parsedConfig, settings.RawTomlOverrides.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        return parsedConfig;
    }

    private static void ParseTomlInto(ParsedCodexConfig config, IEnumerable<string> lines)
    {
        string[] currentSection = Array.Empty<string>();
        foreach (string rawLine in lines)
        {
            string line = StripTomlComment(rawLine).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                currentSection = SplitTomlPath(line.Substring(1, line.Length - 2)).ToArray();
                continue;
            }

            int separatorIndex = FindTomlAssignmentSeparator(line);
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = line.Substring(0, separatorIndex).Trim();
            string value = line.Substring(separatorIndex + 1).Trim();
            ApplyTomlAssignment(config, currentSection, key, value);
        }
    }

    private static void ApplyTomlAssignment(ParsedCodexConfig config, IReadOnlyList<string> currentSection, string key, string value)
    {
        if (currentSection.Count == 0)
        {
            if (string.Equals(key, "profile", StringComparison.OrdinalIgnoreCase))
            {
                config.DefaultProfile = UnquoteTomlString(value);
            }
            else if (string.Equals(key, "model_provider", StringComparison.OrdinalIgnoreCase))
            {
                config.RootModelProvider = UnquoteTomlString(value);
            }

            return;
        }

        if (currentSection.Count == 2 && string.Equals(currentSection[0], "profiles", StringComparison.OrdinalIgnoreCase))
        {
            ParsedProfileConfig profile = config.GetOrCreateProfile(currentSection[1]);
            if (string.Equals(key, "model_provider", StringComparison.OrdinalIgnoreCase))
            {
                profile.ModelProvider = UnquoteTomlString(value);
            }

            return;
        }

        if (currentSection.Count < 2 || !string.Equals(currentSection[0], "model_providers", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ParsedProviderConfig provider = config.GetOrCreateProvider(currentSection[1]);
        if (currentSection.Count == 2)
        {
            if (string.Equals(key, "env_key", StringComparison.OrdinalIgnoreCase))
            {
                provider.EnvKey = UnquoteTomlString(value);
            }
            else if (string.Equals(key, "api_key", StringComparison.OrdinalIgnoreCase))
            {
                provider.ApiKey = UnquoteTomlString(value);
            }
            else if (string.Equals(key, "env_http_headers", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string envVar in ParseInlineTableValues(value))
                {
                    if (!string.IsNullOrWhiteSpace(envVar))
                    {
                        _ = provider.EnvHeaderVariables.Add(envVar);
                    }
                }
            }
            else if (string.Equals(key, "auth", StringComparison.OrdinalIgnoreCase) && value.StartsWith("{", StringComparison.Ordinal))
            {
                provider.HasAuthSection = true;
            }

            return;
        }

        if (currentSection.Count == 3 && string.Equals(currentSection[2], "auth", StringComparison.OrdinalIgnoreCase))
        {
            provider.HasAuthSection = true;
        }
    }

    private static IEnumerable<string> ParseInlineTableValues(string value)
    {
        string trimmed = value.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (string entry in SplitTopLevelCommaList(trimmed.Substring(1, trimmed.Length - 2)))
        {
            int separatorIndex = FindTomlAssignmentSeparator(entry);
            if (separatorIndex <= 0)
            {
                continue;
            }

            string entryValue = entry.Substring(separatorIndex + 1).Trim();
            string envVar = UnquoteTomlString(entryValue);
            if (!string.IsNullOrWhiteSpace(envVar))
            {
                yield return envVar;
            }
        }
    }

    private static IEnumerable<string> SplitTopLevelCommaList(string text)
    {
        StringBuilder current = new();
        bool inDoubleQuotes = false;
        bool inSingleQuotes = false;
        int braceDepth = 0;

        foreach (char ch in text)
        {
            switch (ch)
            {
                case '"' when !inSingleQuotes:
                    inDoubleQuotes = !inDoubleQuotes;
                    _ = current.Append(ch);
                    continue;
                case '\'' when !inDoubleQuotes:
                    inSingleQuotes = !inSingleQuotes;
                    _ = current.Append(ch);
                    continue;
                case '{' when !inDoubleQuotes && !inSingleQuotes:
                    braceDepth++;
                    _ = current.Append(ch);
                    continue;
                case '}' when !inDoubleQuotes && !inSingleQuotes && braceDepth > 0:
                    braceDepth--;
                    _ = current.Append(ch);
                    continue;
                case ',' when !inDoubleQuotes && !inSingleQuotes && braceDepth == 0:
                    yield return current.ToString().Trim();
                    _ = current.Clear();
                    continue;
                default:
                    _ = current.Append(ch);
                    continue;
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }

    private static IEnumerable<string> SplitTomlPath(string text)
    {
        StringBuilder current = new();
        bool inDoubleQuotes = false;
        bool inSingleQuotes = false;

        foreach (char ch in text)
        {
            switch (ch)
            {
                case '"' when !inSingleQuotes:
                    inDoubleQuotes = !inDoubleQuotes;
                    _ = current.Append(ch);
                    continue;
                case '\'' when !inDoubleQuotes:
                    inSingleQuotes = !inSingleQuotes;
                    _ = current.Append(ch);
                    continue;
                case '.' when !inDoubleQuotes && !inSingleQuotes:
                    string segment = current.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(segment))
                    {
                        yield return UnquoteTomlString(segment);
                    }

                    _ = current.Clear();
                    continue;
                default:
                    _ = current.Append(ch);
                    continue;
            }
        }

        if (current.Length > 0)
        {
            string segment = current.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(segment))
            {
                yield return UnquoteTomlString(segment);
            }
        }
    }

    private static int FindTomlAssignmentSeparator(string text)
    {
        bool inDoubleQuotes = false;
        bool inSingleQuotes = false;
        int braceDepth = 0;

        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            if (ch == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if (ch == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                continue;
            }

            if (inDoubleQuotes || inSingleQuotes)
            {
                continue;
            }

            if (ch == '{')
            {
                braceDepth++;
                continue;
            }

            if (ch == '}' && braceDepth > 0)
            {
                braceDepth--;
                continue;
            }

            if (ch == '=' && braceDepth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string StripTomlComment(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        bool inDoubleQuotes = false;
        bool inSingleQuotes = false;
        for (int index = 0; index < line.Length; index++)
        {
            char ch = line[index];
            if (ch == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if (ch == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                continue;
            }

            if (ch == '#' && !inDoubleQuotes && !inSingleQuotes)
            {
                return line.Substring(0, index);
            }
        }

        return line;
    }

    private static string UnquoteTomlString(string value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length >= 2)
        {
            if ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
                || (trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\''))
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            }
        }

        return trimmed
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\");
    }

    private static string ResolveSelectedProfile(CodexExtensionSettings settings, string defaultProfile)
    {
        return FirstNonEmptyString(
            GetProfileArgument(settings.AdditionalArguments),
            settings.Profile,
            defaultProfile);
    }

    private static string GetProfileArgument(string? commandLine)
    {
        bool awaitingProfileValue = false;
        foreach (string token in SplitArguments(commandLine ?? string.Empty))
        {
            if (awaitingProfileValue)
            {
                return token.Trim();
            }

            if (string.Equals(token, "--profile", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-p", StringComparison.OrdinalIgnoreCase))
            {
                awaitingProfileValue = true;
                continue;
            }

            if (token.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            {
                return token.Substring("--profile=".Length).Trim();
            }
        }

        return string.Empty;
    }

    private static bool HasProfileArgument(string? commandLine)
    {
        return !string.IsNullOrWhiteSpace(GetProfileArgument(commandLine));
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

    private static string JoinArguments(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(QuoteCommandLineArgument));
    }

    private static string QuoteCommandLineArgument(string value)
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

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            return workingDirectory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static void ApplyEnvironmentVariables(ProcessStartInfo psi, string? environmentVariables)
    {
        foreach (KeyValuePair<string, string> entry in CodexEnvironmentPathHelper.ParseEnvironmentVariables(environmentVariables))
        {
            psi.EnvironmentVariables[entry.Key] = entry.Value;
        }
    }

    private static bool IsEnvironmentVariableConfigured(string envVarName, string? environmentVariables)
    {
        return !string.IsNullOrWhiteSpace(CodexEnvironmentPathHelper.GetEffectiveEnvironmentVariable(envVarName, environmentVariables));
    }

    private static string FirstNonEmptyLine(string? text)
    {
        return (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?? string.Empty;
    }

    private static bool HasOpenAiApiKey(string environmentVariables)
    {
        foreach (string? line in (environmentVariables ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = line.Substring(0, separatorIndex).Trim();
            string value = line.Substring(separatorIndex + 1).Trim();
            if (string.Equals(key, "OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        return false;
    }

    private static AuthFileInspection InspectAuthFile(string authFilePath)
    {
        if (string.IsNullOrWhiteSpace(authFilePath) || !File.Exists(authFilePath))
        {
            return AuthFileInspection.Empty;
        }

        try
        {
            JObject document = JObject.Parse(File.ReadAllText(authFilePath));
            string? embeddedApiKey = document["OPENAI_API_KEY"]?.Value<string>();
            JObject? tokens = document["tokens"] as JObject;
            string? accessToken = tokens?["access_token"]?.Value<string>();
            string? refreshToken = tokens?["refresh_token"]?.Value<string>();
            string? idToken = tokens?["id_token"]?.Value<string>();
            bool hasEmbeddedApiKey = !string.IsNullOrWhiteSpace(embeddedApiKey);
            bool hasUsableAuthFile = hasEmbeddedApiKey
                || !string.IsNullOrWhiteSpace(accessToken)
                || !string.IsNullOrWhiteSpace(refreshToken);

            if (string.IsNullOrWhiteSpace(idToken))
            {
                return new AuthFileInspection(hasUsableAuthFile, hasEmbeddedApiKey, string.Empty);
            }

            JObject payload = ParseJwtPayload(idToken!);
            string accountEmail = FirstNonEmptyString(
                payload.TryGetValue("email", out JToken? email) ? email?.ToString() : null,
                payload.TryGetValue("preferred_username", out JToken? preferredUsername) ? preferredUsername?.ToString() : null,
                tokens?["account_id"]?.ToString());

            return new AuthFileInspection(hasUsableAuthFile, hasEmbeddedApiKey, accountEmail);
        }
        catch
        {
            return AuthFileInspection.Empty;
        }
    }

    private static string FirstNonEmptyString(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static JObject ParseJwtPayload(string token)
    {
        string[] parts = token.Split('.');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            return [];
        }

        byte[] payloadBytes = DecodeBase64Url(parts[1]);
        string payloadJson = Encoding.UTF8.GetString(payloadBytes);
        return JObject.Parse(payloadJson);
    }

    private static byte[] DecodeBase64Url(string value)
    {
        string normalized = value.Replace('-', '+').Replace('_', '/');
        int padding = 4 - (normalized.Length % 4);
        if (padding is > 0 and < 4)
        {
            normalized = normalized.PadRight(normalized.Length + padding, '=');
        }

        return Convert.FromBase64String(normalized);
    }

    private static string QuoteForCommandShell(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static bool RequiresCommandShell(string executablePath)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
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
        return Environment.OSVersion.Platform == PlatformID.Win32NT
            && Path.GetExtension(executablePath).Equals(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                _ = process.WaitForExit(1000);
            }
        }
        catch
        {
        }
    }

    private static string ResolvePowerShellHost()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string windowsPowerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell)
            ? windowsPowerShell
            : "powershell.exe";
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }

    private sealed class AuthFileInspection
    {
        public static AuthFileInspection Empty { get; } = new(false, false, string.Empty);

        public AuthFileInspection(bool hasUsableAuthFile, bool hasEmbeddedApiKey, string accountEmail)
        {
            this.HasUsableAuthFile = hasUsableAuthFile;
            this.HasEmbeddedApiKey = hasEmbeddedApiKey;
            this.AccountEmail = accountEmail ?? string.Empty;
        }

        public bool HasUsableAuthFile { get; }

        public bool HasEmbeddedApiKey { get; }

        public string AccountEmail { get; }
    }

    private sealed class AppServerAuthInspection
    {
        public static AppServerAuthInspection Empty { get; } = new(false, true, string.Empty, string.Empty);

        public AppServerAuthInspection(bool success, bool requiresOpenaiAuth, string accountType, string accountEmail)
        {
            this.Success = success;
            this.RequiresOpenaiAuth = requiresOpenaiAuth;
            this.AccountType = accountType ?? string.Empty;
            this.AccountEmail = accountEmail ?? string.Empty;
        }

        public bool Success { get; }

        public bool RequiresOpenaiAuth { get; }

        public string AccountType { get; }

        public string AccountEmail { get; }
    }

    private sealed class ConfigProviderInspection
    {
        public ConfigProviderInspection(string providerId, string selectedProfile, bool hasActiveProvider, bool hasExplicitCredentialRequirement, bool hasConfiguredCredentials)
        {
            this.ProviderId = providerId ?? string.Empty;
            this.SelectedProfile = selectedProfile ?? string.Empty;
            this.HasActiveProvider = hasActiveProvider;
            this.HasExplicitCredentialRequirement = hasExplicitCredentialRequirement;
            this.HasConfiguredCredentials = hasConfiguredCredentials;
        }

        public string ProviderId { get; }

        public string SelectedProfile { get; }

        public bool HasActiveProvider { get; }

        public bool HasExplicitCredentialRequirement { get; }

        public bool HasConfiguredCredentials { get; }

        public bool IsReady => !this.HasExplicitCredentialRequirement || this.HasConfiguredCredentials;

        public bool RequiresOpenaiAuthFallback => !this.HasActiveProvider || string.Equals(this.ProviderId, "openai", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ParsedCodexConfig
    {
        public string DefaultProfile { get; set; } = string.Empty;

        public string RootModelProvider { get; set; } = string.Empty;

        public Dictionary<string, ParsedProfileConfig> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ParsedProviderConfig> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ParsedProfileConfig GetOrCreateProfile(string id)
        {
            if (!this.Profiles.TryGetValue(id, out ParsedProfileConfig? profile))
            {
                profile = new ParsedProfileConfig();
                this.Profiles[id] = profile;
            }

            return profile;
        }

        public ParsedProviderConfig GetOrCreateProvider(string id)
        {
            if (!this.Providers.TryGetValue(id, out ParsedProviderConfig? provider))
            {
                provider = new ParsedProviderConfig();
                this.Providers[id] = provider;
            }

            return provider;
        }
    }

    private sealed class ParsedProfileConfig
    {
        public string ModelProvider { get; set; } = string.Empty;
    }

    private sealed class ParsedProviderConfig
    {
        public string EnvKey { get; set; } = string.Empty;

        public string ApiKey { get; set; } = string.Empty;

        public bool HasAuthSection { get; set; }

        public HashSet<string> EnvHeaderVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
