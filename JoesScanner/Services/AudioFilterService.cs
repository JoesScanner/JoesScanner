using System.Diagnostics;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Storage;
using NLayer;

namespace JoesScanner.Services
{
    // Phase 2 implementation:
    // - Reads Audio filter settings
    // - If enabled, downloads http/https audio to a local cache file
    // - Returns a file:// URL for platform players (safe for iOS/macOS)
    // - No DSP is applied yet (Phase 3/4)
    public sealed class AudioFilterService : IAudioFilterService
    {
        private readonly ISettingsService _settings;
        private readonly HttpClient _http;

        private const int AnalysisSidecarVersion = 2;

        // Concurrency gates: tone/static analysis is CPU-heavy and should not run many times in parallel.
        // Keep these global to reduce spikes when the queue advances quickly.
        private static readonly SemaphoreSlim UnifiedAnalysisGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim ToneAnalysisGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim StaticAnalysisGate = new SemaphoreSlim(1, 1);

        private sealed class DecodedAudio
        {
            public DecodedAudio(float[] monoSamples, int sampleRate)
            {
                MonoSamples = monoSamples;
                SampleRate = sampleRate;
            }

            public float[] MonoSamples { get; }
            public int SampleRate { get; }
        }

        private static readonly ConcurrentDictionary<string, DecodedAudio> DecodeCache = new();


        public AudioFilterService(ISettingsService settings)
        {
            _settings = settings;
            _http = new HttpClient();
        }

        public async Task<PreparedAudio> PrepareForPlaybackAsync(string audioUrl, CancellationToken cancellationToken = default)
        {
            var prepSw = Stopwatch.StartNew();
            var downloadElapsedMs = 0L;
            var downloadBytes = 0L;
            var preparedFromCache = false;
            var toneDetectionElapsedMs = 0L;
            var toneScanWindowMs = 0;
            if (string.IsNullOrWhiteSpace(audioUrl))
                return new PreparedAudio(audioUrl);

            var staticEnabled = false;
            var toneEnabled = false;
            var staticVolume = 0;
            var strength = 0;
            var sensitivity = 50;

            try
            {
                staticEnabled = _settings.AudioStaticFilterEnabled;
                toneEnabled = _settings.AudioToneFilterEnabled;
                staticVolume = Clamp01To100(_settings.AudioStaticAttenuatorVolume);
                strength = Clamp01To100(_settings.AudioToneStrength);
                sensitivity = Clamp01To100(_settings.AudioToneSensitivity);
            }
            catch
            {
                // If settings are unavailable for any reason, do not interfere with playback.
                return new PreparedAudio(audioUrl);
            }

            if (!staticEnabled && !toneEnabled)
                return new PreparedAudio(audioUrl);

            var prepared = new PreparedAudio(audioUrl)
            {
                StaticFilterEnabled = staticEnabled,
                StaticAttenuatorVolume = staticVolume,
                ToneFilterEnabled = toneEnabled,
                ToneStrength = strength,
                ToneSensitivity = sensitivity,
            };

            // Phase 3 (Static attenuator): detect static regions in PCM and attenuate only those regions.
            // No start/end fallback is applied. If no static is detected, volume is unchanged.

            // If already a local file URL, keep it.
            if (IsFileUrl(audioUrl))
            {
                var localPath = TryGetLocalPathFromUrl(audioUrl);
                var withLocal = prepared with
                {
                    LocalPath = localPath,
                    PreparationElapsedMs = prepSw.ElapsedMilliseconds
                };
                var analysisInfo = await ApplyUnifiedToneAndStaticAnalysisIfEnabledAsync(withLocal, cancellationToken);
                var toned = analysisInfo.prepared;
                toneDetectionElapsedMs = analysisInfo.toneDetectionElapsedMs;
                toneScanWindowMs = analysisInfo.toneScanWindowMs;
                return toned with
                {
                    DownloadElapsedMs = downloadElapsedMs,
                    DownloadBytes = downloadBytes,
                    ToneDetectionElapsedMs = toneDetectionElapsedMs,
                    ToneScanWindowMs = toneScanWindowMs,
                    PreparationElapsedMs = prepSw.ElapsedMilliseconds
                };
            }

            // If it looks like a raw local filesystem path, convert to file:// URL.
            if (LooksLikeLocalPath(audioUrl))
            {
                try
                {
                    var localUri = new Uri(Path.GetFullPath(audioUrl));
                    var withLocal = prepared with
                    {
                        Url = localUri.AbsoluteUri,
                        LocalPath = Path.GetFullPath(audioUrl),
                        PreparationElapsedMs = prepSw.ElapsedMilliseconds
                    };
                    var analysisInfo = await ApplyUnifiedToneAndStaticAnalysisIfEnabledAsync(withLocal, cancellationToken);
                    var toned = analysisInfo.prepared;
                    toneDetectionElapsedMs = analysisInfo.toneDetectionElapsedMs;
                    toneScanWindowMs = analysisInfo.toneScanWindowMs;
return toned with
                    {
                        DownloadElapsedMs = downloadElapsedMs,
                        DownloadBytes = downloadBytes,
                        ToneDetectionElapsedMs = toneDetectionElapsedMs,
                        ToneScanWindowMs = toneScanWindowMs,
                        PreparationElapsedMs = prepSw.ElapsedMilliseconds
                    };
                }
                catch
                {
                    return prepared;
                }
            }

            // Only download for http/https. For content:// (Android) we do not touch it.
            if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri))
                return prepared;

            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return prepared;

            var cacheDir = Path.Combine(FileSystem.AppDataDirectory, "AudioFilterCache");
            Directory.CreateDirectory(cacheDir);

            // Settings hash is included so future DSP outputs do not collide.
            // Phase 2: the "processed" file is just the downloaded file.
            var settingsKey = BuildSettingsKey(staticEnabled, toneEnabled, staticVolume, strength, sensitivity);
            var fileExt = TryGetExtensionFromUrl(uri) ?? ".mp3";

            var cacheName = $"{Sha256Hex(uri.AbsoluteUri)}_{Sha256Hex(settingsKey)}{fileExt}";
            var cachePath = Path.Combine(cacheDir, cacheName);

            if (File.Exists(cachePath))
            {
                try
                {
                    AppLog.Add(() => $"AudioFilter: cache hit. file={Path.GetFileName(cachePath)} prepMs={prepSw.ElapsedMilliseconds}");
                }
                catch { }
                preparedFromCache = true;
                var withLocal = prepared with
                {
                    Url = new Uri(cachePath).AbsoluteUri,
                    LocalPath = cachePath,
                    PreparedFromCache = true,
                    PreparationElapsedMs = prepSw.ElapsedMilliseconds
                };

                var analysisInfo = await ApplyUnifiedToneAndStaticAnalysisIfEnabledAsync(withLocal, cancellationToken);
                var toned = analysisInfo.prepared;
                toneDetectionElapsedMs = analysisInfo.toneDetectionElapsedMs;
                toneScanWindowMs = analysisInfo.toneScanWindowMs;
                return toned with
                {
                    DownloadElapsedMs = downloadElapsedMs,
                    DownloadBytes = downloadBytes,
                    ToneDetectionElapsedMs = toneDetectionElapsedMs,
                    ToneScanWindowMs = toneScanWindowMs,
                    PreparationElapsedMs = prepSw.ElapsedMilliseconds,
                    PreparedFromCache = true
                };
            }

            try
            {
                AppLog.Add(() => $"AudioFilter: preparing local audio. static={staticEnabled} tone={toneEnabled} urlHost={uri.Host}");
            }
            catch { }

            var tmpPath = cachePath + ".tmp";
            try
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }
            catch
            {
            }

            try
            {
                var dlSw = Stopwatch.StartNew();
                using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                resp.EnsureSuccessStatusCode();

                await using (var src = await resp.Content.ReadAsStreamAsync(cancellationToken))
                await using (var dst = File.Create(tmpPath))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await src.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await dst.WriteAsync(buffer, 0, read, cancellationToken);
                        downloadBytes += read;
                    }
                }

                downloadElapsedMs = dlSw.ElapsedMilliseconds;

                try
                {
                    AppLog.Add(() => $"AudioFilter: downloaded bytes={downloadBytes} ms={downloadElapsedMs} file={Path.GetFileName(cacheName)}");
                }
                catch { }

                try
                {
                    if (File.Exists(cachePath))
                        File.Delete(cachePath);
                }
                catch
                {
                }

                File.Move(tmpPath, cachePath);
                preparedFromCache = true;
                var withLocal = prepared with
                {
                    Url = new Uri(cachePath).AbsoluteUri,
                    LocalPath = cachePath,
                    PreparedFromCache = true,
                    PreparationElapsedMs = prepSw.ElapsedMilliseconds
                };

                var analysisInfo = await ApplyUnifiedToneAndStaticAnalysisIfEnabledAsync(withLocal, cancellationToken);
                var toned = analysisInfo.prepared;
                toneDetectionElapsedMs = analysisInfo.toneDetectionElapsedMs;
                toneScanWindowMs = analysisInfo.toneScanWindowMs;
                return toned with
                {
                    DownloadElapsedMs = downloadElapsedMs,
                    DownloadBytes = downloadBytes,
                    ToneDetectionElapsedMs = toneDetectionElapsedMs,
                    ToneScanWindowMs = toneScanWindowMs,
                    PreparationElapsedMs = prepSw.ElapsedMilliseconds,
                    PreparedFromCache = true
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    AppLog.Add(() => $"AudioFilter: preparation failed, using original audio. {ex.Message}");
                }
                catch
                {
                }

                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                return prepared;
            }
        }


private async Task<(PreparedAudio prepared, long toneDetectionElapsedMs, int toneScanWindowMs)> ApplyUnifiedToneAndStaticAnalysisIfEnabledAsync(PreparedAudio prepared, CancellationToken cancellationToken)
{
    long toneDetectionElapsedMs = 0;
    int toneScanWindowMs = 0;

    var toneEnabled = prepared.ToneFilterEnabled;
    var staticEnabled = prepared.StaticFilterEnabled && prepared.StaticAttenuatorVolume > 0;

    if (!toneEnabled && !staticEnabled)
        return (prepared, toneDetectionElapsedMs, toneScanWindowMs);

    if (string.IsNullOrWhiteSpace(prepared.LocalPath))
        return (prepared, toneDetectionElapsedMs, toneScanWindowMs);

    var localPath = prepared.LocalPath;
    if (!localPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        return (prepared, toneDetectionElapsedMs, toneScanWindowMs);

    // If this is a cached file and we already analyzed it, reuse the analysis results.
    if (IsInAudioFilterCache(localPath))
    {
        var sidecar = TryLoadSidecar(localPath);

        if (sidecar != null)
        {
            if (toneEnabled && sidecar.ToneDataPresent)
            {
                toneScanWindowMs = sidecar.ToneAnalyzedMs;

                if (sidecar.ToneDetected)
                {
                    prepared = prepared with
                    {
                        ToneDetected = true,
                        ToneDetectedFrequencyHz = sidecar.ToneFrequencyHz,
                        ToneDuckSegments = new List<ToneDuckSegment>
                        {
                            new ToneDuckSegment(0, Math.Max(0, sidecar.ToneDetectedDurationMs))
                        }
                    };
                }
            }

            if (staticEnabled && sidecar.StaticDataPresent)
            {
                var segments = new List<PreparedAudio.StaticSegment>();
                foreach (var dto in sidecar.StaticSegments ?? new List<AudioAnalysisSidecar.SegmentDto>())
                {
                    segments.Add(new PreparedAudio.StaticSegment(dto.StartMs, dto.EndMs));
                }

                prepared = prepared with { StaticSegments = segments };
            }

            if ((!toneEnabled || sidecar.ToneDataPresent) && (!staticEnabled || sidecar.StaticDataPresent))
                return (prepared, toneDetectionElapsedMs, toneScanWindowMs);
        }
    }

    try
    {
        await UnifiedAnalysisGate.WaitAsync(cancellationToken);
        try
        {
            // Decode once and share through DecodeCache for this analysis run.
            var decoded = DecodeMp3ToMono(localPath, cancellationToken, 120);
            DecodeCache[localPath] = decoded;

            try
            {
                var sidecar = new AudioAnalysisSidecar();

                if (toneEnabled)
                {
                    var shortAnalyzeSeconds = 8;
                    var extendedAnalyzeSeconds = 15;

                    var sw = Stopwatch.StartNew();
                    var toneResult = await Task.Run(() => DetectTonesInMp3(localPath, prepared.ToneSensitivity, cancellationToken, shortAnalyzeSeconds), cancellationToken);
                    toneDetectionElapsedMs = sw.ElapsedMilliseconds;
                    toneScanWindowMs = toneResult.analyzedMs;

                    if (!toneResult.detected && toneResult.hintExtendScan)
                    {
                        sw.Restart();
                        toneResult = await Task.Run(() => DetectTonesInMp3(localPath, prepared.ToneSensitivity, cancellationToken, extendedAnalyzeSeconds), cancellationToken);
                        toneDetectionElapsedMs = sw.ElapsedMilliseconds;
                        toneScanWindowMs = toneResult.analyzedMs;
                    }

                    if (!toneResult.detected)
                    {
                        AppLog.Add(() => $"ToneDetect: no tone detected. Sens={prepared.ToneSensitivity} analyzedMs={toneResult.analyzedMs}");
                    }

                    sidecar.ToneDataPresent = true;
                    sidecar.ToneDetected = toneResult.detected;
                    sidecar.ToneFrequencyHz = toneResult.frequencyHz;
                    sidecar.ToneDetectedDurationMs = toneResult.detectedDurationMs;
                    sidecar.ToneAnalyzedMs = toneResult.analyzedMs;

                    if (toneResult.detected)
                    {
                        prepared = prepared with
                        {
                            ToneDetected = true,
                            ToneDetectedFrequencyHz = toneResult.frequencyHz,
                            ToneDuckSegments = new List<ToneDuckSegment>
                            {
                                new ToneDuckSegment(0, Math.Max(0, toneResult.detectedDurationMs))
                            }
                        };
                    }
                }

                if (staticEnabled)
                {
                    var staticSegments = await Task.Run(() => DetectStaticSegmentsInMp3(localPath, prepared.StaticAttenuatorVolume, cancellationToken), cancellationToken);

                    sidecar.StaticDataPresent = true;
                    sidecar.StaticSegments = new List<AudioAnalysisSidecar.SegmentDto>();
                    foreach (var seg in staticSegments)
                    {
                        sidecar.StaticSegments.Add(new AudioAnalysisSidecar.SegmentDto { StartMs = seg.StartMs, EndMs = seg.EndMs });
                    }

                    prepared = prepared with { StaticSegments = staticSegments };
                }

                // Only write sidecar for cache files.
                if (IsInAudioFilterCache(localPath))
                {
                    TrySaveSidecar(localPath, sidecar);
                }
            }
            finally
            {
                DecodeCache.TryRemove(localPath, out _);
            }
        }
        finally
        {
            UnifiedAnalysisGate.Release();
        }
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch
    {
        // Never block playback on analysis.
        return (prepared, toneDetectionElapsedMs, toneScanWindowMs);
    }

    return (prepared, toneDetectionElapsedMs, toneScanWindowMs);
}

        private async Task<(PreparedAudio prepared, long toneDetectionElapsedMs, int toneScanWindowMs)> ApplyToneDetectionIfEnabledAsync(PreparedAudio prepared, CancellationToken cancellationToken)
        {
            long toneDetectionElapsedMs = 0;
            int toneScanWindowMs = 0;

            if (!prepared.ToneFilterEnabled)
                return (prepared, toneDetectionElapsedMs, toneScanWindowMs);

            if (string.IsNullOrWhiteSpace(prepared.LocalPath))
                return (prepared, toneDetectionElapsedMs, toneScanWindowMs);

            // Best-effort tone detection: only runs on MP3 files we can decode in managed code.
            var localPath = prepared.LocalPath;
            if (!localPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                return (prepared, toneDetectionElapsedMs, toneScanWindowMs);

            // If this is a cached file and we already analyzed it, reuse the analysis results.
            if (IsInAudioFilterCache(localPath))
            {
                var sidecar = TryLoadSidecar(localPath);
                if (sidecar != null && sidecar.ToneDataPresent)
                {
                    toneScanWindowMs = sidecar.ToneAnalyzedMs;

                    if (!sidecar.ToneDetected)
                        return (prepared, toneDetectionElapsedMs, toneScanWindowMs);

                    var duck = new List<ToneDuckSegment>
                    {
                        new ToneDuckSegment(0, Math.Max(0, sidecar.ToneDetectedDurationMs))
                    };

                    return (prepared with
                    {
                        ToneDetected = true,
                        ToneDetectedFrequencyHz = sidecar.ToneFrequencyHz,
                        ToneDuckSegments = duck
                    }, toneDetectionElapsedMs, toneScanWindowMs);
                }
            }

            try
            {
                var shortAnalyzeSeconds = 8;
                var extendedAnalyzeSeconds = 15;

                await ToneAnalysisGate.WaitAsync(cancellationToken);
                try
                {
                    var toneSw = Stopwatch.StartNew();
                var result = await Task.Run(() => DetectTonesInMp3(localPath, prepared.ToneSensitivity, cancellationToken, shortAnalyzeSeconds), cancellationToken);
                toneDetectionElapsedMs = toneSw.ElapsedMilliseconds;
                toneScanWindowMs = result.analyzedMs;

                if (!result.detected && result.hintExtendScan)
                {
                    // If tones begin right at the end of the initial scan window, re-run with a longer window.
                    toneSw.Restart();
                    result = await Task.Run(() => DetectTonesInMp3(localPath, prepared.ToneSensitivity, cancellationToken, extendedAnalyzeSeconds), cancellationToken);
                    toneDetectionElapsedMs = toneSw.ElapsedMilliseconds;
                    toneScanWindowMs = result.analyzedMs;
                }

                if (!result.detected)
                {
                    if (IsInAudioFilterCache(localPath))
                    {
                        var model = TryLoadSidecar(localPath) ?? new AudioAnalysisSidecar();
                        model.ToneDataPresent = true;
                        model.ToneDetected = false;
                        model.ToneFrequencyHz = 0;
                        model.ToneDetectedDurationMs = 0;
                        model.ToneAnalyzedMs = result.analyzedMs;
                        TrySaveSidecar(localPath, model);
                    }

                    return (prepared, toneDetectionElapsedMs, toneScanWindowMs);
                }

                var duck = new List<ToneDuckSegment>
                {
                    new ToneDuckSegment(0, result.detectedDurationMs)
                };

                try
                {
                    AppLog.Add(() => $"AudioTone: detected freq={result.frequencyHz}Hz duration={result.detectedDurationMs}ms strength={prepared.ToneStrength} toneMs={toneDetectionElapsedMs}");
                }
                catch { }

                if (IsInAudioFilterCache(localPath))
                {
                    var model = TryLoadSidecar(localPath) ?? new AudioAnalysisSidecar();
                    model.ToneDataPresent = true;
                    model.ToneDetected = true;
                    model.ToneFrequencyHz = result.frequencyHz;
                    model.ToneDetectedDurationMs = result.detectedDurationMs;
                    model.ToneAnalyzedMs = result.analyzedMs;
                    TrySaveSidecar(localPath, model);
                }

                return (prepared with
                {
                    ToneDetected = true,
                    ToneDetectedFrequencyHz = result.frequencyHz,
                    ToneDuckSegments = duck
                }, toneDetectionElapsedMs, toneScanWindowMs);
                }
                finally
                {
                    try { ToneAnalysisGate.Release(); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Never block playback on tone detection.
                return (prepared, toneDetectionElapsedMs, toneScanWindowMs);
            }
        }

        

private sealed class AudioAnalysisSidecar
{
    public int Version { get; set; } = AnalysisSidecarVersion;

    // Tone
    public bool ToneDataPresent { get; set; }
    public bool ToneDetected { get; set; }
    public int ToneFrequencyHz { get; set; }
    public int ToneDetectedDurationMs { get; set; }
    public int ToneAnalyzedMs { get; set; }

    // Static
    public bool StaticDataPresent { get; set; }
    public List<SegmentDto> StaticSegments { get; set; } = new();

    public sealed class SegmentDto
    {
        public int StartMs { get; set; }
        public int EndMs { get; set; }
    }
}

private static string GetSidecarPath(string localPath) => localPath + ".analysis.json";

private static AudioAnalysisSidecar? TryLoadSidecar(string localPath)
{
    try
    {
        var p = GetSidecarPath(localPath);
        if (!File.Exists(p))
            return null;

        var json = File.ReadAllText(p);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var model = JsonSerializer.Deserialize<AudioAnalysisSidecar>(json);
        if (model == null || model.Version != AnalysisSidecarVersion)
            return null;

        return model;
    }
    catch
    {
        return null;
    }
}

private static void TrySaveSidecar(string localPath, AudioAnalysisSidecar model)
{
    try
    {
        model.Version = AnalysisSidecarVersion;

        var p = GetSidecarPath(localPath);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        File.WriteAllText(p, json);
    }
    catch
    {
    }
}

private static bool IsInAudioFilterCache(string localPath)
{
    try
    {
        var cacheDir = Path.Combine(FileSystem.AppDataDirectory, "AudioFilterCache");
        var full = Path.GetFullPath(localPath);
        var fullCache = Path.GetFullPath(cacheDir);

        return full.StartsWith(fullCache, StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}


        private enum ToneWindowKind
        {
            None = 0,
            SingleTone = 1,
            DualTone = 2
        }

        private readonly struct ToneWindowInfo
        {
            public ToneWindowInfo(bool isTone, ToneWindowKind kind, int dominantFreqHz, int secondFreqHz, double rms, double ratio, double peakiness)
            {
                IsTone = isTone;
                Kind = kind;
                DominantFreqHz = dominantFreqHz;
                SecondFreqHz = secondFreqHz;
                Rms = rms;
                Ratio = ratio;
                Peakiness = peakiness;
            }

            public bool IsTone { get; }
            public ToneWindowKind Kind { get; }
            public int DominantFreqHz { get; }
            public int SecondFreqHz { get; }
            public double Rms { get; }
            public double Ratio { get; }
            public double Peakiness { get; }
        }


        private async Task<(PreparedAudio prepared, long staticDetectionElapsedMs)> ApplyStaticDetectionIfEnabledAsync(PreparedAudio prepared, CancellationToken cancellationToken)
        {
            long staticDetectionElapsedMs = 0;

            if (!prepared.StaticFilterEnabled)
                return (prepared, staticDetectionElapsedMs);

            if (prepared.StaticAttenuatorVolume <= 0)
                return (prepared, staticDetectionElapsedMs);

            if (string.IsNullOrWhiteSpace(prepared.LocalPath))
                return (prepared, staticDetectionElapsedMs);

            var localPath = prepared.LocalPath;
            if (!localPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                return (prepared, staticDetectionElapsedMs);

            // If this is a cached file and we already analyzed it, reuse the analysis results.
            if (IsInAudioFilterCache(localPath))
            {
                var sidecar = TryLoadSidecar(localPath);
                if (sidecar != null && sidecar.StaticDataPresent)
                {
                    var segs = new List<PreparedAudio.StaticSegment>();
                    if (sidecar.StaticSegments != null)
                    {
                        for (var i = 0; i < sidecar.StaticSegments.Count; i++)
                        {
                            segs.Add(new PreparedAudio.StaticSegment(sidecar.StaticSegments[i].StartMs, sidecar.StaticSegments[i].EndMs));
                        }
                    }

                    var cached = prepared with { StaticSegments = segs };
                    return (cached, staticDetectionElapsedMs);
                }
            }

            try
            {
                await StaticAnalysisGate.WaitAsync(cancellationToken);
                try
                {
                    var sw = Stopwatch.StartNew();
                    var segments = await Task.Run(() => DetectStaticSegmentsInMp3(localPath, prepared.StaticAttenuatorVolume, cancellationToken), cancellationToken);
                staticDetectionElapsedMs = sw.ElapsedMilliseconds;
                    if (IsInAudioFilterCache(localPath))
                    {
                        var model = TryLoadSidecar(localPath) ?? new AudioAnalysisSidecar();
                        model.StaticDataPresent = true;
                        model.StaticSegments = segments.Select(s => new AudioAnalysisSidecar.SegmentDto { StartMs = s.StartMs, EndMs = s.EndMs }).ToList();
                        TrySaveSidecar(localPath, model);
                    }

                    var updated = prepared with { StaticSegments = segments };
                    LogStaticDetectionSummary(updated, staticDetectionElapsedMs);
                    return (updated, staticDetectionElapsedMs);
                }
                finally
                {
                    try { StaticAnalysisGate.Release(); } catch { }
                }
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"AudioStatic: detection failed: {ex.Message}");
                return (prepared, staticDetectionElapsedMs);
            }
        }

        private static List<PreparedAudio.StaticSegment> DetectStaticSegmentsInMp3(string mp3Path, int staticVolume0to100, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

var decoded = GetDecodedAudio(mp3Path, cancellationToken, 120);

var sampleRate = decoded.SampleRate;
var monoSamples = decoded.MonoSamples;

if (monoSamples.Length <= sampleRate / 10)
    return new List<PreparedAudio.StaticSegment>();

var analyzedMs = (int)Math.Round((monoSamples.Length / (double)Math.Max(1, sampleRate)) * 1000.0);

var frameMs = 20;
    var frameSamples = Math.Max(1, (int)Math.Round(sampleRate * (frameMs / 1000.0)));
    var frameCount = monoSamples.Length / frameSamples;
    if (frameCount <= 0)
        return new List<PreparedAudio.StaticSegment>();

    var v01 = Clamp01To100(staticVolume0to100) / 100.0;

    // Edge windows to analyze and potentially attenuate.
    var edgeAnalyzeMs = 1500;
    var maxEdgeAttenuateMs = (int)Math.Round(Lerp(700, 1300, v01)); // user control mildly widens allowed edge work
    var edgeAnalyzeFrames = Math.Min(frameCount, (edgeAnalyzeMs / frameMs) + 2);

    // Basic gates.
    var silenceRms = 0.0010;     // below this, treat as silence and ignore for evidence
    var minContentRms = 0.0045;  // minimum RMS to consider tone or speech present
    var minNoiseRms = 0.0030;    // minimum RMS to consider static/noise present

    // Feature thresholds.
    var speechZcrCeil = 0.18;
    var speechHpCeil = 0.65;
    var speechLpFloor = 0.62;

    var noiseZcrFloor = 0.22;
    var noiseHpFloor = 0.70;

    // Tone detection (for edge boundary protection).
    // We only need to avoid treating tone bursts as "static", not fully identify tones.
    var toneZcrCeil = 0.16;
    var toneHpCeil = 0.60;
    var toneCorrFloor = 0.55; // normalized autocorrelation peak

    static double MeanAbs(float[] s, int start, int n)
    {
        var sum = 0.0;
        for (var i = 0; i < n; i++)
            sum += Math.Abs(s[start + i]);
        return sum / Math.Max(1, n);
    }

    static double MeanAbsDiff(float[] s, int start, int n)
    {
        if (n <= 1) return 0.0;
        var sum = 0.0;
        var prev = s[start];
        for (var i = 1; i < n; i++)
        {
            var x = s[start + i];
            sum += Math.Abs(x - prev);
            prev = x;
        }
        return sum / Math.Max(1, (n - 1));
    }

    static double ComputeZcr(float[] s, int start, int n)
    {
        if (n <= 1) return 0.0;
        var zc = 0;
        var prev = s[start];
        for (var i = 1; i < n; i++)
        {
            var x = s[start + i];
            if ((x >= 0 && prev < 0) || (x < 0 && prev >= 0))
                zc++;
            prev = x;
        }
        return zc / (double)n;
    }

    static double ComputeRms(float[] s, int start, int n)
    {
        var sumSq = 0.0;
        for (var i = 0; i < n; i++)
        {
            var x = s[start + i];
            sumSq += x * x;
        }
        return Math.Sqrt(sumSq / Math.Max(1, n));
    }

    static double ComputeLpRatio(float[] s, int start, int n)
    {
        // 1st-order IIR low-pass then compare mean abs LP vs mean abs raw.
        var meanAbs = 0.0;
        for (var i = 0; i < n; i++)
            meanAbs += Math.Abs(s[start + i]);
        meanAbs /= Math.Max(1, n);

        if (meanAbs <= 1e-12)
            return 0.0;

        var lp = 0.0;
        var sumAbsLp = 0.0;

        // alpha chosen to behave consistently across typical scanner sample rates at 20ms frames.
        var alpha = 0.18;
        for (var i = 0; i < n; i++)
        {
            lp += alpha * (s[start + i] - lp);
            sumAbsLp += Math.Abs(lp);
        }
        var meanAbsLp = sumAbsLp / Math.Max(1, n);
        return meanAbsLp / (meanAbs + 1e-12);
    }

    static double ComputeNormAutoCorrPeak(float[] s, int start, int n, int sampleRate)
    {
        // Normalized autocorrelation peak over a pitch/tone lag range.
        // We keep this inexpensive: short range only, and we early-exit on low energy.
        if (n < 32) return 0.0;

        // Typical tone-outs are often 300 to 2000 Hz. Convert to lag range.
        var maxFreq = 2000.0;
        var minFreq = 300.0;
        var minLag = (int)Math.Round(sampleRate / maxFreq);
        var maxLag = (int)Math.Round(sampleRate / minFreq);

        minLag = Math.Max(4, minLag);
        maxLag = Math.Min(n - 4, Math.Max(minLag + 1, maxLag));
        if (maxLag <= minLag) return 0.0;

        // Energy for normalization.
        var energy = 0.0;
        for (var i = 0; i < n; i++)
        {
            var x = (double)s[start + i];
            energy += x * x;
        }
        if (energy <= 1e-9) return 0.0;

        var best = 0.0;

        // Sample a subset of lags to reduce CPU.
        // Step increases with lag range.
        var span = maxLag - minLag;
        var step = span > 200 ? 3 : (span > 120 ? 2 : 1);

        for (var lag = minLag; lag <= maxLag; lag += step)
        {
            var sum = 0.0;
            for (var i = lag; i < n; i++)
                sum += (double)s[start + i] * s[start + i - lag];

            var corr = sum / (energy + 1e-9);
            if (corr > best) best = corr;
        }

        return Clamp01(best);
    }

    bool IsContentFrame(double rms, double zcr, double hpRatio, double lpRatio, double corrPeak)
    {
        // Tone: tonal, periodic, and not too "hashy".
        if (rms >= minContentRms &&
            zcr <= toneZcrCeil &&
            hpRatio <= toneHpCeil &&
            corrPeak >= toneCorrFloor)
            return true;

        // Speech-like: lower ZCR, lower HF proxy, and higher LP ratio.
        if (rms >= minContentRms &&
            zcr <= speechZcrCeil &&
            hpRatio <= speechHpCeil &&
            lpRatio >= speechLpFloor)
            return true;

        return false;
    }

    bool IsNoiseEvidence(double rms, double zcr, double hpRatio)
    {
        // Evidence of squelch/static: broadband-ish, higher ZCR and/or higher HF proxy.
        if (rms < minNoiseRms)
            return false;

        if (zcr >= noiseZcrFloor)
            return true;

        if (hpRatio >= noiseHpFloor)
            return true;

        return false;
    }

    // Scan for Ts (content start) in the first edge window.
    var tsMs = 0;
    {
        var contentFlags = new bool[edgeAnalyzeFrames];
        for (var f = 0; f < edgeAnalyzeFrames; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var start = f * frameSamples;
            var end = Math.Min(monoSamples.Length, start + frameSamples);
            var n = Math.Max(1, end - start);

            var rms = ComputeRms(monoSamples, start, n);
            if (rms < silenceRms)
            {
                contentFlags[f] = false;
                continue;
            }

            var zcr = ComputeZcr(monoSamples, start, n);
            var meanAbs = MeanAbs(monoSamples, start, n);
            var meanAbsDiff = MeanAbsDiff(monoSamples, start, n);
            var hpRatio = meanAbsDiff / (meanAbs + 1e-12);
            var lpRatio = ComputeLpRatio(monoSamples, start, n);
            var corr = ComputeNormAutoCorrPeak(monoSamples, start, n, sampleRate);

            contentFlags[f] = IsContentFrame(rms, zcr, hpRatio, lpRatio, corr);
        }

        // Debounce: require K of last N frames to be content.
        var nWindow = 6;
        var kNeeded = 4;

        for (var f = 0; f < edgeAnalyzeFrames; f++)
        {
            var count = 0;
            var a = Math.Max(0, f - (nWindow - 1));
            for (var j = a; j <= f; j++)
            {
                if (contentFlags[j]) count++;
            }

            if (count >= kNeeded)
            {
                tsMs = Math.Max(0, (f * frameMs) - 60); // pad earlier for safety
                break;
            }
        }

        // If we never see content, keep Ts at 0 (no start edge attenuation by default).
    }

    // Scan for Te (content end) in the last edge window.
    var teMs = analyzedMs;
    {
        var contentFlags = new bool[edgeAnalyzeFrames];
        for (var idx = 0; idx < edgeAnalyzeFrames; idx++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var f = (frameCount - edgeAnalyzeFrames) + idx;
            if (f < 0) continue;

            var start = f * frameSamples;
            var end = Math.Min(monoSamples.Length, start + frameSamples);
            var n = Math.Max(1, end - start);

            var rms = ComputeRms(monoSamples, start, n);
            if (rms < silenceRms)
            {
                contentFlags[idx] = false;
                continue;
            }

            var zcr = ComputeZcr(monoSamples, start, n);
            var meanAbs = MeanAbs(monoSamples, start, n);
            var meanAbsDiff = MeanAbsDiff(monoSamples, start, n);
            var hpRatio = meanAbsDiff / (meanAbs + 1e-12);
            var lpRatio = ComputeLpRatio(monoSamples, start, n);
            var corr = ComputeNormAutoCorrPeak(monoSamples, start, n, sampleRate);

            contentFlags[idx] = IsContentFrame(rms, zcr, hpRatio, lpRatio, corr);
        }

        var nWindow = 6;
        var kNeeded = 4;

        // Walk backwards to find last stable content.
        var lastContentFrameIndex = -1;
        for (var idx = edgeAnalyzeFrames - 1; idx >= 0; idx--)
        {
            var count = 0;
            var b = Math.Min(edgeAnalyzeFrames - 1, idx + (nWindow - 1));
            for (var j = idx; j <= b; j++)
            {
                if (contentFlags[j]) count++;
            }

            if (count >= kNeeded)
            {
                lastContentFrameIndex = idx;
                break;
            }
        }

        if (lastContentFrameIndex >= 0)
        {
            var f = (frameCount - edgeAnalyzeFrames) + lastContentFrameIndex;
            var t = f * frameMs;
            teMs = Math.Min(analyzedMs, t + 60); // pad later for safety
        }
        else
        {
            teMs = analyzedMs;
        }
    }

    // Guard against weirdness on very short files.
    tsMs = Math.Clamp(tsMs, 0, analyzedMs);
    teMs = Math.Clamp(teMs, 0, analyzedMs);
    if (teMs < tsMs)
    {
        // If boundaries cross, disable attenuation.
        tsMs = 0;
        teMs = analyzedMs;
    }

    // Hard cap edge attenuation length, even if Ts/Te allow more.
    tsMs = Math.Min(tsMs, maxEdgeAttenuateMs);
    teMs = Math.Max(teMs, analyzedMs - maxEdgeAttenuateMs);

    // Evidence scan: only create edge segments if there is noise evidence inside that edge region.
    bool HasNoiseEvidenceInRangeMs(int startMs, int endMs)
    {
        if (endMs <= startMs + 20)
            return false;

        var startFrame = Math.Clamp(startMs / frameMs, 0, frameCount - 1);
        var endFrame = Math.Clamp(endMs / frameMs, 0, frameCount - 1);

        var evidenceFrames = 0;
        var checkedFrames = 0;

        for (var f = startFrame; f <= endFrame; f++)
        {
            var start = f * frameSamples;
            var end = Math.Min(monoSamples.Length, start + frameSamples);
            var n = Math.Max(1, end - start);

            var rms = ComputeRms(monoSamples, start, n);
            if (rms < silenceRms)
                continue;

            var zcr = ComputeZcr(monoSamples, start, n);
            var meanAbs = MeanAbs(monoSamples, start, n);
            var meanAbsDiff = MeanAbsDiff(monoSamples, start, n);
            var hpRatio = meanAbsDiff / (meanAbs + 1e-12);

            checkedFrames++;
            if (IsNoiseEvidence(rms, zcr, hpRatio))
                evidenceFrames++;
        }

        // Require some evidence but keep it permissive.
        if (checkedFrames <= 0)
            return false;

        return evidenceFrames >= Math.Max(1, checkedFrames / 6);
    }

    var segments = new List<PreparedAudio.StaticSegment>(2);

    // Start edge segment.
    if (tsMs >= 80 && HasNoiseEvidenceInRangeMs(0, tsMs))
    {
        segments.Add(new PreparedAudio.StaticSegment(0, tsMs));
    }

    // End edge segment.
    if ((analyzedMs - teMs) >= 80 && HasNoiseEvidenceInRangeMs(teMs, analyzedMs))
    {
        segments.Add(new PreparedAudio.StaticSegment(teMs, analyzedMs));
    }

    // Logging summary for tuning.
    AppLog.Add(() =>
        $"AudioStatic: opt4 file={Path.GetFileName(mp3Path)} vol={staticVolume0to100} lenMs={analyzedMs} " +
        $"Ts={tsMs} Te={teMs} segs={segments.Count} edgeCap={maxEdgeAttenuateMs}");

    return segments;
}

        private static List<PreparedAudio.StaticSegment> MergeStaticSegments(List<PreparedAudio.StaticSegment> segments, int mergeGapMs)
        {
            if (segments.Count <= 1)
                return segments;

            segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

            var merged = new List<PreparedAudio.StaticSegment>();
            var cur = segments[0];

            for (var i = 1; i < segments.Count; i++)
            {
                var next = segments[i];
                if (next.StartMs <= cur.EndMs + mergeGapMs)
                {
                    cur = cur with { EndMs = Math.Max(cur.EndMs, next.EndMs) };
                }
                else
                {
                    merged.Add(cur);
                    cur = next;
                }
            }

            merged.Add(cur);
            return merged;
        }

        private static void LogStaticDetectionSummary(PreparedAudio prepared, long elapsedMs)
        {
            try
            {
                if (!prepared.StaticFilterEnabled || prepared.StaticAttenuatorVolume <= 0)
                    return;

                var count = prepared.StaticSegments?.Count ?? 0;
                if (count <= 0)
                {
                    AppLog.Add(() => $"AudioStatic: segments=0 file={Path.GetFileName(prepared.LocalPath)} vol={prepared.StaticAttenuatorVolume} elapsedMs={elapsedMs}");
                    return;
                }

                var first = prepared.StaticSegments[0];
                var last = prepared.StaticSegments[count - 1];
                AppLog.Add(() => $"AudioStatic: segments={count} file={Path.GetFileName(prepared.LocalPath)} first={first.StartMs}-{first.EndMs} last={last.StartMs}-{last.EndMs} vol={prepared.StaticAttenuatorVolume} elapsedMs={elapsedMs}");
            }
            catch { }
        }


private static DecodedAudio DecodeMp3ToMono(string mp3Path, CancellationToken cancellationToken, int maxAnalyzeSeconds)
{
    cancellationToken.ThrowIfCancellationRequested();

    using var fs = new FileStream(mp3Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var mpeg = new MpegFile(fs);

    var sampleRate = mpeg.SampleRate;
    var channels = Math.Max(1, mpeg.Channels);

    var maxSamples = Math.Max(0, sampleRate * channels * Math.Max(1, maxAnalyzeSeconds));
    var buffer = new float[maxSamples];

    var samplesRead = 0;
    while (samplesRead < maxSamples)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var n = mpeg.ReadSamples(buffer, samplesRead, maxSamples - samplesRead);
        if (n <= 0)
            break;
        samplesRead += n;
    }

    if (samplesRead <= 0 || sampleRate <= 0)
        return new DecodedAudio(Array.Empty<float>(), sampleRate);

    var monoCount = samplesRead / channels;
    if (monoCount <= 0)
        return new DecodedAudio(Array.Empty<float>(), sampleRate);

    var mono = new float[monoCount];

    if (channels == 1)
    {
        Array.Copy(buffer, mono, monoCount);
    }
    else
    {
        var idx = 0;
        for (var i = 0; i < monoCount; i++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
                sum += buffer[idx++];

            mono[i] = (float)(sum / channels);
        }
    }

    return new DecodedAudio(mono, sampleRate);
}

private static DecodedAudio GetDecodedAudio(string mp3Path, CancellationToken cancellationToken, int maxAnalyzeSeconds)
{
    if (DecodeCache.TryGetValue(mp3Path, out var cached))
        return cached;

    return DecodeMp3ToMono(mp3Path, cancellationToken, maxAnalyzeSeconds);
}

        private static (bool detected, int frequencyHz, int detectedDurationMs, bool hintExtendScan, int analyzedMs) DetectTonesInMp3(string mp3Path, int toneSensitivity, CancellationToken cancellationToken, int analyzeSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();

var decoded = GetDecodedAudio(mp3Path, cancellationToken, analyzeSeconds);

var sampleRate = decoded.SampleRate;
var monoSamples = decoded.MonoSamples;

// Bound analysis to the requested number of seconds.
var maxMono = Math.Max(0, sampleRate * Math.Max(1, analyzeSeconds));
if (monoSamples.Length > maxMono && maxMono > 0)
{
    var trimmed = new float[maxMono];
    Array.Copy(monoSamples, trimmed, maxMono);
    monoSamples = trimmed;
}

var analyzedMs = (int)Math.Round((monoSamples.Length / (double)Math.Max(1, sampleRate)) * 1000.0);

if (monoSamples.Length <= sampleRate / 2)
    return (false, 0, 0, false, analyzedMs);

// Windowed narrowband dominance check.
            // 100ms windows.
            // A sustained tone that lasts longer than about 1 second should be treated as an alert.
            // This includes multi-level tones where the dominant frequency may shift while the
            // audio remains clearly tone-like.
            var windowMs = 100;

            // Tone sensitivity slider drives how easily we classify a window as tone-like.
            // Higher = more sensitive (lower thresholds, shorter sustain requirement).
            var s = Clamp01To100(toneSensitivity) / 100.0;

            // Single-tone window thresholds (Goertzel scan).
            // Lower ratios and peakiness tolerate noisier over-the-air recordings and low bitrate MP3.
            var ratioThreshold = Lerp(0.65, 0.35, s);
            var peakinessThreshold = Lerp(3.0, 1.6, s);

            // Quiet window guard: keep some protection against codec artifacts.
            var rmsThreshold = Lerp(0.030, 0.015, s);

            // Make RMS threshold adaptive to the overall call level. Some MP3s are quiet even when tones are obvious.
            // We never want a fixed absolute threshold to be the reason tones are missed.
            var overallRms = ComputeOverallRms(monoSamples);
            rmsThreshold = Math.Max(rmsThreshold, overallRms * 0.12);

            // Sustained tone requirement. Higher sensitivity allows shorter tones to qualify.
            var minSustainedMs = (int)Math.Round(Lerp(1400, 600, s));
            var windowSize = (int)Math.Round(sampleRate * (windowMs / 1000.0));
            if (windowSize < 128)
                windowSize = 128;

            var stepSize = windowSize;
            var neededConsecutive = (int)Math.Ceiling(minSustainedMs / (double)windowMs);

            var freqs = BuildToneFrequencyList();

            // Pre-scan all windows so we can also detect "repeating short tones" transmissions.
            // Some agencies will send periodic short beeps to keep a channel clear for emergency traffic.
            // We want to detect those *only* when the call is essentially tone-only (no sustained speech),
            // and explicitly ignore the single courtesy beeps that often appear at the beginning/end of
            var windows = new List<ToneWindowInfo>(monoSamples.Length / windowSize);
            for (var start = 0; start + windowSize <= monoSamples.Length; start += stepSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var w = AnalyzeToneWindow(monoSamples, start, windowSize, sampleRate, freqs, ratioThreshold, peakinessThreshold, rmsThreshold);
                windows.Add(w);
            }

            
            // Early exclusions: avoid treating common digital signaling bursts as paging tones.
            // These are often short AFSK bursts (MDC1200-style) that can appear in voice traffic.
            if (LooksLikeAfskSignalingBurst(windows, windowMs))
                return (false, 0, 0, false, analyzedMs);

            // Two-tone sequential paging (common for fire/EMS) usually appears at the start of a dispatch.
            // Detect the A then B pattern and duck through the end of the B tone.
            var twoTone = DetectTwoToneSequentialPaging(windows, windowMs);
            if (twoTone.detected)
                return (true, twoTone.frequencyHz, twoTone.detectedDurationMs, false, analyzedMs);

// 1) Long/sustained tone detection ("the long tone")
            // We detect two variants:
            // - Stable-frequency sustained tones
            // - Multi-level sustained tones where the dominant frequency shifts
            var consecutiveStable = 0;
            var currentFreq = 0;
            var bestFreq = 0;
            var bestConsecutiveStable = 0;

            var consecutiveAnyTone = 0;
            var anyToneFreqs = new List<int>(32);

            // Some recordings have brief dropouts (codec artifacts or staticVolume tails) inside an otherwise
            // clean tone. Allow a small number of non-tone windows without breaking a sustained run.
            const int nonToneGapAllowance = 1;
            var nonToneGap = 0;

            foreach (var w in windows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (w.IsTone)
                {
                    nonToneGap = 0;
                    consecutiveAnyTone++;
                    if (w.DominantFreqHz != 0)
                        anyToneFreqs.Add(w.DominantFreqHz);

                    if (consecutiveAnyTone >= neededConsecutive)
                    {
                        var durationMs = consecutiveAnyTone * windowMs;
                        if (durationMs < minSustainedMs)
                            durationMs = minSustainedMs;
                        if (durationMs > analyzedMs)
                            durationMs = analyzedMs;
                        var freq = ModeOrZero(anyToneFreqs);
                        return (true, freq, durationMs, false, analyzedMs);
                    }

                    if (w.DominantFreqHz != 0)
                    {
                        // Stable-frequency sustained tone.
                        if (currentFreq == 0 || Math.Abs(w.DominantFreqHz - currentFreq) <= 100)
                        {
                            consecutiveStable++;
                            currentFreq = w.DominantFreqHz;
                        }
                        else
                        {
                            if (consecutiveStable > bestConsecutiveStable)
                            {
                                bestConsecutiveStable = consecutiveStable;
                                bestFreq = currentFreq;
                            }

                            consecutiveStable = 1;
                            currentFreq = w.DominantFreqHz;
                        }

                        if (consecutiveStable >= neededConsecutive)
                        {
                            var durationMs = consecutiveStable * windowMs;
                            if (durationMs < minSustainedMs)
                                durationMs = minSustainedMs;
                            if (durationMs > analyzedMs)
                            durationMs = analyzedMs;
                            return (true, currentFreq, durationMs, false, analyzedMs);
                        }
                    }
                }
                else
                {
                    // Allow tiny gaps inside an otherwise sustained tone run.
                    if (consecutiveAnyTone > 0 && nonToneGap < nonToneGapAllowance)
                    {
                        nonToneGap++;
                        continue;
                    }

                    nonToneGap = 0;
                    if (consecutiveStable > bestConsecutiveStable)
                    {
                        bestConsecutiveStable = consecutiveStable;
                        bestFreq = currentFreq;
                    }

                    consecutiveStable = 0;
                    currentFreq = 0;
                    consecutiveAnyTone = 0;
                    anyToneFreqs.Clear();
                }
            }

            if (consecutiveStable > bestConsecutiveStable)
            {
                bestConsecutiveStable = consecutiveStable;
                bestFreq = currentFreq;
            }

            if (bestConsecutiveStable >= neededConsecutive)
            {
                var durationMs = bestConsecutiveStable * windowMs;
                if (durationMs < minSustainedMs)
                    durationMs = minSustainedMs;
                if (durationMs > analyzedMs)
                            durationMs = analyzedMs;
                return (true, bestFreq, durationMs, false, analyzedMs);
            }

            // 2) Repeating short tone detection (tone-only calls)
            // Heuristics:
            // - At least 3 short tone bursts (100ms..900ms) in the analyzed window.
            // - Bursts share roughly the same dominant frequency.
            // - No sustained non-tone "active" audio (speech) present.
            // This intentionally ignores common start/end courtesy beeps on voice calls.
            var repeating = DetectToneOnlySpecialCall(windows, windowMs);
            if (repeating.detected)
            {
                // For tone-only transmissions, it's safe to quiet the entire analyzed window.
                // Our player-side ducking is time based, so we clamp to the scan window.
                var quietMs = Math.Min(analyzedMs, windows.Count * windowMs);
                return (true, repeating.frequencyHz, quietMs, false, analyzedMs);
}

            // 3) "Dash" style alert tones on otherwise normal voice calls.
            // Detect 3 or more longer tone bursts (>=300ms) at the same frequency, with short gaps.
            // Courtesy beeps are typically 1..2 short bursts (100..200ms) and are ignored.
            var dash = DetectAlertDashTones(windows, windowMs);
            if (dash.detected)
            {
                var durationMs = Math.Min(analyzedMs, (dash.endWin - dash.startWin + 1) * windowMs);
                return (true, dash.frequencyHz, durationMs, false, analyzedMs);
}

            
            // If we see tone-like windows right at the end of the analyzed audio, signal the caller to extend the scan window.
            // This helps catch two-tone paging that starts late in the call.
            var tailWindowsToCheck = Math.Min(10, windows.Count);
            var hintExtendScan = false;
            for (var i = windows.Count - tailWindowsToCheck; i < windows.Count; i++)
            {
                if (i < 0)
                    continue;

                if (windows[i].IsTone)
                {
                    hintExtendScan = true;
                    break;
                }
            }

return (false, 0, 0, hintExtendScan, analyzedMs);
        }

        private static double ComputeRms(float[] samples, int start, int count)
        {
            double energy = 0;
            for (var i = 0; i < count; i++)
            {
                var s = samples[start + i];
                energy += s * s;
            }

            if (energy <= 0)
                return 0;

            return Math.Sqrt(energy / Math.Max(1, count));
        }

        private static int ModeOrZero(List<int> values)
        {
            if (values == null || values.Count == 0)
                return 0;

            // Mode with deterministic tie-breaker (lowest frequency wins).
            var counts = new Dictionary<int, int>(values.Count);
            foreach (var v in values)
            {
                if (v <= 0)
                    continue;
                if (counts.TryGetValue(v, out var c))
                    counts[v] = c + 1;
                else
                    counts[v] = 1;
            }

            if (counts.Count == 0)
                return 0;

            var bestFreq = 0;
            var bestCount = -1;
            foreach (var kvp in counts)
            {
                if (kvp.Value > bestCount || (kvp.Value == bestCount && kvp.Key < bestFreq))
                {
                    bestFreq = kvp.Key;
                    bestCount = kvp.Value;
                }
            }

            return bestFreq;
        }

        private static int Median(IEnumerable<int> values)
        {
            if (values == null)
                return 0;

            var list = values.Where(v => v > 0).ToList();
            if (list.Count == 0)
                return 0;

            list.Sort();
            var mid = list.Count / 2;
            if ((list.Count & 1) == 1)
                return list[mid];

            // Even count: average the middle two (rounded).
            return (int)Math.Round((list[mid - 1] + list[mid]) / 2.0);
        }

        
        private static (bool detected, int frequencyHz) DetectToneOnlySpecialCall(List<ToneWindowInfo> windows, int windowMs)
        {
            // Detect special "tone-only" transmissions while avoiding courtesy beeps that bracket voice calls.
            //
            // We support two patterns:
            // 1) Single short beep calls: the entire call is < 2 seconds and is essentially nothing but one beep.
            // 2) Repeating longer tones: tones repeat multiple times for >= 2 seconds total in the analyzed window, with no speech-like energy.
            //
            // Anything with meaningful non-tone active audio is treated as a normal call and ignored.

            if (windows.Count < 3)
                return (false, 0);

            var analyzedMs = windows.Count * windowMs;

            // Treat "active" non-tone energy as likely speech/voice traffic.
            // Use a slightly more sensitive threshold so quiet voice doesn't slip through.
            const double activeRms = 0.012;

            var longestActiveNonTone = 0;
            var activeNonToneCount = 0;
            var curActiveNonTone = 0;

            // Extract tone bursts (runs of tone windows).
            var bursts = new List<(int startWin, int endWin, int durationMs, int DominantFreqHz)>(8);
            var inTone = false;
            var toneStart = 0;
            var toneFreqs = new List<int>(16);

            for (var i = 0; i < windows.Count; i++)
            {
                var w = windows[i];

                var isActiveNonTone = !w.IsTone && w.Rms >= activeRms;
                if (isActiveNonTone)
                {
                    activeNonToneCount++;
                    curActiveNonTone++;
                    if (curActiveNonTone > longestActiveNonTone)
                        longestActiveNonTone = curActiveNonTone;
                }
                else
                {
                    curActiveNonTone = 0;
                }

                if (w.IsTone)
                {
                    if (!inTone)
                    {
                        inTone = true;
                        toneStart = i;
                        toneFreqs.Clear();
                    }
                    if (w.DominantFreqHz != 0)
                        toneFreqs.Add(w.DominantFreqHz);
                }
                else if (inTone)
                {
                    inTone = false;
                    var end = i - 1;
                    var durMs = (end - toneStart + 1) * windowMs;
                    var freq = ModeOrZero(toneFreqs);
                    bursts.Add((toneStart, end, durMs, freq));
                }
            }

            if (inTone)
            {
                var end = windows.Count - 1;
                var durMs = (end - toneStart + 1) * windowMs;
                var freq = ModeOrZero(toneFreqs);
                bursts.Add((toneStart, end, durMs, freq));
            }

            // Guardrails: if there's sustained non-tone activity, assume normal voice call and ignore.
            var activeNonToneFrac = activeNonToneCount / (double)Math.Max(1, windows.Count);
            var longestActiveNonToneMs = longestActiveNonTone * windowMs;

            if (longestActiveNonToneMs >= 500)
                return (false, 0);

            if (activeNonToneFrac >= 0.10)
                return (false, 0);

            // Keep only tone bursts with a real frequency estimate.
            var validBursts = bursts.Where(b => b.durationMs >= windowMs && b.DominantFreqHz != 0).ToList();
            if (validBursts.Count == 0)
                return (false, 0);

            // Case 1: "single short beep call" (<2s total call)
            if (analyzedMs <= 2000)
            {
                // Require exactly one short burst and essentially no other activity.
                // Short beep: ~<=900ms (with 100ms windows this becomes 100..900ms).
                var shortBeep = validBursts.Where(b => b.durationMs >= windowMs && b.durationMs <= 900).ToList();
                if (shortBeep.Count == 1)
                {
                    // Additional guard: keep non-tone activity very near zero for this mode.
                    if (longestActiveNonToneMs <= 200 && activeNonToneFrac <= 0.05)
                        return (true, shortBeep[0].DominantFreqHz);
                }

                return (false, 0);
            }

            // Case 2: "repeating longer tones" (tones repeat multiple times for >=2s total)
            // Accept either:
            // - at least 2 long bursts (>=500ms) totaling >=2000ms, or
            // - at least 3 medium bursts (>=200ms) totaling >=2000ms
            var longBursts = validBursts.Where(b => b.durationMs >= 500).ToList();
            var mediumBursts = validBursts.Where(b => b.durationMs >= 200).ToList();

            var freqSource = longBursts.Count > 0 ? longBursts : mediumBursts;
            if (freqSource.Count == 0)
                return (false, 0);

            var candidateFreq = Median(freqSource.Select(b => b.DominantFreqHz));
            bool FreqMatch(int f) => Math.Abs(f - candidateFreq) <= 100;

            var matchingLong = longBursts.Where(b => FreqMatch(b.DominantFreqHz)).ToList();
            var matchingMedium = mediumBursts.Where(b => FreqMatch(b.DominantFreqHz)).ToList();

            var totalToneMsLong = matchingLong.Sum(b => b.durationMs);
            var totalToneMsMedium = matchingMedium.Sum(b => b.durationMs);

            var qualifies =
                (matchingLong.Count >= 2 && totalToneMsLong >= 2000) ||
                (matchingMedium.Count >= 3 && totalToneMsMedium >= 2000);

            if (!qualifies)
                return (false, 0);

            // Extra guard: ensure gaps between matching bursts are silence-ish (no active non-tone).
            var matching = (matchingLong.Count > 0 ? matchingLong : matchingMedium)
                .OrderBy(b => b.startWin)
                .ToList();

            for (var i = 1; i < matching.Count; i++)
            {
                var gapStart = matching[i - 1].endWin + 1;
                var gapEnd = matching[i].startWin - 1;
                if (gapEnd < gapStart)
                    continue;

                for (var w = gapStart; w <= gapEnd && w < windows.Count; w++)
                {
                    var ww = windows[w];
                    if (!ww.IsTone && ww.Rms >= activeRms)
                        return (false, 0);
                }
            }

            return (true, candidateFreq);
        }



        private static (bool detected, int frequencyHz, int startWin, int endWin) DetectAlertDashTones(
    List<ToneWindowInfo> windows,
    int windowMs)
{
    // Detect "mechanical" alert tone clusters on otherwise normal voice calls.
    //
    // Simplified rules (tuned to avoid speech-trigger false positives):
    // - Extract tone bursts (runs of tone windows).
    // - Find a cluster of bursts where:
    //     - bursts >= 3
    //     - max gap between bursts <= 1000ms
    //     - total cluster span <= 5000ms
    //     - bursts are at (roughly) the same frequency (mechanical, precise)
    //     - gaps are mostly quiet (no speech-like energy)
    //
    // This intentionally ignores the common 1..2 courtesy beeps at the start/end of calls.

    if (windows.Count < 3)
        return (false, 0, 0, 0);

    const int minBursts = 3;

    // Cluster timing rules per your request.
    const int maxGapMs = 1000;
    const int maxClusterSpanMs = 5000;

    // Burst constraints.
    var minBurstMs = windowMs;   // allow short bursts (100ms windows)
    const int maxBurstMs = 2000; // sustained tones are handled by the sustained-tone path

    // Energy gates.
    // - minToneRms: filters ultra-quiet codec artifacts.
    // - activeNonToneRms: windows above this are "active" (likely speech/voice traffic).
    const double minToneRms = 0.018;
    const double activeNonToneRms = 0.020;

    // Mechanical tones should be very frequency-stable. Our scan grid is 50Hz, so use 50Hz tolerance.
    const int freqToleranceHz = 50;

    // 1) Extract tone bursts (runs of tone windows).
    var bursts = new List<(int startWin, int endWin, int durationMs, int DominantFreqHz, int toneWinCount, int modeCount)>(16);

    var inTone = false;
    var toneStart = 0;
    var toneFreqs = new List<int>(24);
    var toneRmsSum = 0.0;
    var toneRmsCount = 0;

    void CommitBurst(int endWin)
    {
        var durMs = (endWin - toneStart + 1) * windowMs;
        if (durMs < minBurstMs || durMs > maxBurstMs)
            return;

        var freq = ModeOrZero(toneFreqs);
        if (freq == 0)
            return;

        var avgRms = toneRmsCount > 0 ? (toneRmsSum / toneRmsCount) : 0;
        if (avgRms < minToneRms)
            return;

        // Mechanical stability guard:
        // - If the burst spans multiple windows, require strong agreement on frequency.
        // - Speech false-positives often have wandering "dominant" bins.
        var modeCount = 0;
        if (toneFreqs.Count > 0)
            modeCount = toneFreqs.Count(f => f == freq);

        if (toneFreqs.Count >= 3)
        {
            var frac = modeCount / (double)toneFreqs.Count;
            if (frac < 0.70)
                return;

            var minF = toneFreqs.Min();
            var maxF = toneFreqs.Max();
            if ((maxF - minF) > freqToleranceHz)
                return;
        }

        bursts.Add((toneStart, endWin, durMs, freq, toneRmsCount, modeCount));
    }

    for (var i = 0; i < windows.Count; i++)
    {
        var w = windows[i];

        if (w.IsTone)
        {
            if (!inTone)
            {
                inTone = true;
                toneStart = i;
                toneFreqs.Clear();
                toneRmsSum = 0;
                toneRmsCount = 0;
            }

            if (w.DominantFreqHz != 0)
                toneFreqs.Add(w.DominantFreqHz);

            toneRmsSum += w.Rms;
            toneRmsCount++;
        }
        else if (inTone)
        {
            inTone = false;
            CommitBurst(i - 1);
        }
    }

    if (inTone)
        CommitBurst(windows.Count - 1);

    if (bursts.Count < minBursts)
        return (false, 0, 0, 0);

    // 2) Find the best cluster by walking time order.
    // We require:
    // - bursts tightly grouped by maxGapMs and maxClusterSpanMs
    // - mostly quiet gaps
    // - frequency agreement within freqToleranceHz
    bursts = bursts.OrderBy(b => b.startWin).ToList();

    static bool FreqMatch(int a, int b, int tol) => Math.Abs(a - b) <= tol;

    (bool detected, int frequencyHz, int startWin, int endWin) best = (false, 0, 0, 0);
    var bestBurstCount = 0;

	    for (var startIdx = 0; startIdx < bursts.Count; startIdx++)
    {
	        var cluster = new List<(int startWin, int endWin, int durationMs, int DominantFreqHz, int toneWinCount, int modeCount)>(8)
        {
            bursts[startIdx]
        };

        var clusterStart = bursts[startIdx].startWin;
        var clusterEnd = bursts[startIdx].endWin;

        for (var j = startIdx + 1; j < bursts.Count; j++)
        {
            var gapMs = (bursts[j].startWin - clusterEnd - 1) * windowMs;
            if (gapMs > maxGapMs)
                break;

            var spanMs = (bursts[j].endWin - clusterStart + 1) * windowMs;
            if (spanMs > maxClusterSpanMs)
                break;

            cluster.Add(bursts[j]);
            clusterEnd = bursts[j].endWin;
        }

        if (cluster.Count < minBursts)
            continue;

        // Frequency agreement (mechanical precision): use median and require all bursts close.
        var candidateFreq = Median(cluster.Select(b => b.DominantFreqHz));
        if (candidateFreq == 0)
            continue;

        var allMatch = true;
        foreach (var b in cluster)
        {
            if (!FreqMatch(b.DominantFreqHz, candidateFreq, freqToleranceHz))
            {
                allMatch = false;
                break;
            }
        }
        if (!allMatch)
            continue;

        // Quiet gaps: between bursts should not contain sustained "active" non-tone windows.
        // Allow at most ONE active non-tone window across all gaps in the cluster.
        var activeNonToneInGaps = 0;
        for (var i = 1; i < cluster.Count; i++)
        {
            var gapStart = cluster[i - 1].endWin + 1;
            var gapEnd = cluster[i].startWin - 1;
            if (gapEnd < gapStart)
                continue;

            for (var w = gapStart; w <= gapEnd && w < windows.Count; w++)
            {
                var ww = windows[w];
                if (!ww.IsTone && ww.Rms >= activeNonToneRms)
                    activeNonToneInGaps++;

                if (activeNonToneInGaps > 1)
                    break;
            }

            if (activeNonToneInGaps > 1)
                break;
        }

        if (activeNonToneInGaps > 1)
        {
            if (AppLog.IsEnabled)
            {
                try
                {
                    AppLog.Add(() => $"AudioToneDiag: dashCandidate rejected (speech in gaps). bursts={cluster.Count} freq~{candidateFreq}Hz spanMs={(clusterEnd - clusterStart + 1) * windowMs}");
                }
                catch { }
            }
            continue;
        }

        if (cluster.Count > bestBurstCount)
        {
            bestBurstCount = cluster.Count;
            best = (true, candidateFreq, clusterStart, clusterEnd);
        }
    }

    if (best.detected && AppLog.IsEnabled)
    {
        try
        {
            var spanMs = (best.endWin - best.startWin + 1) * windowMs;
            AppLog.Add(() => $"AudioToneDiag: dashCluster accepted. bursts={bestBurstCount} freq~{best.frequencyHz}Hz spanMs={spanMs}");
        }
        catch { }
    }

    return best;
}

        private static int[] BuildToneFrequencyList()
        {
            // Most paging and alert tones are in the low audio band. We scan densely enough to be useful,
            // but keep the list bounded to avoid heavy CPU on mobile.
            //
            // 250..2200 in 25Hz steps catches most two-tone paging.
            // 2250..3000 in 50Hz steps adds headroom for odd systems.
            var list = new List<int>(128);

            for (var f = 250; f <= 2200; f += 25)
                list.Add(f);

            for (var f = 2250; f <= 3000; f += 50)
                list.Add(f);

            // Add a few common points with tighter emphasis.
            list.AddRange(new[] { 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1400, 1600, 1800, 2000 });

            // DTMF exact frequencies (to improve dual-tone rejection).
            list.AddRange(new[] { 697, 770, 852, 941, 1209, 1336, 1477, 1633 });

            return list.Distinct().OrderBy(x => x).ToArray();
        }

        private static readonly int[] DtmfRowHz = { 697, 770, 852, 941 };
        private static readonly int[] DtmfColHz = { 1209, 1336, 1477, 1633 };

        private static ToneWindowInfo AnalyzeToneWindow(
            float[] samples,
            int start,
            int count,
            int sampleRate,
            int[] freqs,
            double ratioThreshold,
            double peakinessThreshold,
            double rmsThreshold)
        {
            // Total energy (RMS^2) for ratio checks.
            double energy = 0;
            for (var i = 0; i < count; i++)
            {
                var s = samples[start + i];
                energy += s * s;
            }

            if (energy <= 1e-6)
                return new ToneWindowInfo(false, ToneWindowKind.None, 0, 0, 0, 0, 0);

            // Absolute energy guard: ignore ultra-quiet windows to avoid false detections on codec artifacts.
            var rms = Math.Sqrt(energy / Math.Max(1, count));
            if (rms < rmsThreshold)
                return new ToneWindowInfo(false, ToneWindowKind.None, 0, 0, rms, 0, 0);

            var bestPower = 0.0;
            var secondBestPower = 0.0;
            var bestFreq = 0;
            var secondFreq = 0;

            foreach (var f in freqs)
            {
                var p = GoertzelPower(samples, start, count, sampleRate, f);
                if (p > bestPower)
                {
                    secondBestPower = bestPower;
                    secondFreq = bestFreq;
                    bestPower = p;
                    bestFreq = f;
                }
                else if (p > secondBestPower)
                {
                    secondBestPower = p;
                    secondFreq = f;
                }
            }

            var ratio = bestPower / energy;
            var secondRatio = secondBestPower / energy;
            var peakiness = bestPower / (secondBestPower + 1e-9);

            // Dual-tone rejection (DTMF). DTMF is two simultaneous strong tones.
            // We do not want to treat this as a paging tone.
            if (ratio >= 0.28 && secondRatio >= 0.20 && peakiness <= 1.7)
            {
                if (LooksLikeDtmf(bestFreq, secondFreq))
                    return new ToneWindowInfo(false, ToneWindowKind.DualTone, bestFreq, secondFreq, rms, ratio, peakiness);
            }

            // Single-tone detection.
            // Heuristic thresholds:
            // - Narrow peak must dominate the window energy strongly.
            // - Peak should also be notably stronger than the runner-up frequency (avoids voice/harmonics).
            if (ratio >= ratioThreshold && peakiness >= peakinessThreshold)
                return new ToneWindowInfo(true, ToneWindowKind.SingleTone, bestFreq, secondFreq, rms, ratio, peakiness);

            return new ToneWindowInfo(false, ToneWindowKind.None, 0, 0, rms, ratio, peakiness);
        }

        private static bool LooksLikeDtmf(int f1, int f2)
        {
            // Allow small tolerance because we scan on a grid.
            const int tol = 20;

            bool IsNear(int f, int target) => Math.Abs(f - target) <= tol;

            var hasRow = DtmfRowHz.Any(r => IsNear(f1, r) || IsNear(f2, r));
            var hasCol = DtmfColHz.Any(c => IsNear(f1, c) || IsNear(f2, c));

            return hasRow && hasCol;
        }

        private static bool LooksLikeAfskSignalingBurst(List<ToneWindowInfo> windows, int windowMs)
        {
            // Reject short AFSK signaling bursts that can look tone-like when scanned with Goertzel.
            //
            // Typical patterns:
            // - Duration under about 1.5 seconds
            // - Dominant frequency jumps back and forth rapidly (many changes between adjacent windows)
            //
            // We only apply this as a rejector, not as a detector, so it is intentionally conservative.
            if (windows.Count < 4)
                return false;

            var analyzedMs = windows.Count * windowMs;
            if (analyzedMs > 2000)
                analyzedMs = 2000;

            var maxWins = Math.Min(windows.Count, (int)Math.Ceiling(analyzedMs / (double)windowMs));

            var toneWins = 0;
            var jumps = 0;
            var lastFreq = 0;

            for (var i = 0; i < maxWins; i++)
            {
                var w = windows[i];

                if (!w.IsTone)
                    continue;

                toneWins++;

                if (lastFreq != 0 && w.DominantFreqHz != 0)
                {
                    if (Math.Abs(w.DominantFreqHz - lastFreq) >= 200)
                        jumps++;
                }

                lastFreq = w.DominantFreqHz;
            }

            if (toneWins < 3)
                return false;

            // If we have lots of large frequency jumps in a short time window, this is likely AFSK.
            // Example: MDC1200 style alternation in the low audio band.
            return jumps >= 3;
        }

        private static (bool detected, int frequencyHz, int detectedDurationMs) DetectTwoToneSequentialPaging(List<ToneWindowInfo> windows, int windowMs)
        {
            // Detect an A then B paging pattern:
            // - A: 350ms..2000ms
            // - gap: 0..300ms
            // - B: 800ms..6000ms
            // Also require that the segment is tone-dominant (minimal speech-like active non-tone).
            if (windows.Count < 6)
                return (false, 0, 0);

            const double activeNonToneRms = 0.020;

            // If there is sustained active non-tone energy early, do not treat as a paging tone-out.
            var activeNonToneWins = 0;
            var scanWins = Math.Min(windows.Count, (int)Math.Ceiling(6000 / (double)windowMs));
            for (var i = 0; i < scanWins; i++)
            {
                var w = windows[i];
                if (!w.IsTone && w.Rms >= activeNonToneRms)
                    activeNonToneWins++;
            }

            if ((activeNonToneWins / (double)Math.Max(1, scanWins)) >= 0.15)
                return (false, 0, 0);

            // Extract tone runs.
            var runs = new List<(int startWin, int endWin, int durMs, int freqHz)>(8);
            var inTone = false;
            var runStart = 0;
            var runFreqs = new List<int>(16);

            void CommitRun(int endWin)
            {
                var durMs = (endWin - runStart + 1) * windowMs;
                var freq = ModeOrZero(runFreqs);
                if (durMs >= windowMs && freq != 0)
                    runs.Add((runStart, endWin, durMs, freq));
            }

            for (var i = 0; i < scanWins; i++)
            {
                var w = windows[i];

                if (w.IsTone)
                {
                    if (!inTone)
                    {
                        inTone = true;
                        runStart = i;
                        runFreqs.Clear();
                    }

                    if (w.DominantFreqHz != 0)
                        runFreqs.Add(w.DominantFreqHz);
                }
                else if (inTone)
                {
                    inTone = false;
                    CommitRun(i - 1);
                }
            }

            if (inTone)
                CommitRun(scanWins - 1);

            if (runs.Count < 2)
                return (false, 0, 0);

            // Look for two-tone sequence.
            for (var i = 0; i < runs.Count - 1; i++)
            {
                var a = runs[i];
                var b = runs[i + 1];

                // A duration.
                if (a.durMs < 350 || a.durMs > 2000)
                    continue;

                // Gap.
                var gapMs = (b.startWin - a.endWin - 1) * windowMs;
                if (gapMs < 0) gapMs = 0;
                if (gapMs > 300)
                    continue;

                // B duration.
                if (b.durMs < 800 || b.durMs > 6000)
                    continue;

                // Frequencies should differ.
                if (Math.Abs(a.freqHz - b.freqHz) < 25)
                    continue;

                // Reject siren-like sweeps.
                if (LooksLikeSweep(windows, a.startWin, a.endWin) || LooksLikeSweep(windows, b.startWin, b.endWin))
                    continue;

                var endMs = (b.endWin + 1) * windowMs;
                if (endMs > 8000)
                    endMs = 8000;

                // Prefer B tone frequency (usually longer, more stable).
                return (true, b.freqHz, endMs);
            }

            return (false, 0, 0);
        }

        private static bool LooksLikeSweep(List<ToneWindowInfo> windows, int startWin, int endWin)
        {
            // Sirens and sweep tones are tonal but not paging. They show continuous frequency change.
            if (endWin <= startWin)
                return false;

            var freqs = new List<int>(endWin - startWin + 1);
            for (var i = startWin; i <= endWin && i < windows.Count; i++)
            {
                var f = windows[i].DominantFreqHz;
                if (f != 0)
                    freqs.Add(f);
            }

            if (freqs.Count < 3)
                return false;

            var minF = freqs.Min();
            var maxF = freqs.Max();

            // If the tone runs over a wide range, it is likely a sweep.
            if ((maxF - minF) >= 250)
                return true;

            // If the frequency is mostly monotonic with small steps, also treat as sweep.
            var up = 0;
            var down = 0;
            for (var i = 1; i < freqs.Count; i++)
            {
                var d = freqs[i] - freqs[i - 1];
                if (d > 25) up++;
                else if (d < -25) down++;
            }

            return (up >= 2 && down == 0) || (down >= 2 && up == 0);
        }


        private static double GoertzelPower(float[] samples, int start, int count, int sampleRate, int targetFreq)
        {
            var normalized = 2.0 * Math.PI * targetFreq / sampleRate;
            var cosine = Math.Cos(normalized);
            var coeff = 2.0 * cosine;

            double s0 = 0;
            double s1 = 0;
            double s2 = 0;

            for (var i = 0; i < count; i++)
            {
                s0 = samples[start + i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            // Power estimate.
            return s1 * s1 + s2 * s2 - coeff * s1 * s2;
        }

        private static string? TryGetLocalPathFromUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
                return null;

            if (!u.IsFile)
                return null;

            try
            {
                return u.LocalPath;
            }
            catch
            {
                return null;
            }
        }
private static int Clamp01To100(int v)
        {
            if (v < 0) return 0;
            if (v > 100) return 100;
            return v;
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }


        private static double Lerp(double a, double b, double t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return a + ((b - a) * t);
        }

        private static double ComputeOverallRms(float[] samples)
        {
            if (samples == null || samples.Length == 0)
                return 0;

            double sumSq = 0;
            for (var i = 0; i < samples.Length; i++)
            {
                var v = samples[i];
                sumSq += (double)v * v;
            }

            var meanSq = sumSq / samples.Length;
            if (meanSq <= 0)
                return 0;

            return Math.Sqrt(meanSq);
        }


        private static bool IsFileUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
                return false;

            return u.IsFile || string.Equals(u.Scheme, "file", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeLocalPath(string s)
        {
            // Windows absolute path: C:\...
            if (s.Length >= 3 && char.IsLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))
                return true;

            // Unix-like absolute path: /var/...
            if (s.StartsWith("/", StringComparison.Ordinal))
                return true;

            return false;
        }

        private static string BuildSettingsKey(bool staticEnabled, bool toneEnabled, int staticVolume, int strength, int sensitivity)
        {
            return $"static={staticEnabled};tone={toneEnabled};staticVolume={staticVolume};strength={strength};sens={Clamp01To100(sensitivity)}";
        }

        private static string? TryGetExtensionFromUrl(Uri uri)
        {
            try
            {
                var path = uri.AbsolutePath;
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                var ext = Path.GetExtension(path);
                if (string.IsNullOrWhiteSpace(ext))
                    return null;

                // Keep it conservative
                if (ext.Length > 8)
                    return null;

                return ext;
            }
            catch
            {
                return null;
            }
        }

        private static string Sha256Hex(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
            var sb = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}