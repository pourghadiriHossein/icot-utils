using System.Collections.Generic;

namespace ICOT.Core.Enrichment;

/// <summary>
/// Defines how enrichment keys are collected from a DTO and resolved values are applied back.
/// </summary>
public interface IBatchFieldMapping<TDto, TKey, TValue> where TKey : notnull
{
    void CollectKeys(TDto dto, ISet<TKey> missingKeys);
    void Apply(TDto dto, IReadOnlyDictionary<TKey, TValue> lookup);
}
