namespace KiTTYManager.Core;

using System.Runtime.ExceptionServices;

/// <summary>
/// Provides non-blocking background sequential persistence for ManagerConfig.
/// Immediately captures a consistent snapshot on caller thread (UI),
/// then asynchronously flushes the latest snapshot to disk sequentially.
/// </summary>
public sealed class BackgroundConfigSaver
{
    private readonly object lockObject = new();
    private readonly Action<string, ManagerConfig> saveSnapshotAction;
    private ManagerConfig? pendingSnapshot;
    private bool workerRunning;
    private Task? currentWorkerTask;
    private long enqueuedVersion;
    private long savedVersion;
    private Exception? lastError;

    public string ConfigPath { get; set; }
    public Action<Exception>? OnSaveFailed { get; set; }

    public BackgroundConfigSaver(string configPath, Action<string, ManagerConfig>? saveSnapshotAction = null)
    {
        ConfigPath = configPath;
        this.saveSnapshotAction = saveSnapshotAction ?? ConfigStore.SaveSnapshot;
    }

    public bool IsBusy
    {
        get
        {
            lock (lockObject)
            {
                return pendingSnapshot is not null || workerRunning;
            }
        }
    }

    public Exception? LastError
    {
        get
        {
            lock (lockObject)
            {
                return lastError;
            }
        }
    }

    public bool HasPendingChanges
    {
        get
        {
            lock (lockObject)
            {
                return pendingSnapshot is not null || savedVersion < enqueuedVersion;
            }
        }
    }

    public long EnqueuedVersion
    {
        get
        {
            lock (lockObject)
            {
                return enqueuedVersion;
            }
        }
    }

    public long SavedVersion
    {
        get
        {
            lock (lockObject)
            {
                return savedVersion;
            }
        }
    }

    public bool HasPendingUnsavedChanges => HasPendingChanges;

    public long Save(ManagerConfig config)
    {
        lock (lockObject)
        {
            var snapshot = config.CloneForSave();
            enqueuedVersion++;
            pendingSnapshot = snapshot;
            lastError = null;
            if (!workerRunning)
            {
                workerRunning = true;
                currentWorkerTask = Task.Run(ProcessPendingSavesAsync);
            }
            return enqueuedVersion;
        }
    }

    public void ClearPending()
    {
        lock (lockObject)
        {
            pendingSnapshot = null;
            enqueuedVersion = savedVersion;
            lastError = null;
        }
    }

    public void RetryPendingSave()
    {
        lock (lockObject)
        {
            if (pendingSnapshot is not null && !workerRunning)
            {
                lastError = null;
                workerRunning = true;
                currentWorkerTask = Task.Run(ProcessPendingSavesAsync);
            }
        }
    }

    private void ProcessPendingSavesAsync()
    {
        while (true)
        {
            ManagerConfig snapshotToWrite;
            string path;
            long targetVersion;

            lock (lockObject)
            {
                if (pendingSnapshot is null)
                {
                    workerRunning = false;
                    return;
                }

                snapshotToWrite = pendingSnapshot;
                pendingSnapshot = null;
                targetVersion = enqueuedVersion;
                path = ConfigPath;
            }

            try
            {
                var writeCopy = snapshotToWrite.CloneForSave();
                saveSnapshotAction(path, writeCopy);
                lock (lockObject)
                {
                    savedVersion = targetVersion;
                    lastError = null;
                }
            }
            catch (Exception ex)
            {
                Action<Exception>? failureHandler;
                lock (lockObject)
                {
                    lastError = ex;
                    if (pendingSnapshot is null)
                    {
                        pendingSnapshot = snapshotToWrite;
                    }
                    workerRunning = false;
                    failureHandler = OnSaveFailed;
                }
                failureHandler?.Invoke(ex);
                return;
            }
        }
    }

    public void WaitForPendingSave()
    {
        while (true)
        {
            Task? taskToWait;
            lock (lockObject)
            {
                if (lastError is not null && (savedVersion < enqueuedVersion || pendingSnapshot is not null))
                {
                    ExceptionDispatchInfo.Capture(lastError).Throw();
                }

                if (pendingSnapshot is null && !workerRunning)
                {
                    if (lastError is not null)
                    {
                        ExceptionDispatchInfo.Capture(lastError).Throw();
                    }
                    return;
                }

                taskToWait = currentWorkerTask;
            }

            if (taskToWait is not null)
            {
                try { taskToWait.GetAwaiter().GetResult(); } catch { }
            }
            else
            {
                Thread.Sleep(5);
            }
        }
    }

    public async Task FlushPendingSavesAsync()
    {
        while (true)
        {
            Task? taskToWait;
            lock (lockObject)
            {
                if (lastError is not null && (savedVersion < enqueuedVersion || pendingSnapshot is not null))
                {
                    ExceptionDispatchInfo.Capture(lastError).Throw();
                }

                if (pendingSnapshot is null && !workerRunning)
                {
                    if (lastError is not null)
                    {
                        ExceptionDispatchInfo.Capture(lastError).Throw();
                    }
                    return;
                }

                taskToWait = currentWorkerTask;
            }

            if (taskToWait is not null)
            {
                try { await taskToWait.ConfigureAwait(false); } catch { }
            }
            else
            {
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
    }
}
