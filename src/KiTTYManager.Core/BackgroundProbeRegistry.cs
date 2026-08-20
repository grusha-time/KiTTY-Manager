namespace KiTTYManager.Core;

/// <summary>Не допускает двух фоновых проверок одной сессии и отменяет старую.</summary>
public sealed class BackgroundProbeRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, CancellationTokenSource> active = [];

    public Lease? TryStart(Guid serverId)
    {
        lock (sync)
        {
            if (active.ContainsKey(serverId)) return null;
            var source = new CancellationTokenSource();
            active[serverId] = source;
            return new Lease(this, serverId, source);
        }
    }

    public void Cancel(Guid serverId)
    {
        CancellationTokenSource? source;
        lock (sync)
        {
            if (!active.Remove(serverId, out source)) return;
        }
        source.Cancel();
        // Lease может ещё читать Token и завершает/освобождает source сам.
    }

    public void CancelAll()
    {
        CancellationTokenSource[] sources;
        lock (sync)
        {
            sources = active.Values.ToArray();
            active.Clear();
        }
        foreach (var source in sources) source.Cancel();
        // Active leases still own and dispose their sources.
    }

    private void Complete(Guid serverId, CancellationTokenSource source)
    {
        lock (sync)
        {
            if (active.TryGetValue(serverId, out var current) && ReferenceEquals(current, source))
                active.Remove(serverId);
        }
        source.Dispose();
    }

    public sealed class Lease : IDisposable
    {
        private readonly BackgroundProbeRegistry owner;
        private readonly Guid serverId;
        private CancellationTokenSource? source;

        internal Lease(BackgroundProbeRegistry owner, Guid serverId, CancellationTokenSource source) =>
            (this.owner, this.serverId, this.source) = (owner, serverId, source);

        public CancellationToken Token => source?.Token ?? new CancellationToken(true);

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref source, null);
            if (value is not null) owner.Complete(serverId, value);
        }
    }
}
