namespace GitExtensions.Linux;

public sealed class JenkinsException(string message, int? statusCode = null) : InvalidOperationException(message)
{
    public int? StatusCode { get; } = statusCode;
}
public interface IJenkinsClient : IAsyncDisposable
{
    Task<JenkinsSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default);
    Uri? BuildPageUri(JenkinsBuild build);
}
