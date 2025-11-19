using System;
using System.Threading;
using System.Threading.Tasks;

namespace JoesScanner.Services;

public interface IAudioCacheService
{
    Task<string> CacheAsync(string audioUrl, DateTime timestamp, CancellationToken cancellationToken = default);
}
