using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AppVeyorRepositoryVariablesTests
{
    // Original RepoNameExtractorTest Windows cases; the folder is not a substitute for the remote URL.
    [TestCase("https://github.com/project/repo.git", "project", "repo")]
    [TestCase("file://github/project/repo.git", "project", "repo")]
    [TestCase("https://github.com/extra/extra/project/repo.git", "project", "repo")]
    [TestCase("https://github.com/project/", "project", "")]
    [TestCase("git@github.com/project/repo.git", "project", "repo")]
    [TestCase(null, "", "")]
    public void OriginalRemoteNameCasesKeepWindowsVariableMeaning(string? url, string project, string shortName)
    {
        BuildServerRepositoryVariables variables = BuildServerRepositoryVariables.FromRemoteUrl(url);
        Assert.That(variables.Project, Is.EqualTo(project)); Assert.That(variables.ShortName, Is.EqualTo(shortName));
        Assert.That(variables.Expand("{cRepoProject}/{cRepoShortName}|{unknown}"),
            Is.EqualTo((project.Length == 0 ? "{cRepoProject}" : project) + "/" + (shortName.Length == 0 ? "{cRepoShortName}" : shortName) + "|{unknown}"));
    }

    [Test]
    public async Task ActualBranchFetchRemoteWinsOverOriginPushUrlAndFolderAndDetachedUsesFirstRemote()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        await f.Git.GitAsync("remote", "add", "origin", "https://github.com/other/origin.git");
        await f.Git.GitAsync("remote", "add", "aaa", "https://github.com/first/fallback.git");
        await f.Git.GitAsync("remote", "add", "upstream", "https://github.com/team/actual.git");
        await f.Git.GitAsync("remote", "set-url", "--push", "upstream", "https://github.com/push/wrong.git");
        string branch = (await f.Repository!.GetStatusAsync()).Branch!;
        await f.Git.GitAsync("config", "branch." + branch + ".remote", "upstream");
        BuildServerRepositoryVariables tracked = await f.Repository.GetBuildServerRepositoryVariablesAsync();
        Assert.That(tracked.Expand("{cRepoProject}/{cRepoShortName}"), Is.EqualTo("team/actual"));
        await f.Git.GitAsync("checkout", "--detach");
        BuildServerRepositoryVariables detached = await f.Repository.GetBuildServerRepositoryVariablesAsync();
        Assert.That(detached.Expand("{cRepoProject}/{cRepoShortName}"), Is.EqualTo("first/fallback"));
        await f.Git.GitAsync("remote", "remove", "aaa"); await f.Git.GitAsync("remote", "remove", "origin"); await f.Git.GitAsync("remote", "remove", "upstream");
        BuildServerRepositoryVariables none = await f.Repository.GetBuildServerRepositoryVariablesAsync();
        Assert.That(none.Expand("{cRepoProject}/{cRepoShortName}"), Is.EqualTo("{cRepoProject}/{cRepoShortName}"));
    }
}
