namespace ErsatzTV.Extensions;

public static class QueryCollectionExtensions
{
    private static readonly System.Collections.Generic.HashSet<string> ReservedParameters =
        new(StringComparer.OrdinalIgnoreCase) { "mode", "access_token", "index" };

    public static Dictionary<string, string> CustomParameters(this IQueryCollection query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, Microsoft.Extensions.Primitives.StringValues value) in query)
        {
            if (!ReservedParameters.Contains(key))
            {
                result[key] = value.ToString();
            }
        }

        return result;
    }

    public static string ToQueryString(this IReadOnlyDictionary<string, string> parameters) =>
        string.Join(
            '&',
            parameters
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

    public static string AppendQuery(this string baseQuery, string extraQuery)
    {
        if (string.IsNullOrWhiteSpace(extraQuery))
        {
            return baseQuery;
        }

        return string.IsNullOrWhiteSpace(baseQuery) ? $"?{extraQuery}" : $"{baseQuery}&{extraQuery}";
    }
}
