using System;
using System.Collections.Generic;
using System.IO;

namespace CodexVsix.Services;

internal static class CodexEnvironmentPathHelper
{
    public static string GetCodexHomeDirectory(string? environmentVariables = null)
    {
        string configuredHome = GetEffectiveEnvironmentVariable("CODEX_HOME", environmentVariables);
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return NormalizePath(configuredHome);
        }

        return Path.Combine(GetUserHomeDirectory(environmentVariables), ".codex");
    }

    public static string GetUserHomeDirectory(string? environmentVariables = null)
    {
        string home = FirstNonEmpty(
            GetEffectiveEnvironmentVariable("HOME", environmentVariables),
            GetEffectiveEnvironmentVariable("USERPROFILE", environmentVariables));

        if (!string.IsNullOrWhiteSpace(home))
        {
            return NormalizePath(home);
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static string GetEffectiveEnvironmentVariable(string key, string? environmentVariables = null)
    {
        IReadOnlyDictionary<string, string> overrides = ParseEnvironmentVariables(environmentVariables);
        if (overrides.TryGetValue(key, out string? overrideValue) && !string.IsNullOrWhiteSpace(overrideValue))
        {
            return overrideValue;
        }

        return Environment.GetEnvironmentVariable(key) ?? string.Empty;
    }

    public static IReadOnlyDictionary<string, string> ParseEnvironmentVariables(string? environmentVariables)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? line in (environmentVariables ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
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
                values[key] = value;
            }
        }

        return values;
    }

    private static string NormalizePath(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch
        {
            return expanded;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }

        return string.Empty;
    }
}
