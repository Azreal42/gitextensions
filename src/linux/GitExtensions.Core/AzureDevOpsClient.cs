namespace GitExtensions.Linux;

public sealed class AzureDevOpsException(string message, int? statusCode = null) : InvalidOperationException(message)
{
    public int? StatusCode { get; } = statusCode;
}
public interface IAzureDevOpsClient : IAsyncDisposable
{
    Task<AzureDevOpsSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default);
    Task<string?> GetBuildDefinitionNameFromIdAsync(long buildId, CancellationToken cancellationToken = default);
    Uri? BuildPageUri(AzureDevOpsBuild build);
}
