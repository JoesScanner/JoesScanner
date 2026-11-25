using System.Text.RegularExpressions;

namespace JoesScanner.Services;

internal static class DiagnosticRedaction
{
    private static readonly Regex AuthorizationHeader = new(
        @"(?i)(?<![\w-])((?:proxy[-_ ]?)?authorization[""']?\s*[:=]\s*)(?:\[REDACTED\]|""(?:\\.|[^""\r\n])*""|'(?:\\.|[^'\r\n])*'|[^\r\n]+)(?:\r?\n[ \t]+[^\r\n]*)*",
        RegexOptions.Compiled);
    private static readonly Regex CookieHeader = new(
        @"(?i)(?<![\w-])((?:set[-_ ]?)?cookie[""']?[ \t]*[:=][ \t]*)(?:\[REDACTED\]|""(?:\\.|[^""\r\n])*""|'(?:\\.|[^'\r\n])*'|[^\r\n]+)(?:\r?\n[ \t]+[^\r\n]*)*",
        RegexOptions.Compiled);
    private static readonly Regex VerificationCode = new(
        @"(?i)(\b(?:directory[ _-]+)?(?:email[ _-]+)?verification[ _-]+code\s+(?:is\s+)?)[0-9](?:[0-9 -]{2,}[0-9])",
        RegexOptions.Compiled);
    private static readonly Regex BasicToken = new(@"(?i)(\bBasic[ \t]+)(?!(?:auth(?:entication)?|credentials)\b)[A-Za-z0-9+/=]{8,}", RegexOptions.Compiled);
    private static readonly Regex BearerToken = new(@"(?i)(\bBearer[ \t]+)[^,;\s""']+", RegexOptions.Compiled);
    private static readonly Regex FavoritesToken = new(@"(?i)(\bJoesScannerAccount[ \t]+)[A-Za-z0-9+/=]{4,}", RegexOptions.Compiled);
    private static readonly Regex CredentialValue = new(
        """(?ix)(?<![\w-])(["']?(?:(?:(?:basic[_-]?auth|scanner|firewall|account|wordpress)[_-]?)?(?:password|passwd|passphrase|username|pwd)|(?:auth[_ -]?)?(?:(?:access|refresh|session|bearer|id|csrf)[_ -]?)?token|(?:(?:what3words|x)[_-]?)?api[_ -]?key|client[_-]?secret|(?:x[_-]?)?(?:directory[_ -]?)?(?:email[_ -]?)?(?:verification|challenge|recovery|one[_-]?time|email|directory)[_ -]?code|otp|(?:wp[_-]?)?nonce|(?:submission[_ -]?)?grant)["']?[ \t]*[:=][ \t]*)(\[REDACTED\]|"(?:\\.|[^"\r\n])*"|'(?:\\.|[^'\r\n])*'|[^\r\n]+)""",
        RegexOptions.Compiled);
    private static readonly Regex RemoteUrl = new(@"(?i)(?:\b(?:https?|wss?)://|(?<![\w:/])//)[^\s""'<>]+", RegexOptions.Compiled);
    private const string Host = @"(?:\[[0-9a-f:.%]+\]|[a-z0-9-]+(?:\.[a-z0-9-]+)*)";
    private static readonly Regex SchemelessUserInfo = new(
        @"(?i)(?<![\w/@.])[^\s:/@=<>""']+:[^\s/@<>""']*@" + Host + @"(?::[0-9]+)?[^\s""'<>]*",
        RegexOptions.Compiled);
    private static readonly Regex SchemelessUrl = new(
        @"(?i)(?<![\w/@.:])(?:[a-z0-9-]+(?:\.[a-z0-9-]+)+|localhost|\[[0-9a-f:.%]+\])(?::[0-9]+)?[/?#][^\s""'<>]*",
        RegexOptions.Compiled);
    private static readonly Regex SchemelessLocalQuery = new(
        @"(?i)(?<![\w/@.:])(?:[a-z0-9-]+)(?::[0-9]+)?(?:/[^?\s""'<>]*)?\?[^\s""'<>]+",
        RegexOptions.Compiled);
    private static readonly Regex RelativeQueryUrl = new(
        @"(?<![\w:/])/(?!/)[^?\s""'<>]*\?[^\s""'<>]+", RegexOptions.Compiled);
    private static readonly Regex LegacyValidationUrl = new(
        @"(?im)(\bValidate: url=)[^\r\n]*?(?=,[ \t]*isDefault=|\r?$)", RegexOptions.Compiled);
    private static readonly Regex LegacyValidationProbe = new(
        @"(?im)(\bValidate: sending HEAD to )[^\r\n]*?(?= \(auth=|\r?$)", RegexOptions.Compiled);
    private static readonly Regex LegacyReasonPhrase = new(
        @"(?im)(\b(?:Validate: response \((?:with|no) auth\) HTTP |History: calljson response headers received\.[^\r\n]*? status=)[0-9]{3})[ \t]+[^\r\n]+",
        RegexOptions.Compiled);
    private static readonly Regex RawPayload = new(
        @"(?i)(?<![\w-])(?:body|(?:response|request)[_ -]?(?:body|json)|payload|transcription|call[_-]?text)[""']?[ \t]*[:=][ \t]*",
        RegexOptions.Compiled);
    private static readonly Regex LogEntryStart = new(@"^\d{2}:\d{2}:\d{2} {2}", RegexOptions.Compiled);
    private static readonly Regex LegacyRadioStart = new(
        @"(?i)\bCallStream:[ \t]+(?!(?:INFO|HEARTBEAT) - )[^\r\n]*? - ",
        RegexOptions.Compiled);

    internal static string Describe(Exception error) =>
        error is HttpRequestException { StatusCode: { } status }
            ? $"{error.GetType().Name} (HTTP {(int)status})"
            : error.GetType().Name;

    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        if (LegacyRadioStart.Match(message) is { Success: true } radio)
            message = message[..radio.Index] + "CallStream: [recording content omitted]";
        // Raw bodies can echo a credential without a label. Retain only metadata before the body.
        if (RawPayload.Match(message) is { Success: true } payload)
            message = message[..(payload.Index + payload.Length)] + "[REDACTED]";

        var sanitized = LegacyValidationUrl.Replace(message, "$1[URL REDACTED]");
        sanitized = LegacyValidationProbe.Replace(sanitized, "$1[URL REDACTED]");
        sanitized = LegacyReasonPhrase.Replace(sanitized, "$1").Replace(@"\/", "/");
        sanitized = RemoteUrl.Replace(sanitized, match => RedactUrl(match.Value, schemeless: false));
        sanitized = SchemelessUserInfo.Replace(sanitized, match => RedactUrl(match.Value, schemeless: true));
        sanitized = SchemelessUrl.Replace(sanitized, match => RedactUrl(match.Value, schemeless: true));
        sanitized = SchemelessLocalQuery.Replace(sanitized, match => RedactUrl(match.Value, schemeless: true));
        sanitized = RelativeQueryUrl.Replace(sanitized, "[URL REDACTED]");
        sanitized = AuthorizationHeader.Replace(sanitized, "$1[REDACTED]");
        sanitized = CookieHeader.Replace(sanitized, "$1[REDACTED]");
        sanitized = VerificationCode.Replace(sanitized, "$1[REDACTED]");
        sanitized = BasicToken.Replace(sanitized, "$1[REDACTED]");
        sanitized = BearerToken.Replace(sanitized, "$1[REDACTED]");
        sanitized = FavoritesToken.Replace(sanitized, "$1[REDACTED]");
        return CredentialValue.Replace(sanitized, "$1[REDACTED]");
    }

    private static string RedactUrl(string value, bool schemeless)
    {
        var source = schemeless ? "https://" + value : value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value : value;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return "[URL REDACTED]";
        return schemeless ? uri.Authority : uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    internal static async Task WriteSanitizedLogAsync(TextReader source, TextWriter destination)
    {
        char? payloadQuote = null;
        var omittingRadioText = false;
        while (await source.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            // Older response loggers surrounded raw strings with quotes. Ignore
            // timestamps inside a continuing body until its closing quote.
            if (payloadQuote is { } quote)
            {
                if (line.TrimEnd().EndsWith(quote))
                    payloadQuote = null;
                continue;
            }
            if (omittingRadioText)
            {
                omittingRadioText = !line.Contains("[ServerUrl=", StringComparison.Ordinal);
                continue;
            }
            if (!LogEntryStart.IsMatch(line))
                continue;
            if (LegacyRadioStart.Match(line) is { Success: true } radio)
            {
                omittingRadioText = !line.Contains("[ServerUrl=", StringComparison.Ordinal);
                await destination.WriteLineAsync(Sanitize(line[..radio.Index]) +
                    "CallStream: [recording content omitted]").ConfigureAwait(false);
                continue;
            }
            var payload = RawPayload.Match(line);
            if (payload.Success)
            {
                var value = line[(payload.Index + payload.Length)..].TrimEnd();
                if (value.Length > 0 && value[0] is '\'' or '"' && (value.Length == 1 || value[^1] != value[0]))
                    payloadQuote = value[0];
            }
            await destination.WriteLineAsync(Sanitize(line)).ConfigureAwait(false);
        }
    }

    internal static string SanitizeAuthenticationMessage(string? message, params string[] secrets)
    {
        var safe = Sanitize(message);
        foreach (var secret in secrets.Where(value => !string.IsNullOrEmpty(value)).OrderByDescending(value => value.Length))
            safe = safe.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return safe;
    }
}
