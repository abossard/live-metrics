using System.Text.RegularExpressions;

namespace LiveMetrics;

public static class LiveMetricsUtils
{
    private const string AsteriskChar = "*";
    private static readonly string[] RegexEscapeWithoutAsterisk = { "[", "]", "(", ")", "+", "?", "|", "{", "}" };
    private const string AsteriskPattern = ".*";

    private static string Sanitize(string s)
    {
        return s.Replace("-", "_").Replace(" ", "_").Replace(".", "_");
    }

    private static string ConvertStringToRegex(string s)
    {
        var cleanString = string.Join("", s.Split(RegexEscapeWithoutAsterisk, StringSplitOptions.RemoveEmptyEntries)).Replace(".", "\\.");
        return $"^{cleanString.Replace(AsteriskChar, AsteriskPattern)}$";
    }

    public static IEnumerable<string> ApplyWildcardToFilterList(IEnumerable<string> haystack, IEnumerable<string>? needles)
    {
        var needlesPattern = (needles ?? Array.Empty<string>()).Select(ConvertStringToRegex).ToArray();
        return haystack.Where(s => needlesPattern.Any(p => Regex.IsMatch(s, p))).ToHashSet();
    }

    public static string GetMetricsNameFor(string basename, string type, params string?[] parts)
    {
        return Sanitize(string.Join("_", new[] { basename }.Concat(parts).Concat(new []{ type }))).ToLowerInvariant();
    }
}