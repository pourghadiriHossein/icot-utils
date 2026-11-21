namespace ICOT.Core.Enrichment;

/// <summary>
/// Controls batching behavior for <see cref="BatchEnricher"/>.
/// </summary>
public sealed class BatchEnricherOptions
{
    public const int DefaultMaxBatchSize = 200;

    public static BatchEnricherOptions Default { get; } = new();

    /// <summary>
    /// Maximum number of unique keys dispatched per loader invocation. Values &lt;= 0 disable chunking.
    /// </summary>
    public int MaxBatchSize { get; init; } = DefaultMaxBatchSize;
}
