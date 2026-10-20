namespace ApplicationTestLab;

/// <summary>One test run per OS host, across applications, bundles and dashboards.</summary>
public sealed class HostResourceLease : IDisposable
{
    private readonly ManualResetEventSlim release = new();
    private readonly Thread owner;
    private int disposed;

    private HostResourceLease(TaskCompletionSource<HostResourceLease> ready, CancellationToken cancellation)
    {
        // Windows mutex ownership is thread-affine. Keep acquisition and release on this thread.
        owner = new Thread(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var mutex = new Mutex(false, @"Global\ApplicationTestLab.Resources.v1");
                    bool acquired = false;
                    try
                    {
                        while (!acquired)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            try { acquired = mutex.WaitOne(200); }
                            catch (AbandonedMutexException) { acquired = true; }
                        }
                        ready.SetResult(this);
                        release.Wait();
                    }
                    finally { if (acquired) mutex.ReleaseMutex(); }
                }
                else
                {
                    // A fixed machine-wide path; neither HOME nor per-user TMPDIR may scope this lock.
                    const string path = "/tmp/application-test-lab.resources.v1.lock";
                    FileStream? file = null;
                    while (file is null)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        try
                        {
                            file = new FileStream(path, new FileStreamOptions
                            {
                                Mode = FileMode.OpenOrCreate,
                                Access = FileAccess.ReadWrite,
                                Share = FileShare.None,
                                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                                    | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                            });
                        }
                        catch (IOException) { cancellation.WaitHandle.WaitOne(200); }
                    }
                    using (file) { ready.SetResult(this); release.Wait(); }
                    // Never unlink a lock file: another waiter may already hold its inode open.
                }
            }
            catch (OperationCanceledException) { ready.TrySetCanceled(cancellation); }
            catch (Exception error) { ready.TrySetException(error); }
        })
        { IsBackground = true, Name = "Test Lab host resource owner" };
        owner.Start();
    }

    public static Task<HostResourceLease> AcquireAsync(CancellationToken cancellation = default)
    {
        var ready = new TaskCompletionSource<HostResourceLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = new HostResourceLease(ready, cancellation);
        return ready.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        release.Set();
        owner.Join();
        release.Dispose();
    }
}
