namespace KiTTYManager.Core;

/// <summary>
/// Executes connectivity pairs in source batches. The supplied check opens one
/// source route for all target ids; if it returns only a prefix/subset (for
/// example because that source connection was lost), only missing targets are
/// submitted again.
/// </summary>
public static class ConnectivityBatchExecutor
{
    public delegate Task<IReadOnlyList<ConnectivityResult>> CheckFrom(
        Guid sourceId, IReadOnlyList<Guid> targetIds, CancellationToken cancellationToken);

    public static async Task<IReadOnlyList<ConnectivityResult>> CheckPairsAsync(
        IEnumerable<(ManagedServer A, ManagedServer B)> pairs,
        CheckFrom checkFrom,
        CancellationToken cancellationToken = default)
    {
        var pairList = pairs
            .Where(pair => pair.A.Id != pair.B.Id)
            .DistinctBy(pair => OrderedPair(pair.A.Id, pair.B.Id))
            .ToArray();
        var results = new List<ConnectivityResult>();

        foreach (var batch in Batches(pairList.Select(pair => (pair.A, pair.B))))
            results.AddRange(await CheckRemainingAsync(
                batch.Source.Id, batch.TargetIds, checkFrom, cancellationToken));

        var failed = pairList.Where(pair => !results.Any(result =>
            result.Success && result.SourceId == pair.A.Id && result.TargetId == pair.B.Id));
        foreach (var batch in Batches(failed.Select(pair => (pair.B, pair.A))))
            results.AddRange(await CheckRemainingAsync(
                batch.Source.Id, batch.TargetIds, checkFrom, cancellationToken));

        return results;
    }

    private static IEnumerable<ConnectivityBatch> Batches(
        IEnumerable<(ManagedServer Source, ManagedServer Target)> directions) =>
        directions
            .GroupBy(direction => direction.Source.Id)
            .Select(group => new ConnectivityBatch(group.First().Source,
                group.Select(direction => direction.Target.Id).Distinct().ToArray()));

    private static async Task<IReadOnlyList<ConnectivityResult>> CheckRemainingAsync(
        Guid sourceId, IReadOnlyList<Guid> targetIds, CheckFrom checkFrom,
        CancellationToken cancellationToken)
    {
        var remaining = targetIds.Distinct().ToList();
        var results = new List<ConnectivityResult>();
        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchResults = await checkFrom(sourceId, remaining.ToArray(), cancellationToken);
            var tested = batchResults
                .Where(result => result.SourceId == sourceId && remaining.Contains(result.TargetId))
                .Select(result => result.TargetId)
                .Distinct()
                .ToHashSet();
            results.AddRange(batchResults);
            if (tested.Count == 0)
                throw new InvalidOperationException("Проверка не вернула результат ни для одной цели.");
            remaining.RemoveAll(tested.Contains);
        }
        return results;
    }

    private static (Guid A, Guid B) OrderedPair(Guid left, Guid right) =>
        left.CompareTo(right) <= 0 ? (left, right) : (right, left);
}
