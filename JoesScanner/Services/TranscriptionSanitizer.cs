using System;
using System.Collections.Generic;
using System.Text;

namespace JoesScanner.Services
{
    // Lightweight, always-on transcription sanitation.
    //
    // Goals:
    // 1) Keep the UI safe from oversized payloads.
    // 2) Normalize whitespace.
    // 3) Remove pathological repetition (stutter loops) where a word or short phrase repeats more than 3 times in a row.
    public static class TranscriptionSanitizer
    {
        private const int MaxIncomingChars = 20000;

        public static string Sanitize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var s = text.Trim();
            if (s.Length > MaxIncomingChars)
                s = s.Substring(0, MaxIncomingChars);

            // Whitespace normalization (fast, allocation-light)
            var sb = new StringBuilder(s.Length);
            var lastWasSpace = true; // trim leading whitespace
            foreach (var ch in s)
            {
                var c = ch;
                if (c == '\r' || c == '\n' || c == '\t')
                    c = ' ';

                if (char.IsWhiteSpace(c))
                {
                    if (lastWasSpace)
                        continue;
                    sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                sb.Append(c);
                lastWasSpace = false;
            }

            var normalized = sb.ToString().Trim();
            if (normalized.Length == 0)
                return string.Empty;

            // Remove repetitive runs (word or short phrase repeated 4+ times consecutively)
            return RemoveRepetitiveRuns(normalized);
        }

        private static string RemoveRepetitiveRuns(string normalized)
        {
            // Split on single spaces only (we normalized whitespace already)
            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4)
                return normalized;

            var output = new List<string>(tokens.Length);
            var i = 0;

            // Phrase lengths to consider (in tokens)
            const int maxPhraseLen = 6;
            const int minPhraseLen = 2;
            const int minRepeatsToCull = 4; // more than 3

            while (i < tokens.Length)
            {
                var handled = false;

                // Phrase repetition (2-6 tokens), repeated consecutively 4+ times
                for (var phraseLen = Math.Min(maxPhraseLen, tokens.Length - i); phraseLen >= minPhraseLen; phraseLen--)
                {
                    var required = phraseLen * minRepeatsToCull;
                    if (i + required > tokens.Length)
                        continue;

                    var repeats = CountConsecutivePhraseRepeats(tokens, i, phraseLen);
                    if (repeats >= minRepeatsToCull)
                    {
                        // Keep only the first instance of the phrase
                        for (var k = 0; k < phraseLen; k++)
                            output.Add(tokens[i + k]);

                        i += phraseLen * repeats;
                        handled = true;
                        break;
                    }
                }

                if (handled)
                    continue;

                // Single-word repetition, repeated consecutively 4+ times
                var wordRepeats = CountConsecutiveWordRepeats(tokens, i);
                if (wordRepeats >= minRepeatsToCull)
                {
                    output.Add(tokens[i]); // keep first
                    i += wordRepeats;       // skip the rest
                    continue;
                }

                output.Add(tokens[i]);
                i++;
            }

            return string.Join(' ', output);
        }

        private static int CountConsecutiveWordRepeats(string[] tokens, int start)
        {
            var key = NormalizeTokenForCompare(tokens[start]);
            var count = 1;

            for (var j = start + 1; j < tokens.Length; j++)
            {
                if (!string.Equals(key, NormalizeTokenForCompare(tokens[j]), StringComparison.Ordinal))
                    break;
                count++;
            }

            return count;
        }

        private static int CountConsecutivePhraseRepeats(string[] tokens, int start, int phraseLen)
        {
            var repeats = 1;

            while (true)
            {
                var nextStart = start + (repeats * phraseLen);
                if (nextStart + phraseLen > tokens.Length)
                    break;

                if (!PhraseEquals(tokens, start, nextStart, phraseLen))
                    break;

                repeats++;
            }

            return repeats;
        }

        private static bool PhraseEquals(string[] tokens, int aStart, int bStart, int len)
        {
            for (var k = 0; k < len; k++)
            {
                var a = NormalizeTokenForCompare(tokens[aStart + k]);
                var b = NormalizeTokenForCompare(tokens[bStart + k]);
                if (!string.Equals(a, b, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static string NormalizeTokenForCompare(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            var s = token.Trim();

            // Trim common leading/trailing punctuation so "copy," matches "copy".
            var start = 0;
            var end = s.Length - 1;

            while (start <= end && IsTrimPunct(s[start]))
                start++;
            while (end >= start && IsTrimPunct(s[end]))
                end--;

            if (start > end)
                return string.Empty;

            s = s.Substring(start, end - start + 1);
            return s.ToLowerInvariant();
        }

        private static bool IsTrimPunct(char c)
        {
            // Keep internal apostrophes, but trim quotes, commas, periods, parens, etc.
            return char.IsPunctuation(c) || char.IsSymbol(c);
        }
    }
}
