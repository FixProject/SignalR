namespace FixerUpper
{
    using System.Collections.Generic;

    static class DictionaryExtensions
    {
        public static T GetValueOrDefault<T>(this IDictionary<string, object> dict, string key,
            T defaultValue = default(T))
        {
            object value;
            if (!dict.TryGetValue(key, out value)) return defaultValue;
            return value != null ? (T) value : defaultValue;
        }
    }
}