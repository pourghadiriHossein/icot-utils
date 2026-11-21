using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICOT.Core.Enrichment;

/// <summary>
/// Loads values in batches by unique keys from a local or remote data source.
/// </summary>
public interface IBatchLoader<TKey, TValue> where TKey : notnull
{
    Task<IReadOnlyDictionary<TKey, TValue>> LoadByIdsAsync(
        IReadOnlyCollection<TKey> ids,
        CancellationToken cancellationToken = default);
}
