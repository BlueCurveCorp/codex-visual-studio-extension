using System;
using System.IO;

using CodexVsix.Models;

using Newtonsoft.Json;

namespace CodexVsix.Services;

public sealed class ExtensionSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexVsix");

    private static readonly string SettingsFile = Path.Combine(SettingsDirectory, "settings.json");

    public string SettingsFilePath => SettingsFile;

    public CodexExtensionSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return new CodexExtensionSettings();
            }

            string json = File.ReadAllText(SettingsFile);
            CodexExtensionSettings settings = JsonConvert.DeserializeObject<CodexExtensionSettings>(json) ?? new CodexExtensionSettings();
            settings.PromptHistory ??= [];
            settings.CodexExecutablePath ??= "codex.cmd";
            settings.LanguageOverride ??= "";
            settings.WorkingDirectory ??= "";
            settings.DefaultModel ??= "";
            settings.ReasoningEffort ??= "";
            settings.ModelVerbosity ??= "";
            settings.ServiceTier ??= "";
            settings.Profile ??= "";
            settings.ApprovalPolicy ??= "";
            settings.SandboxMode ??= "";
            settings.AdditionalArguments ??= "";
            settings.EnvironmentVariables ??= "";
            settings.RawTomlOverrides ??= "";
            settings.CurrentThreadId ??= "";
            settings.LastThreadWorkingDirectory ??= "";
            settings.CustomModels ??= [];
            settings.CustomReasoningEfforts ??= [];
            settings.CustomVerbosityOptions ??= [];
            settings.CustomServiceTiers ??= [];
            settings.ManagedMcpServers ??= [];
            settings.PreferredMcpServers ??= [];
            return settings;
        }
        catch
        {
            return new CodexExtensionSettings();
        }
    }

    public void Save(CodexExtensionSettings settings)
    {
        _ = Directory.CreateDirectory(SettingsDirectory);
        string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        File.WriteAllText(SettingsFile, json);
    }
}
