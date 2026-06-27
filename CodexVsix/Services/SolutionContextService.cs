using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

using EnvDTE;

using Microsoft.VisualStudio.Shell;

namespace CodexVsix.Services;

public sealed class SolutionContextService
{
    public string? TryGetBestWorkspaceDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return this.TryGetSolutionDirectory()
            ?? TryGetActiveProjectDirectory()
            ?? TryGetFirstProjectDirectory()
            ?? TryGetActiveDocumentDirectory();
    }

    public string? TryGetSolutionDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        string? solutionPath = dte?.Solution?.FullName;
        if (!string.IsNullOrWhiteSpace(solutionPath) && File.Exists(solutionPath))
        {
            return Path.GetDirectoryName(solutionPath);
        }

        return null;
    }

    public string GetBestWorkingDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return this.TryGetBestWorkspaceDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string BuildIdeContextSummary(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LocalizationService localization = new();

        List<string> sections = [];
        string? solutionDirectory = this.TryGetSolutionDirectory();
        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            sections.Add(localization.IdeContextSolutionLabel + " " + FormatPath(workingDirectory, solutionDirectory));
        }

        string? activeDocument = TryGetActiveDocumentPath();
        if (!string.IsNullOrWhiteSpace(activeDocument))
        {
            sections.Add(localization.IdeContextActiveDocumentLabel + " " + FormatPath(workingDirectory, activeDocument));
        }

        IReadOnlyList<string> selectedItems = GetSelectedPaths(workingDirectory);
        if (selectedItems.Count > 0)
        {
            sections.Add(localization.IdeContextSelectedItemsLabel + " " + string.Join(", ", selectedItems.Take(5)));
        }

        IReadOnlyList<string> openDocuments = GetOpenDocumentPaths(workingDirectory);
        if (openDocuments.Count > 0)
        {
            sections.Add(localization.IdeContextOpenFilesLabel + " " + string.Join(", ", openDocuments.Take(6)));
        }

        string selectionSnippet = TryGetActiveSelectionSnippet();
        if (!string.IsNullOrWhiteSpace(selectionSnippet))
        {
            sections.Add(localization.IdeContextSelectionLabel + Environment.NewLine + selectionSnippet);
        }

        return string.Join(Environment.NewLine, sections.Where(section => !string.IsNullOrWhiteSpace(section)));
    }

    public IReadOnlyList<string> FindSolutionFiles(string search)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string root = this.GetBestWorkingDirectory();
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        string normalized = (search ?? string.Empty).Trim().Replace('\\', '/');
        List<string> files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !IsIgnoredPath(f))
            .Select(f => MakeRelative(root, f))
            .Where(f => string.IsNullOrWhiteSpace(normalized) || f.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(f => Score(f, normalized))
            .ThenBy(f => f)
            .Take(30)
            .ToList();

        return files;
    }

    public IReadOnlyList<string> GetSolutionFilePaths()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        if (dte?.Solution?.Projects is null)
        {
            return Array.Empty<string>();
        }

        List<string> files = [];
        HashSet<string> visitedProjects = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (Project project in dte.Solution.Projects)
            {
                CollectProjectFiles(project, files, visitedProjects);
            }
        }
        catch
        {
        }

        return files
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetCodexConfigPath()
    {
        return Path.Combine(this.GetCodexHomeDirectory(), "config.toml");
    }

    public string GetCodexHomeDirectory()
    {
        return CodexEnvironmentPathHelper.GetCodexHomeDirectory();
    }

    public string GetCodexSkillsDirectory()
    {
        return Path.Combine(this.GetCodexHomeDirectory(), "skills");
    }

    public void OpenCodexConfig()
    {
        string path = this.GetCodexConfigPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "model = \"gpt-5.4\"" + Environment.NewLine);
        }

        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    public void OpenCodexSkillsDirectory()
    {
        string path = this.GetCodexSkillsDirectory();
        _ = Directory.CreateDirectory(path);
        this.OpenPath(path);
    }

    public void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _ = System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    public void OpenFileInVisualStudio(string path, int? line = null, int? column = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        if (!TryOpenDocumentInVisualStudio(path))
        {
            return;
        }

        if (line is > 0)
        {
            NavigateActiveDocument(line.Value, column);
        }
    }

    private static bool TryOpenDocumentInVisualStudio(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (TryOpenDocumentWithDte(path))
        {
            return true;
        }

        try
        {
            VsShellUtilities.OpenDocument(Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider, path);
            return true;
        }
        catch
        {
        }

        return false;
    }

    private static bool TryOpenDocumentWithDte(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.ItemOperations is null)
            {
                return false;
            }

            Window window = dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            window?.Activate();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void NavigateActiveDocument(int line, int? column)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.ActiveDocument?.Selection is not TextSelection selection)
            {
                return;
            }

            if (column is > 0)
            {
                selection.MoveToLineAndOffset(line, column.Value, false);
            }
            else
            {
                selection.GotoLine(line, false);
            }
        }
        catch
        {
        }
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        _ = System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    public string CreateSkillTemplate(string skillName, string description)
    {
        string normalizedSkillName = NormalizeSkillName(skillName);
        string skillDirectory = Path.Combine(this.GetCodexSkillsDirectory(), normalizedSkillName);
        _ = Directory.CreateDirectory(skillDirectory);

        string skillFile = Path.Combine(skillDirectory, "SKILL.md");
        if (!File.Exists(skillFile))
        {
            File.WriteAllText(
                skillFile,
                BuildSkillTemplate(normalizedSkillName, description),
                new UTF8Encoding(false));
        }

        return skillFile;
    }

    public static bool IsValidSkillName(string? skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return false;
        }

        string trimmed = skillName.Trim();
        if (!char.IsLetterOrDigit(trimmed[0]))
        {
            return false;
        }

        return trimmed.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-');
    }

    private static string NormalizeSkillName(string skillName)
    {
        string trimmed = (skillName ?? string.Empty).Trim();
        if (!IsValidSkillName(trimmed))
        {
            throw new ArgumentException(new LocalizationService().InvalidSkillNameMessage, nameof(skillName));
        }

        return trimmed;
    }

    private static string BuildSkillTemplate(string skillName, string description)
    {
        LocalizationService localization = new();
        string summary = string.IsNullOrWhiteSpace(description)
            ? localization.SkillTemplateSummary
            : description.Trim();

        return "# " + skillName + Environment.NewLine
            + Environment.NewLine
            + summary + Environment.NewLine
            + Environment.NewLine
            + localization.SkillTemplateWhenToUseHeading + Environment.NewLine
            + localization.SkillTemplateWhenToUseBullet + Environment.NewLine
            + Environment.NewLine
            + localization.SkillTemplateFlowHeading + Environment.NewLine
            + localization.SkillTemplateFlowStep1 + Environment.NewLine
            + localization.SkillTemplateFlowStep2 + Environment.NewLine
            + localization.SkillTemplateFlowStep3 + Environment.NewLine;
    }

    private static bool IsIgnoredPath(string fullPath)
    {
        string p = fullPath.Replace('\\', '/');
        return p.Contains("/.git/") || p.Contains("/bin/") || p.Contains("/obj/") || p.Contains("/node_modules/") || p.Contains("/.vs/");
    }

    private static string? TryGetActiveProjectDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.ActiveSolutionProjects is not Array activeProjects)
            {
                return null;
            }

            foreach (object? entry in activeProjects)
            {
                if (entry is Project project)
                {
                    string? directory = TryGetProjectDirectory(project);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        return directory;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryGetFirstProjectDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            Projects? projects = dte?.Solution?.Projects;
            if (projects is null)
            {
                return null;
            }

            foreach (Project project in projects)
            {
                string? directory = TryGetProjectDirectory(project);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return directory;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryGetActiveDocumentDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            string? fullName = dte?.ActiveDocument?.FullName;
            if (!string.IsNullOrWhiteSpace(fullName) && File.Exists(fullName))
            {
                return Path.GetDirectoryName(fullName);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryGetActiveDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            string? fullName = dte?.ActiveDocument?.FullName;
            return !string.IsNullOrWhiteSpace(fullName) && File.Exists(fullName)
                ? fullName
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProjectDirectory(Project? project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (project is null)
        {
            return null;
        }

        try
        {
            string fullName = project.FullName;
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                if (File.Exists(fullName))
                {
                    return Path.GetDirectoryName(fullName);
                }

                if (Directory.Exists(fullName))
                {
                    return fullName;
                }
            }
        }
        catch
        {
        }

        try
        {
            string? fullPath = project.Properties?.Item("FullPath")?.Value as string;
            if (!string.IsNullOrWhiteSpace(fullPath) && Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }
        catch
        {
        }

        try
        {
            if (project.ProjectItems is null)
            {
                return null;
            }

            foreach (ProjectItem item in project.ProjectItems)
            {
                string? nested = TryGetProjectDirectory(item.SubProject);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static void CollectProjectFiles(Project? project, ICollection<string> files, ISet<string> visitedProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (project is null)
        {
            return;
        }

        string? projectKey = TryGetProjectKey(project);
        if (!string.IsNullOrWhiteSpace(projectKey))
        {
            string key = projectKey!;
            if (!visitedProjects.Add(key))
            {
                return;
            }
        }

        try
        {
            TryAddFile(files, project.FullName);
        }
        catch
        {
        }

        try
        {
            if (project.ProjectItems is null)
            {
                return;
            }

            foreach (ProjectItem item in project.ProjectItems)
            {
                CollectProjectItemFiles(item, files, visitedProjects);
            }
        }
        catch
        {
        }
    }

    private static void CollectProjectItemFiles(ProjectItem item, ICollection<string> files, ISet<string> visitedProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            for (short index = 1; index <= item.FileCount; index++)
            {
                TryAddFile(files, item.FileNames[index]);
            }
        }
        catch
        {
        }

        try
        {
            CollectProjectFiles(item.SubProject, files, visitedProjects);
        }
        catch
        {
        }

        try
        {
            if (item.ProjectItems is null)
            {
                return;
            }

            foreach (ProjectItem child in item.ProjectItems)
            {
                CollectProjectItemFiles(child, files, visitedProjects);
            }
        }
        catch
        {
        }
    }

    private static string? TryGetProjectKey(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!string.IsNullOrWhiteSpace(project.UniqueName))
            {
                return project.UniqueName;
            }
        }
        catch
        {
        }

        try
        {
            return project.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static void TryAddFile(ICollection<string> files, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string filePath = path!;
        if (File.Exists(filePath))
        {
            files.Add(filePath);
        }
    }

    private static string MakeRelative(string root, string file)
    {
        string relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Replace('\\', '/');
    }

    private static string FormatPath(string root, string path)
    {
        if (!string.IsNullOrWhiteSpace(root)
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return MakeRelative(root, path);
        }

        return path.Replace('\\', '/');
    }

    private static IReadOnlyList<string> GetOpenDocumentPaths(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.Documents is null)
            {
                return Array.Empty<string>();
            }

            List<string> paths = [];
            foreach (Document document in dte.Documents)
            {
                string documentPath = document.FullName;
                if (!string.IsNullOrWhiteSpace(documentPath) && File.Exists(documentPath))
                {
                    paths.Add(FormatPath(workingDirectory, documentPath));
                }
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> GetSelectedPaths(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.SelectedItems is null)
            {
                return Array.Empty<string>();
            }

            List<string> items = [];
            foreach (SelectedItem selectedItem in dte.SelectedItems)
            {
                string? path = selectedItem.ProjectItem?.FileNames[1]
                    ?? selectedItem.Project?.FullName;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                items.Add(FormatPath(workingDirectory, path));
            }

            return items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string TryGetActiveSelectionSnippet()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            DTE? dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            TextSelection? textSelection = dte?.ActiveDocument?.Selection as TextSelection;
            string? selectedText = textSelection?.Text;
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                return string.Empty;
            }

            string normalized = selectedText.Replace("\r\n", "\n").Trim();
            const int maxLength = 900;
            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            return normalized.Substring(0, maxLength).TrimEnd() + Environment.NewLine + "...";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int Score(string file, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return int.MaxValue / 2;
        }

        int idx = file.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? int.MaxValue : (idx * 10) + file.Length;
    }
}
