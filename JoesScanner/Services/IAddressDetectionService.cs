using JoesScanner.Models;

namespace JoesScanner.Services
{
    public interface IAddressDetectionService
    {
        // Applies address detection to the provided call item based on current settings.
        // Safe to call for every call or transcription update.
        void Apply(CallItem item);
    }
}
