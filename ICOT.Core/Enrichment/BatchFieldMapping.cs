using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace ICOT.Core.Enrichment;

/// <summary>
/// Stateless, thread-safe factory for scalar, nullable-struct scalar, and collection batch field mappings.
/// </summary>
public static class BatchFieldMapping
{
    private delegate bool TryGetKeyDelegate<in TDto, TKey>(TDto dto, [MaybeNullWhen(false)] out TKey key);

    public static IBatchFieldMapping<TDto, TKey, TValue> Scalar<TDto, TKey, TValue>(
        Func<TDto, TKey?> getKey,
        Action<TDto, TValue?> setValue,
        Func<TDto, TValue?>? getCurrent = null,
        Func<TValue, TKey>? getValueKey = null,
        IEqualityComparer<TKey>? keyComparer = null,
        Func<TKey, bool>? isValidKey = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(getKey);
        ArgumentNullException.ThrowIfNull(setValue);

        bool TryGetKey(TDto dto, [MaybeNullWhen(false)] out TKey key)
        {
            var raw = getKey(dto);
            if (raw is null)
            {
                key = default;
                return false;
            }

            key = raw;
            return true;
        }

        return new ScalarBatchFieldMapping<TDto, TKey, TValue>(
            TryGetKey,
            setValue,
            getCurrent,
            getValueKey,
            keyComparer,
            isValidKey);
    }

    public static IBatchFieldMapping<TDto, TKey, TValue> NullableScalar<TDto, TKey, TValue>(
        Func<TDto, TKey?> getKey,
        Action<TDto, TValue?> setValue,
        Func<TDto, TValue?>? getCurrent = null,
        Func<TValue, TKey>? getValueKey = null,
        IEqualityComparer<TKey>? keyComparer = null,
        Func<TKey, bool>? isValidKey = null)
        where TKey : struct
    {
        ArgumentNullException.ThrowIfNull(getKey);
        ArgumentNullException.ThrowIfNull(setValue);

        bool TryGetKey(TDto dto, [MaybeNullWhen(false)] out TKey key)
        {
            var raw = getKey(dto);
            if (!raw.HasValue)
            {
                key = default;
                return false;
            }

            key = raw.Value;
            return true;
        }

        return new ScalarBatchFieldMapping<TDto, TKey, TValue>(
            TryGetKey,
            setValue,
            getCurrent,
            getValueKey,
            keyComparer,
            isValidKey);
    }

    public static IBatchFieldMapping<TDto, TKey, TValue> Collection<TDto, TKey, TValue>(
        Func<TDto, IEnumerable<TKey>?> getKeys,
        Action<TDto, List<TValue>> setValues,
        Func<TDto, IEnumerable<TValue>?>? getCurrent = null,
        Func<TValue, TKey>? getValueKey = null,
        IEqualityComparer<TKey>? keyComparer = null,
        Func<TKey, bool>? isValidKey = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(getKeys);
        ArgumentNullException.ThrowIfNull(setValues);

        return new CollectionBatchFieldMapping<TDto, TKey, TValue>(
            getKeys,
            setValues,
            getCurrent,
            getValueKey,
            keyComparer,
            isValidKey);
    }

    private static bool DefaultIsValidKey<TKey>(TKey key) where TKey : notnull
    {
        if (key is string str)
            return !string.IsNullOrWhiteSpace(str);

        return !EqualityComparer<TKey>.Default.Equals(key, default!);
    }

    private sealed class ScalarBatchFieldMapping<TDto, TKey, TValue> : IBatchFieldMapping<TDto, TKey, TValue>
        where TKey : notnull
    {
        private readonly TryGetKeyDelegate<TDto, TKey> _tryGetKey;
        private readonly Action<TDto, TValue?> _setValue;
        private readonly Func<TDto, TValue?>? _getCurrent;
        private readonly Func<TValue, TKey>? _getValueKey;
        private readonly IEqualityComparer<TKey> _keyComparer;
        private readonly Func<TKey, bool> _isValidKey;

        public ScalarBatchFieldMapping(
            TryGetKeyDelegate<TDto, TKey> tryGetKey,
            Action<TDto, TValue?> setValue,
            Func<TDto, TValue?>? getCurrent,
            Func<TValue, TKey>? getValueKey,
            IEqualityComparer<TKey>? keyComparer,
            Func<TKey, bool>? isValidKey)
        {
            _tryGetKey = tryGetKey;
            _setValue = setValue;
            _getCurrent = getCurrent;
            _getValueKey = getValueKey;
            _keyComparer = keyComparer ?? EqualityComparer<TKey>.Default;
            _isValidKey = isValidKey ?? DefaultIsValidKey;
        }

        public void CollectKeys(TDto dto, ISet<TKey> missingKeys)
        {
            if (!_tryGetKey(dto, out var validKey) || !_isValidKey(validKey))
            {
                if (_getCurrent != null && _getCurrent(dto) is not null)
                    _setValue(dto, default);
                return;
            }

            if (_getCurrent != null && _getValueKey != null)
            {
                var current = _getCurrent(dto);
                if (current is not null)
                {
                    if (_keyComparer.Equals(_getValueKey(current), validKey))
                        return;

                    _setValue(dto, default);
                }
            }

            missingKeys.Add(validKey);
        }

        public void Apply(TDto dto, IReadOnlyDictionary<TKey, TValue> lookup)
        {
            if (!_tryGetKey(dto, out var validKey) || !_isValidKey(validKey))
                return;

            if (_getCurrent != null && _getValueKey != null)
            {
                var current = _getCurrent(dto);
                if (current is not null && _keyComparer.Equals(_getValueKey(current), validKey))
                    return;
            }

            if (lookup.TryGetValue(validKey, out var resolved))
            {
                _setValue(dto, resolved);
            }
        }
    }

    private sealed class CollectionBatchFieldMapping<TDto, TKey, TValue> : IBatchFieldMapping<TDto, TKey, TValue>
        where TKey : notnull
    {
        private readonly Func<TDto, IEnumerable<TKey>?> _getKeys;
        private readonly Action<TDto, List<TValue>> _setValues;
        private readonly Func<TDto, IEnumerable<TValue>?>? _getCurrent;
        private readonly Func<TValue, TKey>? _getValueKey;
        private readonly IEqualityComparer<TKey> _keyComparer;
        private readonly Func<TKey, bool> _isValidKey;

        public CollectionBatchFieldMapping(
            Func<TDto, IEnumerable<TKey>?> getKeys,
            Action<TDto, List<TValue>> setValues,
            Func<TDto, IEnumerable<TValue>?>? getCurrent,
            Func<TValue, TKey>? getValueKey,
            IEqualityComparer<TKey>? keyComparer,
            Func<TKey, bool>? isValidKey)
        {
            _getKeys = getKeys;
            _setValues = setValues;
            _getCurrent = getCurrent;
            _getValueKey = getValueKey;
            _keyComparer = keyComparer ?? EqualityComparer<TKey>.Default;
            _isValidKey = isValidKey ?? DefaultIsValidKey;
        }

        public void CollectKeys(TDto dto, ISet<TKey> missingKeys)
        {
            var rawKeys = _getKeys(dto);
            if (rawKeys == null)
                return;

            HashSet<TKey>? existingKeys = null;
            foreach (var key in rawKeys)
            {
                if (key is null || !_isValidKey(key))
                    continue;

                existingKeys ??= BuildExistingKeySet(dto);
                if (!existingKeys.Contains(key))
                {
                    missingKeys.Add(key);
                }
            }
        }

        public void Apply(TDto dto, IReadOnlyDictionary<TKey, TValue> lookup)
        {
            var rawKeys = _getKeys(dto);
            if (rawKeys == null)
            {
                _setValues(dto, new List<TValue>());
                return;
            }

            var orderedKeys = new List<TKey>();
            var allowedKeys = new HashSet<TKey>(_keyComparer);
            foreach (var key in rawKeys)
            {
                if (key is not null && _isValidKey(key) && allowedKeys.Add(key))
                {
                    orderedKeys.Add(key);
                }
            }

            if (orderedKeys.Count == 0)
            {
                _setValues(dto, new List<TValue>());
                return;
            }

            var existingMap = BuildExistingValueMap(dto, allowedKeys);
            var result = new List<TValue>(orderedKeys.Count);
            foreach (var key in orderedKeys)
            {
                if (existingMap != null && existingMap.TryGetValue(key, out var existing))
                {
                    result.Add(existing);
                }
                else if (lookup.TryGetValue(key, out var loaded))
                {
                    result.Add(loaded);
                }
            }

            _setValues(dto, result);
        }

        private HashSet<TKey> BuildExistingKeySet(TDto dto)
        {
            var existingKeys = new HashSet<TKey>(_keyComparer);
            if (_getCurrent == null || _getValueKey == null)
                return existingKeys;

            var current = _getCurrent(dto);
            if (current == null)
                return existingKeys;

            foreach (var item in current)
            {
                if (item is null)
                    continue;

                var itemKey = _getValueKey(item);
                if (itemKey is not null && _isValidKey(itemKey))
                    existingKeys.Add(itemKey);
            }

            return existingKeys;
        }

        private Dictionary<TKey, TValue>? BuildExistingValueMap(TDto dto, HashSet<TKey> allowedKeys)
        {
            if (_getCurrent == null || _getValueKey == null)
                return null;

            var current = _getCurrent(dto);
            if (current == null)
                return null;

            var existingMap = new Dictionary<TKey, TValue>(_keyComparer);
            foreach (var item in current)
            {
                if (item is null)
                    continue;

                var itemKey = _getValueKey(item);
                if (itemKey is not null && allowedKeys.Contains(itemKey))
                {
                    existingMap.TryAdd(itemKey, item);
                }
            }

            return existingMap;
        }
    }
}
