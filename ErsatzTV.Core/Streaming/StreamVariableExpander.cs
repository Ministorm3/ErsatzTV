using System.Text.RegularExpressions;

namespace ErsatzTV.Core.Streaming;

public static partial class StreamVariableExpander
{
    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>();

    public static bool HasVariables(string input) =>
        !string.IsNullOrEmpty(input) && VariablePattern().IsMatch(input);

    public static string Expand(string input, Option<string> channelNumber) =>
        Expand(input, channelNumber, EmptyParameters);

    public static string ExpandWithDefaults(string input) =>
        Expand(input, Option<string>.None, EmptyParameters);

    public static string Expand(
        string input,
        Option<string> channelNumber,
        IReadOnlyDictionary<string, string> queryParameters)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return VariablePattern().Replace(
            input,
            match =>
            {
                string fallback = match.Groups["default"].Success ? match.Groups["default"].Value : string.Empty;

                if (match.Groups["name"].Value is "channel_number")
                {
                    return channelNumber.IfNone(fallback);
                }

                string key = match.Groups["key"].Value;
                foreach ((string k, string v) in queryParameters)
                {
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    {
                        return v;
                    }
                }

                return fallback;
            });
    }

    [GeneratedRegex(
        @"\{(?<name>channel_number|query:(?<key>[A-Za-z0-9_.-]+))(?:\|(?<default>[^{}]*))?\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
}
