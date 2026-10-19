namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    internal Func<AzureDevOpsSettings, IAzureDevOpsClient> AzureDevOpsClientFactory { get; set; }
        = settings => new AzureDevOpsApiClient(settings);
}
