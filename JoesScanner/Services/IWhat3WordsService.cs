using JoesScanner.Models;

namespace JoesScanner.Services
{
    public interface IWhat3WordsService
    {
        Task ResolveCoordinatesIfNeededAsync(CallItem item, CancellationToken cancellationToken);
    }
}
