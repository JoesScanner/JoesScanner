using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using System.Net;
using System.Text.RegularExpressions;

namespace JoesScanner.Services
{
    internal static class CommsMessageFormatter
    {
        // Matches HTML anchors: <a href="...">text</a>
        private static readonly Regex AnchorRegex = new(
            "<a\\s+[^>]*href\\s*=\\s*[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // Matches plain URLs.
        private static readonly Regex UrlRegex = new(
            "(?i)\\b((?:https?://|www\\.)[^\\s<]+)",
            RegexOptions.Compiled);

        public static FormattedString ToFormattedString(string? raw)
        {
            raw ??= string.Empty;

            // Fast path.
            if (raw.Length == 0)
                return new FormattedString();

            // Normalize common HTML breaks to newlines.
            var normalized = NormalizeBreaks(raw);

            var fs = new FormattedString();

            var lastIndex = 0;
            foreach (Match m in AnchorRegex.Matches(normalized))
            {
                if (!m.Success)
                    continue;

                // Text before the anchor.
                var before = normalized.Substring(lastIndex, m.Index - lastIndex);
                AppendTextWithPlainUrls(fs, StripTagsAndDecode(before));

                var href = WebUtility.HtmlDecode(m.Groups["href"].Value ?? string.Empty).Trim();
                var linkText = StripTagsAndDecode(m.Groups["text"].Value ?? string.Empty);

                if (string.IsNullOrWhiteSpace(linkText))
                    linkText = href;

                AppendLinkSpan(fs, linkText, href);

                lastIndex = m.Index + m.Length;
            }

            // Remainder.
            if (lastIndex < normalized.Length)
            {
                var tail = normalized.Substring(lastIndex);
                AppendTextWithPlainUrls(fs, StripTagsAndDecode(tail));
            }

            return fs;
        }

        private static void AppendTextWithPlainUrls(FormattedString fs, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var idx = 0;
            foreach (Match m in UrlRegex.Matches(text))
            {
                if (!m.Success)
                    continue;

                var urlRaw = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(urlRaw))
                    continue;

                // Add text before the URL.
                if (m.Index > idx)
                {
                    fs.Spans.Add(new Span { Text = text.Substring(idx, m.Index - idx) });
                }

                var (display, url) = NormalizeUrlForLink(urlRaw);
                AppendLinkSpan(fs, display, url);

                idx = m.Index + m.Length;
            }

            if (idx < text.Length)
            {
                fs.Spans.Add(new Span { Text = text.Substring(idx) });
            }
        }

        private static void AppendLinkSpan(FormattedString fs, string displayText, string href)
        {
            displayText ??= string.Empty;
            href ??= string.Empty;

            var (display, url) = NormalizeUrlForLink(displayText, href);

            var span = new Span
            {
                Text = display,
                TextDecorations = TextDecorations.Underline,
                // Do not use AppThemeColor here because it is not available in .NET MAUI.
                // Pick the color based on the current requested theme.
                TextColor = GetLinkColor()
            };

            var tap = new TapGestureRecognizer
            {
                Command = new Command(async () => await TryOpenAsync(url))
            };

            span.GestureRecognizers.Add(tap);
            fs.Spans.Add(span);
        }

        private static Color GetLinkColor()
        {
            try
            {
                var theme = Application.Current?.RequestedTheme ?? AppTheme.Unspecified;
                if (theme == AppTheme.Dark)
                    return Color.FromArgb("#60a5fa");

                // Light and fallback.
                return Color.FromArgb("#2563eb");
            }
            catch
            {
                return Color.FromArgb("#2563eb");
            }
        }

        private static async Task TryOpenAsync(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url))
                    return;

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return;

                await Launcher.OpenAsync(uri);
            }
            catch
            {
            }
        }

        private static (string display, string url) NormalizeUrlForLink(string raw)
        {
            raw ??= string.Empty;
            raw = raw.Trim();

            // Trim common trailing punctuation.
            raw = raw.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');

            var url = raw;
            if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            return (raw, url);
        }

        private static (string display, string url) NormalizeUrlForLink(string displayText, string href)
        {
            displayText ??= string.Empty;
            href ??= string.Empty;

            var display = displayText.Trim();
            if (display.Length == 0)
                display = href.Trim();

            var url = href.Trim();
            if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            // If someone pasted a plain domain without scheme, try https.
            if (url.Length > 0 && !url.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                {
                    // keep
                }
                else
                {
                    url = "https://" + url;
                }
            }

            // Trim trailing punctuation on url.
            url = url.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');

            return (display, url);
        }

        private static string NormalizeBreaks(string raw)
        {
            // Handle <br>, <br/>, <br /> and common paragraph endings.
            var s = raw
                .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("</p>", "\n\n", StringComparison.OrdinalIgnoreCase)
                .Replace("</div>", "\n", StringComparison.OrdinalIgnoreCase);

            return s;
        }

        private static string StripTagsAndDecode(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;

            // Remove any tags left behind after anchor extraction.
            var noTags = Regex.Replace(html, "<[^>]+>", string.Empty, RegexOptions.Singleline);
            return WebUtility.HtmlDecode(noTags);
        }
    }
}
