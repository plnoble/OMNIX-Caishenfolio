namespace Caishenfolio.Host.Data;

public sealed class ProviderResult<T>
{
    public required bool Ok { get; init; }
    public required string Provider { get; init; }
    public T? Data { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
    public double? LatencyMs { get; init; }
    public bool FromCache { get; init; }

    public static ProviderResult<T> Success(
        string provider,
        T data,
        IEnumerable<string>? warnings = null,
        double? latencyMs = null,
        bool fromCache = false) =>
        new()
        {
            Ok = true,
            Provider = provider,
            Data = data,
            Warnings = warnings?.ToArray() ?? Array.Empty<string>(),
            LatencyMs = latencyMs,
            FromCache = fromCache,
        };

    public static ProviderResult<T> Failure(
        string provider,
        string error,
        IEnumerable<string>? warnings = null) =>
        new()
        {
            Ok = false,
            Provider = provider,
            Error = error,
            Warnings = warnings?.ToArray() ?? Array.Empty<string>(),
        };
}
