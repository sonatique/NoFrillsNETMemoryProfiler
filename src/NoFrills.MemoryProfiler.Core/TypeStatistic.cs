using System.Text.RegularExpressions;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>How much memory one type occupies in a snapshot.</summary>
public sealed record TypeStatistic(string TypeName, int Count, long TotalBytes)
{
    public long AverageBytes => Count == 0 ? 0 : TotalBytes / Count;
}

/// <summary>Selects the objects to analyze: by type name, by size, or both.</summary>
public sealed class ObjectFilter
{
    /// <summary>A common pooled buffer: 2 MiB, sixteen bytes of alignment, and the array header.</summary>
    public const int DefaultPoolBlockSize = 2_097_192;

    public string? TypeNamePattern { get; init; }
    public bool UseRegularExpression { get; init; }
    public int MinimalSize { get; init; }
    public int MaximalSize { get; init; } = int.MaxValue;

    private Regex? m_regex;

    public static ObjectFilter ForType(string typeName) => new() { TypeNamePattern = typeName };

    public static ObjectFilter ForExactSize(int size) => new() { MinimalSize = size, MaximalSize = size };

    public static ObjectFilter ForPoolBlocks(int blockSize = DefaultPoolBlockSize) => new()
    {
        TypeNamePattern = "System.Byte[]",
        MinimalSize = blockSize,
        MaximalSize = blockSize
    };

    public bool MatchesType(string typeName)
    {
        if (string.IsNullOrEmpty(TypeNamePattern)) return true;

        if (!UseRegularExpression)
        {
            return typeName.Contains(TypeNamePattern, StringComparison.OrdinalIgnoreCase);
        }

        m_regex ??= new Regex(TypeNamePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return m_regex.IsMatch(typeName);
    }

    public override string ToString()
    {
        var type = string.IsNullOrEmpty(TypeNamePattern) ? "any type" : $"type ~ \"{TypeNamePattern}\"";

        if (MinimalSize == 0 && MaximalSize == int.MaxValue) return type;
        if (MinimalSize == MaximalSize) return $"{type}, size = {MinimalSize:N0} B";

        return $"{type}, size {MinimalSize:N0} .. {(MaximalSize == int.MaxValue ? "max" : MaximalSize.ToString("N0"))} B";
    }
}
