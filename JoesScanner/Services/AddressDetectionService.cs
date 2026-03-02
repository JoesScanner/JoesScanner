using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JoesScanner.Models;

namespace JoesScanner.Services
{
    // Lightweight, local-only address extraction from call transcription text.
    // This is intentionally heuristic-based: fast, offline-capable, and safe to run frequently.
    public class AddressDetectionService : IAddressDetectionService
    {
        private readonly ISettingsService _settings;

        // Basic US-style street suffixes commonly spoken on radio.
        private static readonly string[] StreetSuffixes =
        {
            "st","street","ave","avenue","rd","road","blvd","boulevard","ln","lane","dr","drive",
            "ct","court","way","pkwy","parkway","pl","place","ter","terrace","cir","circle",
            "hwy","highway"
        };

        private const string SuffixAlternation = "st|street|ave|avenue|rd|road|blvd|boulevard|ln|lane|dr|drive|ct|court|way|pkwy|parkway|pl|place|ter|terrace|cir|circle|hwy|highway";

        // Directionals commonly spoken as part of an address.
        // Used both for no-suffix forms and for post-directionals like "13th Avenue South".
        private const string DirectionAlternation = "north|south|east|west|n|s|e|w|ne|nw|se|sw";

        // Example matches:
        // - 123 Main St
        // - 4500 E Franklin Rd
        // - 10 N 12th Street
        // Captures: number + up to ~6 tokens + suffix + optional post-directional.
        private static readonly Regex StreetAddressRegex = new(
            pattern: @"\b(?<num>\d{1,6})[,_\-]?\s+(?<body>(?:(?:[A-Za-z][A-Za-z0-9\.]*|[0-9]{1,2}(?:st|nd|rd|th)?)[,_\-]?\s+){0,8})(?<suffix>(" + SuffixAlternation + @"))\b(?:[,_\-]?\s+(?<postdir>(?:" + DirectionAlternation + @")))?\b",
            options: RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // No-suffix variants that are commonly spoken on radio, e.g.:
        // - 3500 West Franklin
        // - 1200 W Main
        // We require a directional token to reduce false positives.
// what3words format: ///word.word.word
// We accept either with or without the leading /// to handle varied transcription output.
private static readonly Regex What3WordsRegex = new(
    pattern: @"(?<![A-Za-z0-9])(?:/{3})?(?<w1>\p{L}{2,})\.(?<w2>\p{L}{2,})\.(?<w3>\p{L}{2,})(?![A-Za-z0-9])",
    options: RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StreetAddressNoSuffixRegex = new(
            pattern: @"\b(?<num>\d{1,6})[,_\-]?\s+(?<dir>(?:" + DirectionAlternation + @"))\b[,_\-]?\s+(?!of\b)(?<name>(?:[A-Za-z][A-Za-z0-9\.]*\s+){0,2}[A-Za-z][A-Za-z0-9\.]*)\b",
            options: RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Rural/grid addressing patterns commonly spoken in parts of Idaho/Utah, e.g.:
        // - 4500 east 3200 north
        // - 4500 E 3200 N
        // - 4500 e, 3200 n
        // We keep this separate to avoid expanding the no-suffix regex (which would increase false positives).
        private static readonly Regex GridAddressRegex = new(
            pattern: @"\b(?<num1>\d{1,6})[,_\-]?\s+(?<dir1>(?:" + DirectionAlternation + @"))\b[,_\-]?\s+(?<num2>\d{1,6})[,_\-]?\s+(?<dir2>(?:" + DirectionAlternation + @"))\b",
            options: RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public AddressDetectionService(ISettingsService settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public void Apply(CallItem item)
        {
            if (item == null)
                return;

            ApplyWhat3Words(item);

            // Disabled is the default: do nothing except clear cached fields.
            if (!_settings.AddressDetectionEnabled)
            {
                Clear(item);
                return;
            }

            var text = item.Transcription ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                Clear(item);
                return;
            }

            // Tuning knobs
            var minChars = Clamp(_settings.AddressDetectionMinAddressChars, 0, 200);
            var minConfidence = Clamp(_settings.AddressDetectionMinConfidencePercent, 0, 100);
            var maxCandidates = Clamp(_settings.AddressDetectionMaxCandidatesPerCall, 1, 10);

            

            // Fast-path: avoid re-scanning if the transcription and tuning knobs have not changed.
            // (String hash is per-process randomized; that is fine because this cache is in-memory only.)
            var appliedHash = HashCode.Combine(
                _settings.AddressDetectionEnabled,
                text,
                minChars,
                minConfidence,
                maxCandidates);

            if (item.AddressDetectionAppliedHash == appliedHash)
                return;

            item.AddressDetectionAppliedHash = appliedHash;
var shouldLog = AppLog.IsEnabled;
            var callKey = string.IsNullOrWhiteSpace(item.BackendId) ? item.Timestamp.ToString("O") : item.BackendId;

            if (shouldLog)
            {
                AppLog.Add(() => $"AddrDetect: start call={callKey} textLen={text.Length} minChars={minChars} minConf={minConfidence} maxCand={maxCandidates}");
            }

            // Find candidates
            var candidates = new List<(string Address, int Confidence, bool HasKnownSuffix)>(maxCandidates);

            MatchCollection matches;
            try
            {
                matches = StreetAddressRegex.Matches(text);
            }
            catch (Exception ex)
            {
                if (shouldLog)
                    AppLog.Add(() => $"AddrDetect: regex error call={callKey}. {ex.Message}");
                Clear(item);
                return;
            }

            if (shouldLog)
                AppLog.Add(() => $"AddrDetect: regexMatches={matches.Count} call={callKey}");

            var debugLogged = 0;
            const int MaxPerCallDebugLines = 12;

            foreach (Match m in matches)
            {
                if (!m.Success)
                    continue;

                var num = m.Groups["num"].Value;
                var body = (m.Groups["body"].Value ?? string.Empty).Trim();
                var suffix = m.Groups["suffix"].Value;
                var postDir = (m.Groups["postdir"].Value ?? string.Empty).Trim();

                // Recompose and normalize spacing.
                var rawCandidate = string.IsNullOrWhiteSpace(postDir)
                    ? $"{num} {body} {suffix}".Trim()
                    : $"{num} {body} {suffix} {postDir}".Trim();
                var candidate = NormalizeCandidate(rawCandidate);
                candidate = TrimToHouseNumber(candidate);
                candidate = CanonicalizeAfterSuffix(candidate);

                if (shouldLog && debugLogged < MaxPerCallDebugLines)
                {
                    AppLog.Add(() => $"AddrDetect: candRaw='{rawCandidate}' candNorm='{candidate}' call={callKey}");
                    debugLogged++;
                }

                if (candidate.Length < minChars)
                {
                    if (shouldLog && debugLogged < MaxPerCallDebugLines)
                    {
                        AppLog.Add(() => $"AddrDetect: reject tooShort len={candidate.Length} minChars={minChars} call={callKey}");
                        debugLogged++;
                    }
                    continue;
                }

                // Avoid duplicates.
                if (candidates.Any(c => string.Equals(c.Address, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    if (shouldLog && debugLogged < MaxPerCallDebugLines)
                    {
                        AppLog.Add(() => $"AddrDetect: reject duplicate call={callKey}");
                        debugLogged++;
                    }
                    continue;
                }

                var confidence = ScoreCandidate(candidate, suffix, GetContextWindow(text, m.Index, m.Length));
                var hasKnownSuffix = ContainsKnownSuffix(candidate);

                if (shouldLog && debugLogged < MaxPerCallDebugLines)
                {
                    AppLog.Add(() => $"AddrDetect: score {confidence}% for '{candidate}' call={callKey}");
                    debugLogged++;
                }

                // Apply tuning thresholds
                if (confidence < minConfidence)
                {
                    if (shouldLog && debugLogged < MaxPerCallDebugLines)
                    {
                        AppLog.Add(() => $"AddrDetect: reject belowThreshold {confidence}%<{minConfidence}% call={callKey}");
                        debugLogged++;
                    }
                    continue;
                }

                candidates.Add((candidate, confidence, hasKnownSuffix));
                if (candidates.Count >= maxCandidates)
                    break;
            }

            // If we still have room, try the no-suffix pattern (directional + street name).
            if (candidates.Count < maxCandidates)
            {
                // First: grid-style addressing (number + dir + number + dir)
                MatchCollection gridMatches;
                try
                {
                    gridMatches = GridAddressRegex.Matches(text);
                }
                catch (Exception ex)
                {
                    if (shouldLog)
                        AppLog.Add(() => $"AddrDetect: grid regex error call={callKey}. {ex.Message}");
                    gridMatches = null;
                }

                if (gridMatches != null)
                {
                    if (shouldLog)
                        AppLog.Add(() => $"AddrDetect: gridMatches={gridMatches.Count} call={callKey}");

                    foreach (Match m in gridMatches)
                    {
                        if (!m.Success)
                            continue;

                        var num1 = m.Groups["num1"].Value;
                        var dir1 = NormalizeDirectionalToken(m.Groups["dir1"].Value);
                        var num2 = m.Groups["num2"].Value;
                        var dir2 = NormalizeDirectionalToken(m.Groups["dir2"].Value);

                        // Format as a map-friendly civic pattern.
                        var rawCandidate = $"{num1} {dir1} {num2} {dir2}".Trim();
                        var candidate = NormalizeCandidate(rawCandidate);
                        candidate = TrimToHouseNumber(candidate);

                        if (shouldLog && debugLogged < MaxPerCallDebugLines)
                        {
                            AppLog.Add(() => $"AddrDetect: candGridRaw='{rawCandidate}' candNorm='{candidate}' call={callKey}");
                            debugLogged++;
                        }

                        if (candidate.Length < minChars)
                            continue;

                        if (candidates.Any(c => string.Equals(c.Address, candidate, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var confidence = ScoreCandidate(candidate, suffix: string.Empty, GetContextWindow(text, m.Index, m.Length));
                        if (confidence < minConfidence)
                            continue;

                        candidates.Add((candidate, confidence, HasKnownSuffix: false));
                        if (candidates.Count >= maxCandidates)
                            break;
                    }
                }

                if (candidates.Count >= maxCandidates)
                {
                    goto CandidatesDone;
                }

                MatchCollection noSuffixMatches;
                try
                {
                    noSuffixMatches = StreetAddressNoSuffixRegex.Matches(text);
                }
                catch (Exception ex)
                {
                    if (shouldLog)
                        AppLog.Add(() => $"AddrDetect: noSuffix regex error call={callKey}. {ex.Message}");
                    noSuffixMatches = null;
                }

                if (noSuffixMatches != null)
                {
                    if (shouldLog)
                        AppLog.Add(() => $"AddrDetect: noSuffixMatches={noSuffixMatches.Count} call={callKey}");

                    foreach (Match m in noSuffixMatches)
                    {
                        if (!m.Success)
                            continue;

                        var num = m.Groups["num"].Value;
                        var dir = (m.Groups["dir"].Value ?? string.Empty).Trim();
                        var name = (m.Groups["name"].Value ?? string.Empty).Trim();

                        var rawCandidate = $"{num} {dir} {name}".Trim();
                        var candidate = NormalizeCandidate(rawCandidate);
                        candidate = TrimToHouseNumber(candidate);
                        candidate = CanonicalizeAfterSuffix(candidate);

                        if (shouldLog && debugLogged < MaxPerCallDebugLines)
                        {
                            AppLog.Add(() => $"AddrDetect: candNoSuffixRaw='{rawCandidate}' candNorm='{candidate}' call={callKey}");
                            debugLogged++;
                        }

                        if (candidate.Length < minChars)
                            continue;

                        if (candidates.Any(c => string.Equals(c.Address, candidate, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var confidence = ScoreCandidate(candidate, suffix: string.Empty, GetContextWindow(text, m.Index, m.Length));
                        if (confidence < minConfidence)
                            continue;

                        var hasKnownSuffix = ContainsKnownSuffix(candidate);
                        candidates.Add((candidate, confidence, hasKnownSuffix));
                        if (candidates.Count >= maxCandidates)
                            break;
                    }
                }
            }

        CandidatesDone:

            if (candidates.Count == 0)
            {
                if (shouldLog)
                    AppLog.Add(() => $"AddrDetect: noAcceptedCandidates call={callKey}");
                Clear(item);
                return;
            }

            var ordered = candidates
                .OrderByDescending(c => c.Confidence)
                .ThenByDescending(c => c.HasKnownSuffix)
                .ThenBy(c => c.Address.Length)
                .ToList();

            item.DetectedAddress = ordered[0].Address;
            item.DetectedAddressConfidencePercent = ordered[0].Confidence;

            if (ordered.Count > 1)
            {
                item.DetectedAddressCandidates = string.Join("\n", ordered.Skip(1).Select(c => $"{c.Address} ({c.Confidence}%)"));
            }
            else
            {
                item.DetectedAddressCandidates = string.Empty;
            }

            if (shouldLog)
            {
                var candCount = ordered.Count;
                AppLog.Add(() => $"AddrDetect: selected '{item.DetectedAddress}' {item.DetectedAddressConfidencePercent}% candidates={candCount} call={callKey}");
            }
        }

        private void ApplyWhat3Words(CallItem item)
{
            var text = item.Transcription ?? string.Empty;

            if (!_settings.What3WordsLinksEnabled)
            {
                // When disabled, clear display value and do not cache the hash so enabling later will rescan.
                item.What3WordsAddress = string.Empty;
                item.What3WordsAppliedHash = 0;
                return;
            }

    var appliedHash = HashCode.Combine(text);
    if (item.What3WordsAppliedHash == appliedHash)
        return;

    item.What3WordsAppliedHash = appliedHash;

    if (string.IsNullOrWhiteSpace(text))
    {
        item.What3WordsAddress = string.Empty;
        return;
    }

    try
    {
        var m = What3WordsRegex.Match(text);
        if (!m.Success)
        {
            item.What3WordsAddress = string.Empty;
            return;
        }

        var w1 = m.Groups["w1"].Value;
        var w2 = m.Groups["w2"].Value;
        var w3 = m.Groups["w3"].Value;

        if (string.IsNullOrWhiteSpace(w1) || string.IsNullOrWhiteSpace(w2) || string.IsNullOrWhiteSpace(w3))
        {
            item.What3WordsAddress = string.Empty;
            return;
        }

        var shouldLog = AppLog.IsEnabled;
        var callKey = string.IsNullOrWhiteSpace(item.BackendId) ? item.Timestamp.ToString("O") : item.BackendId;

        var normalized = $"{w1}.{w2}.{w3}".ToLowerInvariant();
        item.What3WordsAddress = $"///{normalized}";

        if (shouldLog)
            AppLog.Add(() => $"W3W: detected '{item.What3WordsAddress}' call={callKey}");
    }
    catch
    {
        item.What3WordsAddress = string.Empty;
    }
}

        private static void Clear(CallItem item)
        {
            item.DetectedAddress = string.Empty;
            
            item.AddressDetectionAppliedHash = 0;
item.DetectedAddressConfidencePercent = 0;
            item.DetectedAddressCandidates = string.Empty;
        }

        private static string NormalizeCandidate(string s)
        {
            // Collapse internal whitespace and normalize common transcript punctuation.
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            s = Regex.Replace(s, @"\s+", " ").Trim();

            // Strip trailing punctuation that frequently appears in transcriptions.
            s = s.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '\'', '!', '?');

            // Remove stray commas/underscores/hyphens that split tokens (e.g., "Blaine," or "Blaine-St").
            s = s.Replace(" ,", ",");
            s = s.Replace(",", "");
            s = s.Replace("_", " ");
            s = s.Replace("-", " ");

            // Re-collapse whitespace after edits.
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        private static string TrimToHouseNumber(string s)
        {
            // Some transcriptions include leading incident/unit/call-type numbers (e.g. "25 sick person 4500 ...").
            // If a plausible house number (3-6 digits) appears later in the candidate, trim to start at that number.
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            var m = Regex.Match(s, @"\b\d{3,6}\b");
            if (!m.Success)
            {
                return s.Trim();
            }

            // If the first plausible house number is not already at the beginning, trim to it.
            return s.Substring(m.Index).Trim();
        }

        private static string CanonicalizeAfterSuffix(string candidate)
        {
            // Transcripts can include non-address tokens immediately after an address
            // (unit IDs, names, shorthand like "fsm frank").
            // If we see a known street suffix token, trim the candidate to end at the suffix.
            //
            // Special case: allow a trailing directional suffix (e.g. "1st Street South", "Main St N").
            // This is common in civic addressing and is usually spoken immediately after the street type.
            if (string.IsNullOrWhiteSpace(candidate))
                return string.Empty;

            var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return candidate.Trim();

            // Find the first suffix token that appears after the house number.
            //
            // Nuance: some legitimate street names contain a suffix word as part of the street name,
            // followed immediately by the true suffix.
            // Example: "34 Parkway Place" (Parkway is the name token; Place is the suffix).
            // If we trimmed at the first suffix token, we'd incorrectly return "34 Parkway".
            //
            // To handle this, once we encounter a suffix token, we walk forward across any *consecutive*
            // suffix tokens and treat the last one as the suffix boundary.
            var suffixIndex = -1;
            for (var i = 1; i < parts.Length; i++)
            {
                var token = parts[i].Trim().TrimEnd('.', ',', ';', ':');
                if (!IsKnownSuffixToken(token))
                    continue;

                suffixIndex = i;

                // Prefer the last suffix in a consecutive run (e.g. "Parkway Place").
                while (suffixIndex + 1 < parts.Length)
                {
                    var maybeNext = parts[suffixIndex + 1].Trim().TrimEnd('.', ',', ';', ':');
                    if (!IsKnownSuffixToken(maybeNext))
                        break;
                    suffixIndex++;
                }

                break;
            }

            if (suffixIndex < 0)
                return candidate.Trim();

            var endIndex = suffixIndex;

            // Allow a trailing directional immediately after the suffix (e.g. "Street South", "St S").
            if (suffixIndex + 1 < parts.Length)
            {
                var next = parts[suffixIndex + 1].Trim().TrimEnd('.', ',', ';', ':');
                if (IsDirectionalToken(next))
                {
                    endIndex = suffixIndex + 1;
                }
            }

            // Allow a minimal unit marker sequence, e.g. "apt 3", "unit 12", "# 2".
            if (endIndex + 1 < parts.Length)
            {
                var next = parts[endIndex + 1].Trim().TrimEnd('.', ',', ';', ':');
                if (IsUnitMarker(next))
                {
                    endIndex = Math.Min(parts.Length - 1, endIndex + 2);
                }
            }

            return string.Join(' ', parts.Take(endIndex + 1)).Trim();
        }

        private static bool ContainsKnownSuffix(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var token = parts[i].Trim().TrimEnd('.', ',', ';', ':');
                if (IsKnownSuffixToken(token))
                    return true;
            }

            return false;
        }

        private static bool IsKnownSuffixToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            return StreetSuffixes.Any(s => string.Equals(s, token, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUnitMarker(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            return string.Equals(token, "apt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "apartment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "unit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "suite", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "ste", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "#", StringComparison.OrdinalIgnoreCase);
        }


        private static bool IsDirectionalToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            // Normalize common punctuation already trimmed by callers, but be defensive.
            token = token.Trim().TrimEnd('.', ',', ';', ':');

            return string.Equals(token, "north", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "south", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "east", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "west", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "n", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "s", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "e", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "w", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "ne", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "nw", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "se", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "sw", StringComparison.OrdinalIgnoreCase);
        }

        private static int ScoreCandidate(string candidate, string suffix, string contextWindow)
        {
            // Heuristic scoring tuned for radio-style transcripts.
            // We want a plain "2804 Blaine St" to clear a default 70% threshold.
            //
            // Signals:
            // - Starts with a street number: strong (+25)
            // - Has a known suffix: strong (+35)
            // - Has a plausible street name token (word after number): (+15)
            // - Longer names (more tokens): small bias (+0..15)
            // - Directionals (E, W, etc): small bonus (+5)
            // - Unit markers: penalty (-10)

            // Additional signal:
            // - Rural/grid addressing ("4500 E 3200 N"): strong (+40)

            var score = 0;

            // Reject obviously-bogus house numbers that show up in ASR output.
            // In dispatch audio, a leading '0' is almost never a real civic address.
            if (Regex.IsMatch(candidate, @"^0\b"))
                return 0;

            // In Treasure Valley dispatch audio, real street numbers are overwhelmingly 3-5 digits.
            // 1-2 digit "numbers" are far more likely to be radio codes, unit IDs, or ASR artifacts.
            // We allow < 100 only when the phrase strongly looks like a civic address (contains a directional).
            var leadingNum = 0;
            var leadingNumMatch = Regex.Match(candidate, @"^(?<n>\d{1,6})\b");
            if (leadingNumMatch.Success && int.TryParse(leadingNumMatch.Groups["n"].Value, out leadingNum))
            {
                if (leadingNum < 10)
                    return 0;

                var hasDirectional = Regex.IsMatch(candidate, @"\b(north|south|east|west|n|s|e|w|ne|nw|se|sw)\b", RegexOptions.IgnoreCase);
                if (leadingNum < 100 && !hasDirectional)
                    return 0;
            }


            if (Regex.IsMatch(candidate, @"^\d{1,6}\b"))
                score += 25;

            if (!string.IsNullOrWhiteSpace(suffix))
                score += 35;

            var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // e.g., ["2804","Blaine","St"] -> has street name token
            if (words.Length >= 2 && words[1].Length >= 3)
                score += 15;

            // token count bias: 3 words is normal; 4-6 words adds modest confidence
            if (words.Length >= 4)
                score += Math.Min(15, (words.Length - 3) * 5);

            if (Regex.IsMatch(candidate, @"\b(N|S|E|W|NE|NW|SE|SW)\b", RegexOptions.IgnoreCase))
                score += 5;

            // Grid style (number + dir + number + dir). This is distinct and usually very intentional in dispatch audio.
            if (string.IsNullOrWhiteSpace(suffix)
                && Regex.IsMatch(candidate, @"^\d{1,6}\s+(north|south|east|west|n|s|e|w|ne|nw|se|sw)\s+\d{1,6}\s+(north|south|east|west|n|s|e|w|ne|nw|se|sw)\b", RegexOptions.IgnoreCase))
            {
                score += 40;
            }

            // Some dispatch audio omits the suffix entirely (e.g. "3500 West Franklin").
            // If we have a directional token plus a plausible street name, boost confidence.
            if (string.IsNullOrWhiteSpace(suffix))
            {
                var hasDirectional = Regex.IsMatch(candidate, @"\b(north|south|east|west|n|s|e|w|ne|nw|se|sw)\b", RegexOptions.IgnoreCase);
                var lastToken = words.Length > 0 ? words[^1] : string.Empty;
                var lastTokenLooksLikeStreetName = lastToken.Length >= 4 && Regex.IsMatch(lastToken, @"^[A-Za-z][A-Za-z0-9\.]*$", RegexOptions.IgnoreCase);
                if (hasDirectional && words.Length >= 3 && lastTokenLooksLikeStreetName)
                {
                    score += 25;
                }
            }

            // Context-based adjustments.
            // If the surrounding transcript clearly frames this as an address/location, boost.
            // If it reads like radio codes/status chatter, penalize.
            if (!string.IsNullOrWhiteSpace(contextWindow))
            {
                if (Regex.IsMatch(contextWindow, @"\b(address|location|loc|at|located|on|near|block of|in front of|intersection|cross streets?)\b", RegexOptions.IgnoreCase))
                    score += 10;

                if (Regex.IsMatch(contextWindow, @"\b(status|signal|code|tac|channel|unit|copy|negative|affirmative|en\s*route|clear)\b", RegexOptions.IgnoreCase))
                    score -= 20;
            }

            
            // Penalize 'cross' phrasing which is commonly associated with cross-street narration
            // rather than a precise civic address (and can include ASR artifacts).
            if (Regex.IsMatch(candidate, @"\bcross\b", RegexOptions.IgnoreCase))
                score -= 30;

if (Regex.IsMatch(candidate, @"\b(apt|apartment|unit|suite|ste|#)\b", RegexOptions.IgnoreCase))
                score -= 10;

            score = Clamp(score, 0, 100);
            return score;
        }

        private static string NormalizeDirectionalToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            token = token.Trim().TrimEnd('.', ',', ';', ':');

            if (string.Equals(token, "north", StringComparison.OrdinalIgnoreCase)) return "N";
            if (string.Equals(token, "south", StringComparison.OrdinalIgnoreCase)) return "S";
            if (string.Equals(token, "east", StringComparison.OrdinalIgnoreCase)) return "E";
            if (string.Equals(token, "west", StringComparison.OrdinalIgnoreCase)) return "W";

            // Already abbreviated (or diagonal). Normalize to upper.
            return token.ToUpperInvariant();
        }

        private static string GetContextWindow(string text, int matchIndex, int matchLength)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            // Take a small window around the match to catch phrases like "at" or "address is".
            // Keep this short to avoid accidental boosts/penalties from unrelated parts of the transcript.
            const int Window = 45;
            var start = Math.Max(0, matchIndex - Window);
            var end = Math.Min(text.Length, matchIndex + matchLength + Window);
            var slice = text.Substring(start, Math.Max(0, end - start));
            slice = Regex.Replace(slice, @"\s+", " ").Trim();
            return slice;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
