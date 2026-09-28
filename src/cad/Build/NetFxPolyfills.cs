// Linked into every project by Directory.Build.props so Contracts, Core and the plugin compile from one
// source for both .NET 8 (AutoCAD 2025) and .NET Framework 4.7/4.8 (AutoCAD 2020-2024).
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    // .NET Framework has no init-only setter marker, so records and init properties need this shim.
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }

    // CallerArgumentExpression is .NET 6+; the shim keeps argument names in exception messages identical.
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;

        public string ParameterName { get; }
    }
}

// NotNullAttribute is .NET Core 3.0+; without it the shared ThrowIfNull shim would stop telling the
// compiler that the argument is non-null afterwards, silently weakening analysis on the .NET 8 path too.
namespace System.Diagnostics.CodeAnalysis
{
    using System.ComponentModel;

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class NotNullAttribute : Attribute
    {
    }
}
#endif

#if NETFRAMEWORK
// Index/Range are .NET Core 3.0+; range expressions need the types to exist on .NET Framework.
namespace System
{
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        public Index(int value, bool fromEnd)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "value must be non-negative");
            }

            _value = fromEnd ? ~value : value;
        }

        private Index(int value) => _value = value;

        public static Index Start => new Index(0);

        public static Index End => new Index(~0);

        public static Index FromStart(int value) => new Index(value, fromEnd: false);

        public static Index FromEnd(int value) => new Index(value, fromEnd: true);

        public int Value => _value < 0 ? ~_value : _value;

        public bool IsFromEnd => _value < 0;

        public int GetOffset(int length)
        {
            int offset = _value;
            if (IsFromEnd)
            {
                offset += length + 1;
            }

            return offset;
        }

        public override bool Equals(object? value) => value is Index index && _value == index._value;

        public bool Equals(Index other) => _value == other._value;

        public override int GetHashCode() => _value;

        public override string ToString() => IsFromEnd ? "^" + Value : Value.ToString();

        public static implicit operator Index(int value) => FromStart(value);

        public static bool operator ==(Index left, Index right) => left._value == right._value;

        public static bool operator !=(Index left, Index right) => left._value != right._value;
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public Index Start { get; }

        public Index End { get; }

        public static Range StartAt(Index start) => new Range(start, Index.End);

        public static Range EndAt(Index end) => new Range(Index.Start, end);

        public static Range All => new Range(Index.Start, Index.End);

        public override bool Equals(object? value) =>
            value is Range range && Start.Equals(range.Start) && End.Equals(range.End);

        public bool Equals(Range other) => other.Start.Equals(Start) && other.End.Equals(End);

        public override int GetHashCode() => (Start.GetHashCode() * 397) ^ End.GetHashCode();

        public override string ToString() => Start + ".." + End;

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            return (start, end - start);
        }
    }
}
#endif

// Static members the .NET Framework BCL lacks. Unconditional on purpose: shared source calls the bare
// name (see Build/NetFxGlobalUsings.cs), so net8 and net4x take exactly one code path with one semantic.
namespace CadTranslation.Compat
{
    using System.Runtime.CompilerServices;

    internal static class CompatShims
    {
        public static void ThrowIfNull(
            [System.Diagnostics.CodeAnalysis.NotNull] object? argument,
            [CallerArgumentExpression("argument")] string? paramName = null)
        {
            if (argument is null)
            {
                // Thrown inline on purpose: [NotNull] needs the flow analysis to see that the null path
                // never returns normally, which a helper method would hide (CS8777).
                throw new ArgumentNullException(paramName);
            }
        }

        public static void ThrowIfNullOrWhiteSpace(
            string? argument,
            [CallerArgumentExpression("argument")] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (string.IsNullOrWhiteSpace(argument))
            {
                throw new ArgumentException("The value cannot be null or whitespace.", paramName);
            }
        }

        // char.IsAsciiLetter/IsAsciiLetterOrDigit are .NET 7+; same semantics, ASCII range only.
        public static bool IsAsciiLetter(char value) => (uint)((value | 0x20) - 'a') <= 25u;

        public static bool IsAsciiLetterOrDigit(char value) =>
            IsAsciiLetter(value) || (uint)(value - '0') <= 9u;

        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static double Clamp(double value, double min, double max)
        {
            if (min > max)
            {
                ThrowMinMax(min, max);
            }

            return value < min ? min : value > max ? max : value;
        }

        public static float Clamp(float value, float min, float max)
        {
            if (min > max)
            {
                ThrowMinMax(min, max);
            }

            return value < min ? min : value > max ? max : value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (min > max)
            {
                ThrowMinMax(min, max);
            }

            return value < min ? min : value > max ? max : value;
        }

        public static long Clamp(long value, long min, long max)
        {
            if (min > max)
            {
                ThrowMinMax(min, max);
            }

            return value < min ? min : value > max ? max : value;
        }

        public static string ToHexString(byte[] inArray)
        {
            char[] text = new char[inArray.Length * 2];
            for (int index = 0; index < inArray.Length; index++)
            {
                byte value = inArray[index];
                text[index * 2] = HexDigit(value >> 4);
                text[(index * 2) + 1] = HexDigit(value & 15);
            }

            return new string(text);
        }

        private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'A' + (value - 10));

        // Path.IsPathFullyQualified is .NET Core 2.1+. Same documented rule: drive-absolute, UNC and
        // device paths qualify; "\dir" (rooted-relative) and "C:dir" (drive-relative) do not. These are
        // containment guards, so the rule must not be looser than the BCL one.
        public static bool IsPathFullyQualified(string path)
        {
            ThrowIfNull(path);
            if (path.Length == 0)
            {
                return false;
            }

            if (path.Length >= 3 && IsLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]))
            {
                return true;
            }

            return path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);
        }

        // Path.GetRelativePath is .NET Core 2.0+; callers pass already-expanded directory paths.
        public static string GetRelativePath(string relativeTo, string path)
        {
            ThrowIfNull(relativeTo);
            ThrowIfNull(path);
            string from = Path.GetFullPath(relativeTo).TrimEnd('\\', '/');
            string to = Path.GetFullPath(path);
            Uri fromUri = new Uri(from + Path.DirectorySeparatorChar);
            Uri toUri = new Uri(to);
            string relative = Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString());
            return relative.Replace('/', Path.DirectorySeparatorChar);
        }

        private static bool IsSeparator(char value) => value == '\\' || value == '/';

        private static bool IsLetter(char value) => (uint)((value | 0x20) - 'a') <= 25u;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowMinMax<T>(T min, T max) =>
            throw new ArgumentException("'" + min + "' cannot be greater than '" + max + "'.");
    }
}

#if NETFRAMEWORK
// Members the .NET Framework BCL lacks that can be supplied as extensions, so call sites stay unchanged.
// net4x only: on .NET 8 these would be ambiguous with the real BCL members.
namespace CadTranslation.Compat
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;

    internal static class NetFxExtensions
    {
        public static bool Contains(this string value, char character) => value.IndexOf(character) >= 0;

        public static bool Contains(this string value, string text, StringComparison comparison) =>
            value.IndexOf(text, comparison) >= 0;

        public static bool StartsWith(this string value, char character) =>
            value.Length > 0 && value[0] == character;

        public static bool EndsWith(this string value, char character) =>
            value.Length > 0 && value[value.Length - 1] == character;

        public static string[] Split(this string value, char separator, StringSplitOptions options) =>
            value.Split(new[] { separator }, options);

        public static string[] Split(this string value, string? separator, StringSplitOptions options) =>
            value.Split(separator is null ? new string[0] : new[] { separator }, options);

        public static List<TSource> Order<TSource>(this IEnumerable<TSource> source) =>
            source.OrderBy(item => item).ToList();

        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
        {
            HashSet<TKey> seen = new HashSet<TKey>();
            foreach (TSource item in source)
            {
                if (seen.Add(keySelector(item)))
                {
                    yield return item;
                }
            }
        }

        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IEqualityComparer<TKey>? comparer)
        {
            HashSet<TKey> seen = new HashSet<TKey>(comparer);
            foreach (TSource item in source)
            {
                if (seen.Add(keySelector(item)))
                {
                    yield return item;
                }
            }
        }

#if !NET48_OR_GREATER
        // Enumerable.Append/Prepend only reach .NET Framework in 4.7.1+; AutoCAD 2020 (net47) lacks them.
        public static IEnumerable<TSource> Append<TSource>(this IEnumerable<TSource> source, TSource element)
        {
            foreach (TSource item in source)
            {
                yield return item;
            }

            yield return element;
        }

        public static IEnumerable<TSource> Prepend<TSource>(this IEnumerable<TSource> source, TSource element)
        {
            yield return element;
            foreach (TSource item in source)
            {
                yield return item;
            }
        }
#endif

        // Enumerable.Zip(second) is .NET Core 3.0+; same pairwise truncation at the shorter sequence.
        public static IEnumerable<(TFirst First, TSecond Second)> Zip<TFirst, TSecond>(
            this IEnumerable<TFirst> first,
            IEnumerable<TSecond> second)
        {
            using IEnumerator<TFirst> left = first.GetEnumerator();
            using IEnumerator<TSecond> right = second.GetEnumerator();
            while (left.MoveNext() && right.MoveNext())
            {
                yield return (left.Current, right.Current);
            }
        }

        // Enumerable.FirstOrDefault(defaultValue) is .NET 6+.
        public static TSource FirstOrDefault<TSource>(this IEnumerable<TSource> source, TSource defaultValue)
        {
            foreach (TSource item in source)
            {
                return item;
            }

            return defaultValue;
        }

        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> source, TKey key, TValue value)
        {
            if (source.ContainsKey(key))
            {
                return false;
            }

            source.Add(key, value);
            return true;
        }

        public static bool Remove<TKey, TValue>(this Dictionary<TKey, TValue> source, TKey key, out TValue? value)
        {
            if (source.TryGetValue(key, out TValue? found))
            {
                value = found;
                source.Remove(key);
                return true;
            }

            value = default;
            return false;
        }

        public static void Deconstruct<TKey, TValue>(
            this KeyValuePair<TKey, TValue> pair,
            out TKey key,
            out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }

        public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> source, TKey key) =>
            source.TryGetValue(key, out TValue? value) ? value! : default!;

        public static TValue GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> source,
            TKey key,
            TValue defaultValue) =>
            source.TryGetValue(key, out TValue? value) ? value! : defaultValue;

        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> source, TKey key) =>
            source.TryGetValue(key, out TValue? value) ? value! : default!;

        public static TValue GetValueOrDefault<TKey, TValue>(
            this IReadOnlyDictionary<TKey, TValue> source,
            TKey key,
            TValue defaultValue) =>
            source.TryGetValue(key, out TValue? value) ? value! : defaultValue;

#if !NET48_OR_GREATER
        // .NET Framework 4.8 already ships Enumerable.ToHashSet; defining it there is ambiguous.
        public static HashSet<TSource> ToHashSet<TSource>(this IEnumerable<TSource> source) =>
            new HashSet<TSource>(source);

        public static HashSet<TSource> ToHashSet<TSource>(
            this IEnumerable<TSource> source,
            IEqualityComparer<TSource>? comparer) =>
            new HashSet<TSource>(source, comparer);
#endif

        // string.Replace(string, string, StringComparison) is .NET Core 2.0+; same replacement order
        // and the same argument checks, so shared source keeps one behaviour on both runtimes.
        public static string Replace(this string value, string oldValue, string newValue, StringComparison comparison)
        {
            if (oldValue is null)
            {
                throw new ArgumentNullException(nameof(oldValue));
            }

            if (oldValue.Length == 0)
            {
                throw new ArgumentException("The value cannot be an empty string.", nameof(oldValue));
            }

            string replacement = newValue ?? string.Empty;
            StringBuilder builder = new StringBuilder();
            int start = 0;
            int index;
            while ((index = value.IndexOf(oldValue, start, comparison)) >= 0)
            {
                builder.Append(value, start, index - start).Append(replacement);
                start = index + oldValue.Length;
            }

            return builder.Append(value, start, value.Length - start).ToString();
        }
    }
}
#endif
