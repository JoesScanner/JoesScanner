using System.Collections.Generic;
using System.Threading;
using JoesScanner.Models;

namespace JoesScanner.Services;

public interface ICallStreamService
{
    IAsyncEnumerable<CallItem> GetCallStreamAsync(CancellationToken cancellationToken);
}
