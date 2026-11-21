using System.Collections.Generic;

namespace ICOT.Core.Enrichment;

/// <summary>
/// Evicts cached batch entries when underlying data changes.
/// </summary>
public interface IBatchCacheInvalidator<in TKey> where TKey : notnull
{
    void Invalidate(TKey key);
    void Invalidate(IEnumerable<TKey> keys);
}
