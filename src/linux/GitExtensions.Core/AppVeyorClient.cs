namespace GitExtensions.Linux;

public sealed class AppVeyorException(string message, int? statusCode = null) : InvalidOperationException(message)
{
    public int? StatusCode { get; } = statusCode;
}

public interface IAppVeyorClient : IAsyncDisposable
{
    Task<AppVeyorSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default);
    Uri? BuildPageUri(AppVeyorBuild build);
}
