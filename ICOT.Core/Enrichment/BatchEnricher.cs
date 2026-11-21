using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ICOT.Core.Enrichment;

/// <summary>
/// Orchestrates Gather -> Deduplicate -> Chunked Batch Fetch -> Scatter DTO enrichment
/// for both single DTOs and collections across local DB projections and remote gRPC services.
/// </summary>
public static class BatchEnricher
{
    public static Task EnrichAsync<TDto, TKey, TValue>(
        TDto? dto,
        IBatchLoader<TKey, TValue>? loader,
        IReadOnlyList<IBatchFieldMapping<TDto, TKey, TValue>> mappings,
        BatchEnricherOptions? options = null,
        IEqualityComparer<TKey>? keyComparer = null,
        CancellationToken cancellationToken = default)
        where TDto : class
        where TKey : notnull
    {
        if (dto == null || mappings == null || mappings.Count == 0)
            return Task.CompletedTask;

        return EnrichAsync(new[] { dto }, loader, mappings, options, keyComparer, cancellationToken);
    }

    public static Task EnrichAsync<TDto, TKey, TValue>(
        TDto? dto,
        IBatchLoader<TKey, TValue>? loader,
        IBatchFieldMapping<TDto, TKey, TValue> mapping,
        BatchEnricherOptions? options = null,
        IEqualityComparer<TKey>? keyComparer = null,
        CancellationToken cancellationToken = default)
        where TDto : class
        where TKey : notnull
    {
        if (dto == null || mapping == null)
            return Task.CompletedTask;

        return EnrichAsync(new[] { dto }, loader, new[] { mapping }, options, keyComparer, cancellationToken);
    }

    public static Task EnrichAsync<TDto, TKey, TValue>(
        IEnumerable<TDto>? dtos,
        IBatchLoader<TKey, TValue>? loader,
        IBatchFieldMapping<TDto, TKey, TValue> mapping,
        BatchEnricherOptions? options = null,
        IEqualityComparer<TKey>? keyComparer = null,
        CancellationToken cancellationToken = default)
        where TDto : class
        where TKey : notnull
    {
        if (dtos == null || mapping == null)
            return Task.CompletedTask;

        return EnrichAsync(dtos, loader, new[] { mapping }, options, keyComparer, cancellationToken);
    }

    public static async Task EnrichAsync<TDto, TKey, TValue>(
        IEnumerable<TDto>? dtos,
        IBatchLoader<TKey, TValue>? loader,
        IReadOnlyList<IBatchFieldMapping<TDto, TKey, TValue>> mappings,
        BatchEnricherOptions? options = null,
        IEqualityComparer<TKey>? keyComparer = null,
        CancellationToken cancellationToken = default)
        where TDto : class
        where TKey : notnull
    {
        if (dtos == null || mappings == null || mappings.Count == 0)
            return;

        var dtoList = dtos as IReadOnlyList<TDto> ?? dtos.ToList();
        if (dtoList.Count == 0)
            return;

        var comparer = keyComparer ?? EqualityComparer<TKey>.Default;
        var missingKeys = new HashSet<TKey>(comparer);

        foreach (var dto in dtoList)
        {
            if (dto == null)
                continue;

            for (var i = 0; i < mappings.Count; i++)
            {
                mappings[i].CollectKeys(dto, missingKeys);
            }
        }

        IReadOnlyDictionary<TKey, TValue> lookup;
        if (missingKeys.Count > 0 && loader != null)
        {
            var effectiveOptions = options ?? BatchEnricherOptions.Default;
            lookup = await LoadChunkedAsync(
                loader,
                missingKeys,
                effectiveOptions.MaxBatchSize,
                comparer,
                cancellationToken);
        }
        else
        {
            lookup = EmptyDictionary<TKey, TValue>.Instance;
        }

        foreach (var dto in dtoList)
        {
            if (dto == null)
                continue;

            for (var i = 0; i < mappings.Count; i++)
            {
                mappings[i].Apply(dto, lookup);
            }
        }
    }

    public static Task EnrichAsync<TDto, TKey, TValue>(
        TDto? dto,
        Func<IReadOnlyCollection<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>>? loaderFunc,
        IReadOnlyList<IBatchFieldMapping<TDto, TKey, TValue>> mappings,
        BatchEnricherOptions? options = null,
        IEqualityComparer<TKey>? keyComparer = null,
        CancellationToken cancellationToken = default)
        where TDto : class
        where TKey : notnull
    {
        if (dto == null || mappings == null || mappings.Count == 0)
            return Task.CompletedTask;

        return EnrichAsync(new[] { dto }, WrapLoader(loaderFunc), mappings, options, keyComparer, cancellationToken);
    }

    public static Task EnrichAsync<TDto, TKey, TValue>(
        TDto? dto,
        Func<IReadOnlyCollection<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>>? loaderFunc,
        IBatchFieldMapping<TDto, TKey, TValue> mapping,
        BatchEnricherOptions? options = null,
        IEqualityComparer<TKey>? keyComparer = null,
        CancellationToken cancellationToken = default)
        where TDto : class
        where TKey : notnull
    {
        if (dto == null || mapping == null)
            return Task.CompletedTask;

        return EnrichAsync(new[] { dto }, WrapLoader(loaderFunc), new[] { mapping }, options, keyComparer, cancellationToken);
    }

    public static Task EnrichAsync<TDto, TKey, TValue>(
        IEnumerable<TDto>? dtos,
        Func<IReadOnlyCollection<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>>? loaderFunc,
        IBatchFieldMapping<TDto, TKey, TValue> mapping,
        BatchEnricherOptions? options = null,
        IEqualityComparer<TKey>? keyComparer = null,
        CancellationToken cancellationToken = default)
        where TDto : class
        where TKey : notnull
    {
        if (dtos == null || mapping == null)
            return Task.CompletedTask;

        return EnrichAsync(dtos, WrapLoader(loaderFunc), new[] { mapping }, options, keyComparer, cancellationToken);
    }

    public static Task EnrichAsync<TDto, TKey, TValue>(
        IEnumerable<TDto>? dtos,
        Func<IReadOnlyCollection<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>>? loaderFunc,
        IReadOnlyList<IBatchFieldMapping<TDto, TKey, TValue>> mappings,
        BatchEnricherOptions? options = null,
        IEqualityComparer<TKey>? keyComparer = null,
        CancellationToken cancellationToken = default)
        where TDto : class
        where TKey : notnull
    {
        if (dtos == null || mappings == null || mappings.Count == 0)
            return Task.CompletedTask;

        return EnrichAsync(dtos, WrapLoader(loaderFunc), mappings, options, keyComparer, cancellationToken);
    }

    private static IBatchLoader<TKey, TValue>? WrapLoader<TKey, TValue>(
        Func<IReadOnlyCollection<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>>? loaderFunc)
        where TKey : notnull =>
        loaderFunc != null ? new DelegateBatchLoader<TKey, TValue>(loaderFunc) : null;

    private static async Task<IReadOnlyDictionary<TKey, TValue>> LoadChunkedAsync<TKey, TValue>(
        IBatchLoader<TKey, TValue> loader,
        HashSet<TKey> missingKeys,
        int maxBatchSize,
        IEqualityComparer<TKey> comparer,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        if (maxBatchSize <= 0 || missingKeys.Count <= maxBatchSize)
        {
            return await loader.LoadByIdsAsync(missingKeys, cancellationToken)
                   ?? EmptyDictionary<TKey, TValue>.Instance;
        }

        var merged = new Dictionary<TKey, TValue>(missingKeys.Count, comparer);
        foreach (var chunk in missingKeys.Chunk(maxBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkResult = await loader.LoadByIdsAsync(chunk, cancellationToken);
            if (chunkResult == null)
                continue;

            foreach (var (key, value) in chunkResult)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    private static class EmptyDictionary<TKey, TValue> where TKey : notnull
    {
        public static readonly IReadOnlyDictionary<TKey, TValue> Instance =
            new Dictionary<TKey, TValue>(0);
    }
}
