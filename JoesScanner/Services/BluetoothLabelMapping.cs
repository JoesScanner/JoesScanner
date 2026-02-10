using JoesScanner.Models;

namespace JoesScanner.Services
{
    public static class BluetoothLabelMapping
    {
        public const string TokenAppName = "AppName";
        public const string TokenTranscription = "Transcription";
        public const string TokenTalkgroup = "Talkgroup";
        public const string TokenSite = "Site";
        public const string TokenReceiver = "Receiver";

        public sealed class Option
        {
            public string Key { get; }
            public string Label { get; }

            public Option(string key, string label)
            {
                Key = key;
                Label = label;
            }
        }

        public static readonly IReadOnlyList<Option> Options = new List<Option>
        {
            new Option(TokenAppName, "App name"),
            new Option(TokenTranscription, "Transcription"),
            new Option(TokenTalkgroup, "Talk group"),
            new Option(TokenSite, "Site"),
            new Option(TokenReceiver, "Receiver")
        };

        public static string NormalizeToken(string? raw, string fallback)
        {
            var token = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token))
                return fallback;

            if (Options.Any(o => string.Equals(o.Key, token, StringComparison.OrdinalIgnoreCase)))
                return Options.First(o => string.Equals(o.Key, token, StringComparison.OrdinalIgnoreCase)).Key;

            return fallback;
        }

        public static string Resolve(CallItem item, string token, string appName)
        {
            if (item == null)
                return string.Empty;

            switch (token)
            {
                case TokenAppName:
                    return appName;

                case TokenTranscription:
                    return item.Transcription ?? string.Empty;

                case TokenTalkgroup:
                    return item.Talkgroup ?? string.Empty;

                case TokenSite:
                    return item.Site ?? string.Empty;

                case TokenReceiver:
                    return item.VoiceReceiver ?? string.Empty;

                default:
                    return string.Empty;
            }
        }
    }
}
