namespace JoesScanner.Services;

public sealed partial class DurableLiveQueue
{
    public DurableLiveQueue(IDatabasePathProvider paths)
        : this(paths, new ProtectedCredentials(new PlatformCredentialStorage()).SignedReferenceKey()) { }
}
