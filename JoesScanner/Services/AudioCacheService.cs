using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace JoesScanner.Services;

public class AudioCacheService : IAudioCacheService
{
    private readonly HttpClient _httpClient = new();

    public async Task<string> CacheAsync(string audioUrl, DateTime timestamp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new ArgumentException("Audio URL is required.", nameof(audioUrl));

        string appData = FileSystem.AppDataDirectory;
        Directory.CreateDirectory(appData);

        string fileName = $"{timestamp:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.mp3";
        string path = Path.Combine(appData, fileName);

        byte[] bytes = await _httpClient.GetByteArrayAsync(audioUrl, cancellationToken);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);

        return path;
    }
}
