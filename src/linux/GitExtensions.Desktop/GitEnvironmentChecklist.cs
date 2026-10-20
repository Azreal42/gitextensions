using System.Globalization;
using System.Text;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed record GitEnvironmentChecklistResult(IReadOnlyList<GitEnvironmentCheck> Checks,
    GitEnvironmentDiagnostic Diagnostic, string EnvironmentInformation)
{
    internal bool AllGood => Checks.Count == Enum.GetValues<GitEnvironmentCheckId>().Length
        && Checks.All(check => check.Status == GitEnvironmentCheckStatus.Good);
}

/// <summary>Native read-only observations, with the same global configuration criteria as the Windows checklist.</summary>
internal static class GitEnvironmentChecklist
{
    internal static async Task<GitEnvironmentChecklistResult> ScanAsync(GitRepository? selected,
        GitExecutionEnvironment environment, TranslationCatalog translations, string? language, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(environment); ArgumentNullException.ThrowIfNull(translations);
        token.ThrowIfCancellationRequested();
        using GitConfigurationContext context = GitConfigurationContext.Create(selected, environment);
        GitEnvironmentDiagnostic diagnostic = await GitEnvironmentDiagnostics.ProbeAsync(context.Environment, context.Repository.Path, token).ConfigureAwait(false);
        GitToolConfigurationSnapshot configuration = await context.Repository.GetToolConfigurationAsync(token).ConfigureAwait(false);
        GitToolConfigurationScopeSnapshot global = configuration.GetScope(GitToolConfigurationView.Global);
        GitToolConfigurationScopeSnapshot effective = configuration.GetScope(GitToolConfigurationView.Effective);
        List<GitEnvironmentCheck> checks = [];
        List<string> toolInformation = [];

        checks.Add(CheckGit());
        checks.Add(!global.IsAvailable ? Unavailable(GitEnvironmentCheckId.GlobalIdentity, global.Diagnostic)
            : string.IsNullOrEmpty(Last(global.GetSetting(GitConfigurationField.UserName))) || string.IsNullOrEmpty(Last(global.GetSetting(GitConfigurationField.UserEmail)))
                ? Check(GitEnvironmentCheckId.GlobalIdentity, GitEnvironmentCheckStatus.Error, "_noEmailSet", "You need to configure a username and an email address.")
                : Check(GitEnvironmentCheckId.GlobalIdentity, GitEnvironmentCheckStatus.Good, "_emailSet", "A username and an email address are configured."));
        checks.Add(CheckEditor());
        checks.Add(await ObserveToolAsync(GitToolRole.Merge, GitEnvironmentCheckId.MergeTool).ConfigureAwait(false));
        checks.Add(await ObserveToolAsync(GitToolRole.Diff, GitEnvironmentCheckId.DiffTool).ConfigureAwait(false));
        checks.Add(diagnostic.Shell.Success
            ? Check(GitEnvironmentCheckId.Shell, GitEnvironmentCheckStatus.Good, "_linuxToolsSshFound", "Linux tools (sh) found on your computer.")
            : Check(GitEnvironmentCheckId.Shell, GitEnvironmentCheckStatus.Error, "_linuxToolsSshNotFound", "Linux tools (sh) not found. To solve this problem you can set the correct path in settings.") with { Message = Text("_linuxToolsSshNotFound", "Linux tools (sh) not found. To solve this problem you can set the correct path in settings.") + " " + Failure(diagnostic.Shell) });
        GitSshConfiguration? sshConfiguration = diagnostic.SshConfiguration;
        GitEnvironmentCheckStatus sshStatus = sshConfiguration?.Source == GitSshCommandSource.Unavailable ? GitEnvironmentCheckStatus.Unavailable
            : sshConfiguration?.HasCommandOverride == true ? GitEnvironmentCheckStatus.Warning
            : diagnostic.Ssh.Success ? GitEnvironmentCheckStatus.Good : GitEnvironmentCheckStatus.Error;
        checks.Add(Check(GitEnvironmentCheckId.Ssh, sshStatus,
            "LocalSshProbe", diagnostic.Ssh.Success ? "SSH responds to a local version check. Authentication has not been tested."
                : "SSH could not complete a local version check. Authentication has not been tested.") with
        { Message = translations.Translate(diagnostic.Ssh.Success ? "SSH responds to a local version check. Authentication has not been tested."
            : "SSH could not complete a local version check. Authentication has not been tested.") + (diagnostic.Ssh.Success ? "" : " " + Failure(diagnostic.Ssh)) + " " + DescribeSshCommand(diagnostic, translations) });
        checks.Add(CheckTranslation());
        checks.Add(CheckHome());
        token.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(checks.ToArray()), diagnostic, Information());

        GitEnvironmentCheck CheckGit()
        {
            if (!diagnostic.Git.Success || diagnostic.Version is not { } version)
            { return Check(GitEnvironmentCheckId.GitExecutable, GitEnvironmentCheckStatus.Error, "_gitNotFound", "Git not found. To solve this problem you can set the correct path in settings.") with { Message = Text("_gitNotFound", "Git not found. To solve this problem you can set the correct path in settings.") + " " + Failure(diagnostic.Git) }; }
            if (version < GitEnvironmentDiagnostics.MinimumVersion)
            { return Check(GitEnvironmentCheckId.GitExecutable, GitEnvironmentCheckStatus.Error, "_wrongGitVersion", "Git found but version {0} is not supported. Upgrade to version {1} or later", version, GitEnvironmentDiagnostics.MinimumVersion); }
            if (version < GitEnvironmentDiagnostics.RecommendedVersion)
            { return Check(GitEnvironmentCheckId.GitExecutable, GitEnvironmentCheckStatus.Warning, "_notRecommendedGitVersion", "Git found but version {0} is older than recommended. Upgrade to version {1} or later", version, GitEnvironmentDiagnostics.RecommendedVersion); }
            return Check(GitEnvironmentCheckId.GitExecutable, GitEnvironmentCheckStatus.Good, "_gitVersionFound", "Git {0} is found on your computer.", version);
        }

        GitEnvironmentCheck CheckEditor()
        {
            string? editor = context.Environment.GetVariable("GIT_EDITOR");
            if (string.IsNullOrEmpty(editor))
            {
                if (!global.IsAvailable) { return Unavailable(GitEnvironmentCheckId.Editor, global.Diagnostic); }
                editor = Last(global.GetSetting(GitConfigurationField.Editor));
            }
            if (string.IsNullOrEmpty(editor)) { editor = context.Environment.GetVariable("VISUAL"); }
            if (string.IsNullOrEmpty(editor)) { editor = context.Environment.GetVariable("EDITOR"); }
            return string.IsNullOrEmpty(editor)
                ? Check(GitEnvironmentCheckId.Editor, GitEnvironmentCheckStatus.Error, "EditorNotConfigured", "You need to configure a text editor.")
                : Check(GitEnvironmentCheckId.Editor, GitEnvironmentCheckStatus.Good, "EditorConfigured", "A text editor is configured: {0}", editor);
        }

        async Task<GitEnvironmentCheck> ObserveToolAsync(GitToolRole role, GitEnvironmentCheckId id)
        {
            token.ThrowIfCancellationRequested();
            if (!effective.IsAvailable) { return Unavailable(id, effective.Diagnostic); }
            try
            {
                string? name = Last(role == GitToolRole.Merge ? effective.SelectedMergeGui : effective.SelectedDiffGui);
                if (string.IsNullOrEmpty(name))
                {
                    name = role == GitToolRole.Merge ? Last(effective.MergeToolFallback)
                        : await ConfigGetAsync("diff.tool").ConfigureAwait(false);
                }
                if (string.IsNullOrEmpty(name))
                {
                    return role == GitToolRole.Merge
                        ? Check(id, GitEnvironmentCheckStatus.Error, "_configureMergeTool", "You need to configure merge tool in order to solve merge conflicts.")
                        : Check(id, GitEnvironmentCheckStatus.Error, "_adviceDiffToolConfiguration", "You should configure a diff tool to show file diff in external program.");
                }
                GitToolConfigurationDefinition definition = effective.GetTool(role, name);
                string? command = Last(definition.Command);
                if (command is null && role == GitToolRole.Diff) { command = Last(effective.GetTool(GitToolRole.Merge, name).Command); }
                if (!string.IsNullOrEmpty(command) && string.IsNullOrWhiteSpace(command))
                {
                    return role == GitToolRole.Merge
                        ? Check(id, GitEnvironmentCheckStatus.Error, "_mergeToolXConfiguredNeedsCmd", "{0} is configured as mergetool, this is a custom mergetool and needs a custom cmd to be configured.", name)
                        : Check(id, GitEnvironmentCheckStatus.Error, "CustomDiffToolNeedsCommand", "{0} is configured as difftool and needs a custom cmd to be configured.", name);
                }
                if (!string.IsNullOrWhiteSpace(command))
                {
                    toolInformation.Add(role + ": " + name + "; " + translations.Translate("Custom command configured; it was not executed."));
                    return role == GitToolRole.Merge
                        ? Check(id, GitEnvironmentCheckStatus.Good, "_customMergeToolXConfigured", "There is a custom mergetool configured: {0}", name)
                        : Check(id, GitEnvironmentCheckStatus.Good, "CustomDiffToolConfigured", "There is a custom difftool configured: {0}", name);
                }
                string verb = role == GitToolRole.Merge ? "mergetool" : "difftool";
                GitResult help = await context.Environment.CreateRunner().RunAsync(context.Repository.Path, [verb, "--tool-help"], token).ConfigureAwait(false);
                if (!help.Success) { return Unavailable(id, help.StandardError.Length == 0 ? "Git could not list its configured tools." : help.StandardError); }
                (bool known, bool available) = FindNativeTool(help.StandardOutput, name);
                if (!known)
                {
                    return role == GitToolRole.Merge
                        ? Check(id, GitEnvironmentCheckStatus.Error, "_mergeToolXConfiguredNeedsCmd", "{0} is configured as mergetool, this is a custom mergetool and needs a custom cmd to be configured.", name)
                        : Check(id, GitEnvironmentCheckStatus.Error, "CustomDiffToolNeedsCommand", "{0} is configured as difftool and needs a custom cmd to be configured.", name);
                }
                string? path = Last(definition.Path);
                if (path is null && role == GitToolRole.Diff) { path = Last(effective.GetTool(GitToolRole.Merge, name).Path); }
                // Windows validates configuration. Git's help lists native defaults and does not validate configured .path overrides.
                toolInformation.Add(role + ": " + name + "; " + translations.Translate(available ? "Git lists the native default as available." : "Git lists the native default as not currently available.")
                    + (string.IsNullOrEmpty(path) ? "" : " " + translations.Translate("Configured path (not executed):") + " " + path));
                return role == GitToolRole.Merge
                    ? Check(id, GitEnvironmentCheckStatus.Good, "_mergeToolXConfigured", "There is a mergetool configured: {0}", name)
                    : Check(id, GitEnvironmentCheckStatus.Good, "_diffToolXConfigured", "There is a difftool configured: {0}", name);
            }
            catch (Exception error) when (error is not OperationCanceledException) { return Unavailable(id, error.Message); }
        }

        async Task<string?> ConfigGetAsync(string key)
        {
            GitResult result = await context.Environment.CreateRunner().RunAsync(context.Repository.Path, ["config", "--null", "--get", key], token).ConfigureAwait(false);
            if (result.ExitCode == 1) { return null; }
            if (!result.Success) { throw new GitCommandException(["config", "--null", "--get", key], result); }
            return result.StandardOutput.EndsWith('\0') ? result.StandardOutput[..^1] : result.StandardOutput;
        }

        GitEnvironmentCheck CheckTranslation()
        {
            if (string.IsNullOrEmpty(language)) { return Check(GitEnvironmentCheckId.Translation, GitEnvironmentCheckStatus.Error, "_noLanguageConfigured", "There is no language configured for Git Extensions."); }
            LanguageCatalog catalog = LanguageCatalog.LoadDefault();
            return catalog.Find(language) is not null
                ? Check(GitEnvironmentCheckId.Translation, GitEnvironmentCheckStatus.Good, "_languageConfigured", "The configured language is {0}.", language)
                : Check(GitEnvironmentCheckId.Translation, GitEnvironmentCheckStatus.Error, "LanguageUnavailable", "The configured language is unavailable: {0}", language);
        }

        GitEnvironmentCheck CheckHome()
        {
            string? home = context.Environment.HomeDirectory;
            if (string.IsNullOrEmpty(home)) { return Check(GitEnvironmentCheckId.Home, GitEnvironmentCheckStatus.Error, "HomeMissing", "HOME is not configured."); }
            try
            {
                string path = Path.GetFullPath(home, context.Repository.Path);
                using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                _ = entries.MoveNext();
                return Check(GitEnvironmentCheckId.Home, GitEnvironmentCheckStatus.Good, "HomeAccessible", "HOME is accessible: {0}", home);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return Check(GitEnvironmentCheckId.Home, GitEnvironmentCheckStatus.Error, "HomeUnavailable", "HOME is not accessible: {0}", home) with { Message = Text("HomeUnavailable", "HOME is not accessible: {0}", home) + " " + error.Message }; }
        }

        string Information()
        {
            StringBuilder text = new();
            text.AppendLine(translations.Translate("Git executable:") + " " + diagnostic.Git.Executable);
            text.AppendLine(translations.Translate("Git version:") + " " + (diagnostic.Version?.ToString() ?? translations.Translate("Unavailable")));
            text.AppendLine(translations.Translate("Git local version probe:") + " " + ProbeInformation(diagnostic.Git));
            text.AppendLine(translations.Translate("Configuration context:") + " " + (context.GlobalOnly ? translations.Translate("Global configuration only") : context.Repository.Path));
            text.AppendLine("HOME: " + diagnostic.HomeDirectory);
            text.AppendLine("GIT_CONFIG_GLOBAL: " + (diagnostic.GlobalConfigOverride ?? translations.Translate("Native default")));
            foreach (string path in diagnostic.GlobalConfigCandidates) { text.AppendLine(translations.Translate("Global configuration candidate:") + " " + path); }
            text.AppendLine(translations.Translate("sh local probe:") + " " + ProbeInformation(diagnostic.Shell));
            text.AppendLine(translations.Translate("ssh local version probe:") + " " + ProbeInformation(diagnostic.Ssh));
            text.AppendLine(DescribeSshCommand(diagnostic, translations));
            text.AppendLine(translations.Translate("SSH authentication has not been tested."));
            foreach (string line in toolInformation) { text.AppendLine(line); }
            foreach (GitToolConfigurationScopeSnapshot scope in new[] { global, effective })
            {
                if (!scope.IsAvailable) { text.AppendLine(scope.View + ": " + scope.Diagnostic); }
                foreach (GitToolConfigurationOccurrence entry in scope.Occurrences.Concat(Enum.GetValues<GitConfigurationField>().SelectMany(field => scope.GetSetting(field).Occurrences))
                    .DistinctBy(entry => entry.Order))
                { text.AppendLine(scope.View + ": " + entry.Key + " ← " + entry.Origin); }
            }
            return text.ToString();
        }

        GitEnvironmentCheck Check(GitEnvironmentCheckId id, GitEnvironmentCheckStatus status, string item, string source, params object[] args)
            => new(id, status, Text(item, source, args));
        GitEnvironmentCheck Unavailable(GitEnvironmentCheckId id, string? error)
            => new(id, GitEnvironmentCheckStatus.Unavailable, translations.Translate("Unable to read Git configuration:") + " " + error);
        string Text(string item, string source, params object[] args)
            => string.Format(CultureInfo.CurrentCulture, translations.Translate("ChecklistSettingsPage", item, "Text", source), args);
    }

    private static string? Last(GitToolConfigurationValue value) => value.Occurrences.LastOrDefault()?.Value;
    private static string Failure(GitExecutableDiagnostic value) => value.Error ?? value.Result?.StandardError ?? "";
    private static string ProbeInformation(GitExecutableDiagnostic value)
        => value.Error ?? ("exit " + value.Result?.ExitCode + ": " + value.Result?.StandardOutput + value.Result?.StandardError).TrimEnd('\r', '\n');

    private static (bool Known, bool Available) FindNativeTool(string output, string name)
    {
        bool available = true, nativeBlock = false;
        foreach (string line in output.Split('\n'))
        {
            if (line.Contains("may be set to one of the following:", StringComparison.Ordinal)) { nativeBlock = true; continue; }
            if (line.Contains("not currently available:", StringComparison.Ordinal)) { nativeBlock = true; available = false; continue; }
            if (line.Trim() == "user-defined:") { nativeBlock = false; continue; }
            if (!nativeBlock || !line.StartsWith("\t\t", StringComparison.Ordinal)) { continue; }
            string? candidate = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.Equals(candidate, name, StringComparison.Ordinal)) { return (true, available); }
        }
        return (false, false);
    }
    internal static string DescribeSshCommand(GitEnvironmentDiagnostic diagnostic, TranslationCatalog translations)
        => diagnostic.SshConfiguration?.Source switch
        {
            GitSshCommandSource.GitSshCommand => translations.Translate("Git uses GIT_SSH_COMMAND. This command was not executed by the version check."),
            GitSshCommandSource.CoreSshCommand => translations.Translate("Git uses core.sshCommand. This command was not executed by the version check."),
            GitSshCommandSource.GitSsh => translations.Translate("Git uses the GIT_SSH executable; SSH variants and agents follow native Git settings."),
            GitSshCommandSource.Path => translations.Translate("Git uses ssh from PATH."),
            _ => translations.Translate("Unable to determine the Git SSH command:") + " " + diagnostic.SshConfiguration?.Error
        };
}
