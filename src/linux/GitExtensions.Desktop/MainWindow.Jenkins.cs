using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    internal Func<JenkinsSettings, IBuildServerCredentials, IJenkinsClient> JenkinsClientFactory { get; set; }
        = (settings, credentials) => new JenkinsApiClient(settings, credentials);
}
