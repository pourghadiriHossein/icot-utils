using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace ICOT.Core.Enrichment;

/// <summary>
/// Decorator for <see cref="IBatchLoader{TKey, TValue}"/> that resolves hits from <see cref="IMemoryCache"/>
/// and delegates only cache misses to the wrapped loader.
/// </summary>
public sealed class CachingBatchLoader<TKey, TValue> : IBatchLoader<TKey, TValue>, IBatchCacheInvalidator<TKey>
    where TKey : notnull
{
    private readonly IBatchLoader<TKey, TValue> _inner;
    private readonly IMemoryCache _cache;
    private readonly string _cacheKeyPrefix;
    private readonly TimeSpan _ttl;
    private readonly IEqualityComparer<TKey> _comparer;

    public CachingBatchLoader(
        IBatchLoader<TKey, TValue> inner,
        IMemoryCache cache,
        string cacheKeyPrefix,
        TimeSpan ttl,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKeyPrefix);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "Cache TTL must be greater than zero.");

        _cacheKeyPrefix = cacheKeyPrefix;
        _ttl = ttl;
        _comparer = keyComparer ?? EqualityComparer<TKey>.Default;
    }

    public static string BuildCacheKey(string prefix, TKey key) => $"{prefix}:{key}";

    public async Task<IReadOnlyDictionary<TKey, TValue>> LoadByIdsAsync(
        IReadOnlyCollection<TKey> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids == null || ids.Count == 0)
            return new Dictionary<TKey, TValue>(_comparer);

        var result = new Dictionary<TKey, TValue>(ids.Count, _comparer);
        var misses = new List<TKey>();
        var seen = new HashSet<TKey>(_comparer);

        foreach (var id in ids)
        {
            if (!seen.Add(id))
                continue;

            var cacheKey = BuildCacheKey(_cacheKeyPrefix, id);
            if (_cache.TryGetValue(cacheKey, out object? raw) && raw is TValue cachedValue)
            {
                result[id] = cachedValue;
            }
            else
            {
                misses.Add(id);
            }
        }

        if (misses.Count == 0)
            return result;

        var loaded = await _inner.LoadByIdsAsync(misses, cancellationToken);
        if (loaded == null)
            return result;

        foreach (var (key, value) in loaded)
        {
            if (value is not null)
            {
                _cache.Set(BuildCacheKey(_cacheKeyPrefix, key), value, _ttl);
            }

            result[key] = value;
        }

        return result;
    }

    public void Invalidate(TKey key)
    {
        _cache.Remove(BuildCacheKey(_cacheKeyPrefix, key));
    }

    public void Invalidate(IEnumerable<TKey> keys)
    {
        if (keys == null)
            return;

        foreach (var key in keys)
        {
            _cache.Remove(BuildCacheKey(_cacheKeyPrefix, key));
        }
    }
}
