using JoesScanner.Models;
using JoesScanner.Services;

internal static class AndroidAudioTests
{
    public static void Run()
    {
        var denied = new AndroidClipFocusState();
        denied.Requested(registered: false);
        Check(denied.Ready.IsCompletedSuccessfully && !denied.Ready.Result && !denied.Release(),
            "Denied focus must neither permit playback nor attempt to abandon an unregistered request.");
        var mixed = new AndroidClipFocusState();
        mixed.ReadyWithoutFocus();
        Check(mixed.Ready.Result && !mixed.Release(), "Mix normally should be ready without a focus registration.");
        Console.WriteLine("PASS Android focus denial and playback without focus");

        var previous = new AndroidClipFocusState();
        previous.Requested(registered: true, delayed: true);
        Check(!previous.Ready.IsCompleted, "Delayed focus must wait for gain.");
        Check(previous.Release(), "An abandoned delayed request must be unregistered.");

        var next = new AndroidClipFocusState();
        next.Requested(registered: true, delayed: true);
        previous.OnGain();
        previous.OnLoss();
        Check(!previous.Ready.IsCompleted && !previous.Lost.IsCompleted &&
              !next.Ready.IsCompleted && !next.Lost.IsCompleted && !previous.Release(),
            "Late callbacks and duplicate cleanup on a skipped request must not affect a replacement.");
        next.OnGain();
        Check(next.Ready.Result, "Only the current request's gain should allow it to play.");
        next.OnLoss();
        Check(next.Lost.IsCompleted && next.Release() && !next.Release(),
            "A lost registration must be released once.");
        var completed = new AndroidClipFocusState();
        completed.Requested(registered: true);
        Check(completed.Ready.Result && completed.Release() && !completed.Release(),
            "A normally finished recording must abandon focus exactly once.");
        Console.WriteLine("PASS Android delayed focus, rapid replacement, and release ownership");

        var audio = new AndroidAudioInterruption();
        audio.SetCallActive(true);
        audio.BlockForFocus();
        Check(audio.IsBlocked && !audio.RequiresPlay, "A detected call must suspend playback without requiring a tap.");
        audio.SetCallActive(false);
        Check(!audio.IsBlocked, "Ending the detected call must allow the same recording to reacquire focus.");
        audio.BlockForFocus();
        audio.SetCallActive(true);
        audio.SetCallActive(false);
        Check(audio.RequiresPlay && audio.IsBlocked, "An unrelated call must not clear a prior denial or unknown focus loss.");
        audio.RequestPlay();
        Check(!audio.IsBlocked, "Explicit Play must allow a fresh focus request.");
        Console.WriteLine("PASS Android phone interruption recovery and explicit recovery after unknown focus loss");

        var queue = new Queue<int>([10]);
        var interrupted = queue.Peek();
        audio.RouteDisconnected(AudioDisconnectionBehavior.PausePlayback);
        audio.RouteDisconnected(AudioDisconnectionBehavior.PausePlayback);
        queue.Enqueue(11);
        queue.Enqueue(12);
        audio.SetCallActive(true);
        audio.SetCallActive(false);
        Check(audio.IsBlocked && audio.RequiresPlay && queue.Peek() == interrupted,
            "Ending a call must not resume a disconnected route or move the queue's playback boundary.");
        audio.SetCallActive(true);
        audio.RequestPlay();
        Check(audio.IsBlocked && !audio.RequiresPlay, "Explicit Play cannot bypass an active phone call.");
        audio.SetCallActive(false);
        Check(!audio.IsBlocked && queue.Dequeue() == interrupted && queue.SequenceEqual([11, 12]),
            "After explicit Play, the interrupted recording and later calls should remain in order.");
        audio.RouteDisconnected(AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker);
        Check(!audio.IsBlocked, "The continue preference must leave playback to Android routing.");
        Console.WriteLine("PASS Android route disconnection, overlapping interruptions, and queue boundary");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
