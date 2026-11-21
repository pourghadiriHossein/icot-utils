using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICOT.Core.Enrichment;

/// <summary>
/// Adapts an async delegate into an <see cref="IBatchLoader{TKey, TValue}"/>.
/// </summary>
public sealed class DelegateBatchLoader<TKey, TValue> : IBatchLoader<TKey, TValue>
    where TKey : notnull
{
    private readonly Func<IReadOnlyCollection<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> _loader;

    public DelegateBatchLoader(
        Func<IReadOnlyCollection<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> loader)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    public Task<IReadOnlyDictionary<TKey, TValue>> LoadByIdsAsync(
        IReadOnlyCollection<TKey> ids,
        CancellationToken cancellationToken = default)
    {
        return _loader(ids, cancellationToken);
    }
}
