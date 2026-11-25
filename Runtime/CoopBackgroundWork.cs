using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProbablyCoop;

internal static class CoopBackgroundWork
{
    internal static Task<T> Start<T>(CancellationToken session, Func<CancellationToken, T> work)
    {
        if (session.IsCancellationRequested) return Task.FromCanceled<T>(session);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ModHelper.RunInBackground("probably_coop", mod =>
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(session, mod);
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                T result = work(stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                completion.SetResult(result);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                completion.SetCanceled();
            }
            catch (Exception error)
            {
                completion.SetException(error);
                // An ended session releases its task before an in-flight worker
                // can report a failure. The live consumer still sees the error.
                _ = completion.Task.Exception;
            }
        }))
            return Task.FromException<T>(new InvalidOperationException("Co-op background work requires an active mod."));
        return completion.Task;
    }
}
