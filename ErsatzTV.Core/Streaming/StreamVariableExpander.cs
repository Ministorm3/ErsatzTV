using System.Text.RegularExpressions;

namespace ErsatzTV.Core.Streaming;

public static partial class StreamVariableExpander
{
    // caller-supplied values are substituted into urls that ffmpeg then opens;
    // anything longer than this, or carrying control characters, is treated as
    // absent so the template's own default is used instead
    private const int MaximumValueLength = 256;

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>();

    public static bool HasVariables(string input) =>
        !string.IsNullOrEmpty(input) && VariablePattern().IsMatch(input);

    /// <summary>
    ///     Expands a template containing no caller-supplied values, so every <c>query:</c> variable resolves to its
    ///     declared default. The result contains only administrator-authored content.
    /// </summary>
    public static string ExpandWithDefaults(string input) =>
        ExpandUnescaped(input, Option<string>.None, EmptyParameters);

    /// <summary>
    ///     Expands a template whose result is used as a URL. Caller-supplied values are percent-encoded, and the
    ///     expanded URL is required to keep the scheme, host and port the template resolves to without them. A value
    ///     that would steer the URL elsewhere, or a template with no origin to preserve, resolves without
    ///     caller-supplied values at all.
    /// </summary>
    public static string ExpandUrl(
        string input,
        Option<string> channelNumber,
        IReadOnlyDictionary<string, string> queryParameters)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        // the channel number and the declared defaults, and nothing a caller
        // supplied; this is both the origin the expanded url has to agree with
        // and the result to fall back to when it does not
        string trusted = ExpandUnescaped(input, channelNumber, EmptyParameters);

        if (queryParameters is null || queryParameters.Count == 0)
        {
            return trusted;
        }

        string expanded = Expand(input, channelNumber, queryParameters, Uri.EscapeDataString);

        // a template that is not an absolute uri has no origin to hold the
        // substitution to, so caller-supplied values cannot be bounded and are
        // refused rather than trusted
        if (!Uri.TryCreate(trusted, UriKind.Absolute, out Uri trustedUri))
        {
            return trusted;
        }

        if (Uri.TryCreate(expanded, UriKind.Absolute, out Uri expandedUri) &&
            string.Equals(expandedUri.Scheme, trustedUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(expandedUri.Host, trustedUri.Host, StringComparison.OrdinalIgnoreCase) &&
            expandedUri.Port == trustedUri.Port)
        {
            return expanded;
        }

        return trusted;
    }

    /// <summary>
    ///     Substitutes values verbatim, applying no escaping of any kind. Only safe when every value is
    ///     administrator-authored. A template expanded for a new destination needs escaping appropriate to that
    ///     destination — a URL needs <see cref="ExpandUrl" />, and a command line would need argument quoting.
    /// </summary>
    public static string ExpandUnescaped(string input, Option<string> channelNumber) =>
        ExpandUnescaped(input, channelNumber, EmptyParameters);

    /// <inheritdoc cref="ExpandUnescaped(string, Option{string})" />
    public static string ExpandUnescaped(
        string input,
        Option<string> channelNumber,
        IReadOnlyDictionary<string, string> queryParameters) =>
        Expand(input, channelNumber, queryParameters, static value => value);

    private static string Expand(
        string input,
        Option<string> channelNumber,
        IReadOnlyDictionary<string, string> queryParameters,
        Func<string, string> escape)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        queryParameters ??= EmptyParameters;

        return VariablePattern().Replace(
            input,
            match =>
            {
                // defaults and the channel number come from the template and the
                // database, so they are escaped no more than the surrounding
                // template is; only caller-supplied values cross a trust boundary
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
                        return IsAcceptableValue(v) ? escape(v) : fallback;
                    }
                }

                return fallback;
            });
    }

    private static bool IsAcceptableValue(string value) =>
        value is not null && value.Length <= MaximumValueLength && !value.Any(char.IsControl);

    [GeneratedRegex(
        @"\{(?<name>channel_number|query:(?<key>[A-Za-z0-9_.-]+))(?:\|(?<default>[^{}]*))?\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
}
