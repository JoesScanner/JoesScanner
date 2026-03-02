using System;
using System.Collections.Generic;

namespace JoesScanner.Services
{
    // A lightweight container for playback preparation results.
    // Phase 3 uses this to carry detected static regions and attenuation settings.
    public sealed record PreparedAudio
    {
        public PreparedAudio(string url)
        {
            Url = url;
        }

        public string Url { get; init; }

        // When we download or cache audio locally, this is the absolute file path.
        // It is optional and may be null when Url already points at a local file or when
        // preparation fails and we fall back to the original Url.
        public string? LocalPath { get; init; }


        // Phase 5 diagnostics (best-effort; used only for logging/tuning)
        public bool PreparedFromCache { get; init; }
        public long PreparationElapsedMs { get; init; }
        public long DownloadElapsedMs { get; init; }
        public long DownloadBytes { get; init; }
        public long ToneDetectionElapsedMs { get; init; }
        public int ToneScanWindowMs { get; init; }


        // Static attenuator (detect and attenuate)
        public bool StaticFilterEnabled { get; init; }
        public int StaticAttenuatorVolume { get; init; }

        // Detected static regions in milliseconds.
        public List<StaticSegment> StaticSegments { get; init; } = new();

        public sealed record StaticSegment(int StartMs, int EndMs);

// Tone filter settings are carried forward for Phase 4.
        public bool ToneFilterEnabled { get; init; }
        public int ToneStrength { get; init; }
        public int ToneSensitivity { get; init; }

        // Phase 4: tone detection results (best-effort).
        public bool ToneDetected { get; init; }
        public int ToneDetectedFrequencyHz { get; init; }

        // Phase 4: windows where tones should be quieted during playback.
        public IReadOnlyList<ToneDuckSegment> ToneDuckSegments { get; init; } = Array.Empty<ToneDuckSegment>();
    }

    public readonly record struct ToneDuckSegment(int StartMs, int EndMs);
}
