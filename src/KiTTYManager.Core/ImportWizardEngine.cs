using System.Text.Json;

namespace KiTTYManager.Core;

public enum ImportMatchConfidence { None, Probable, Exact }
public enum ImportDecision { Skip, Add, KeepCurrent, UseIncoming }
public enum ImportGroupDecision { CreateNew, MergeKeepCurrentName, MergeUseIncomingName }

public sealed record ImportSessionCandidate(Guid Id, string Display)
{
    public override string ToString() => Display;
}

public sealed class ImportSessionDecision
{
    public Guid IncomingId { get; init; }
    public Guid? CurrentId { get; set; }
    public string IncomingName { get; init; } = "";
    public string IncomingEndpoint { get; init; } = "";
    public string CurrentName { get; init; } = "";
    public string CurrentEndpoint { get; init; } = "";
    public string IncomingGroup { get; init; } = "Без группы";
    public string CurrentGroup { get; init; } = "";
    public ImportMatchConfidence Confidence { get; init; }
    public string Reason { get; init; } = "";
    public ImportDecision Decision { get; set; }
    public bool UseIncomingGroup { get; set; }
    public int CheckedFields { get; set; }
    public List<ImportSessionCandidate> Candidates { get; init; } = [];
    public string IncomingDisplay => $"{IncomingName} — {IncomingEndpoint} — {IncomingGroup}";
    public string CurrentDisplay => CurrentId is null ? "—" : $"{CurrentName} — {CurrentEndpoint} — {CurrentGroup}";
}

public sealed class ImportFieldDecision
{
    public Guid IncomingSessionId { get; init; }
    public string SessionName { get; init; } = "";
    public string PropertyName { get; init; } = "";
    public string Label { get; init; } = "";
    public string CurrentValue { get; init; } = "";
    public string IncomingValue { get; init; } = "";
    public bool UseIncoming { get; set; }
}

public sealed class ImportGroupSuggestion
{
    public Guid IncomingId { get; init; }
    public Guid? CurrentId { get; init; }
    public string IncomingPath { get; init; } = "";
    public string CurrentPath { get; init; } = "";
    public int MatchedSessions { get; init; }
    public int IncomingSessions { get; init; }
    public ImportGroupDecision Decision { get; set; }
    public bool Merge
    {
        get => Decision != ImportGroupDecision.CreateNew;
        set => Decision = value ? ImportGroupDecision.MergeKeepCurrentName : ImportGroupDecision.CreateNew;
    }
    public bool UseIncomingName
    {
        get => Decision == ImportGroupDecision.MergeUseIncomingName;
        set
        {
            if (value) Decision = ImportGroupDecision.MergeUseIncomingName;
            else if (Decision == ImportGroupDecision.MergeUseIncomingName)
                Decision = ImportGroupDecision.MergeKeepCurrentName;
        }
    }
}

public sealed class ImportProxyDecision
{
    public Guid IncomingId { get; init; }
    public Guid? CurrentId { get; init; }
    public string Name { get; init; } = "";
    public string StartupName { get; init; } = "";
    public ImportMatchConfidence Confidence { get; init; }
    public ImportDecision Decision { get; set; }
    public string ConfidenceDisplay => Confidence switch
    {
        ImportMatchConfidence.Exact => "Полное совпадение",
        ImportMatchConfidence.Probable => "Похожая точка входа",
        _ => "Новая точка входа"
    };
}

public sealed class ImportLinkDecision
{
    public int IncomingIndex { get; init; }
    public string FromName { get; init; } = "";
    public string ToName { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool ExistingConflict { get; init; }
    public ImportDecision Decision { get; set; }
}

public sealed class ImportWizardPlan
{
    public List<ImportSessionDecision> Sessions { get; init; } = [];
    public List<ImportFieldDecision> Fields { get; init; } = [];
    public List<ImportGroupSuggestion> Groups { get; init; } = [];
    public List<ImportProxyDecision> Proxies { get; init; } = [];
    public List<ImportLinkDecision> Links { get; init; } = [];
    public int NewSessions => Sessions.Count(x => x.CurrentId is null);
    public int ExactSessions => Sessions.Count(x => x.Confidence == ImportMatchConfidence.Exact);
    public int ProbableSessions => Sessions.Count(x => x.Confidence == ImportMatchConfidence.Probable);
}

public static class ImportSearchPolicy
{
    public static bool Matches(object? item, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var text = item switch
        {
            ImportSessionDecision x => $"{x.IncomingName} {x.CurrentName} {x.IncomingGroup} {x.CurrentGroup} {x.Reason}",
            ImportFieldDecision x => $"{x.SessionName} {x.Label} {x.CurrentValue} {x.IncomingValue}",
            ImportGroupSuggestion x => $"{x.IncomingPath} {x.CurrentPath}",
            ImportProxyDecision x => $"{x.Name} {x.Confidence} {x.Decision}",
            ImportLinkDecision x => $"{x.FromName} {x.ToName} {x.Reason}",
            _ => ""
        };
        return text.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

public static class ImportWizardEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ImportWizardPlan Analyze(ManagerConfig current, ManagerConfig incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        var currentLocations = Locations(current);
        var incomingLocations = Locations(incoming);
        var sessions = new List<ImportSessionDecision>();

        foreach (var item in incomingLocations)
        {
            var exact = currentLocations.FirstOrDefault(x => x.Server.Id == item.Server.Id);
            if (exact is not null)
            {
                sessions.Add(Session(item, exact, ImportMatchConfidence.Exact,
                    "Совпадает внутренний ID", ImportDecision.KeepCurrent, [exact]));
                continue;
            }

            // Passwords, keys, TOTP and imported commands are deliberately absent here.
            var endpointMatches = currentLocations.Where(x =>
                string.Equals(x.Server.CleanHost, item.Server.CleanHost, StringComparison.OrdinalIgnoreCase) &&
                x.Server.Port == item.Server.Port &&
                string.Equals(x.Server.EffectiveUsername, item.Server.EffectiveUsername,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (endpointMatches.Count > 1)
            {
                var sameContext = endpointMatches.Where(x =>
                    string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                if (sameContext.Count == 1)
                {
                    sessions.Add(Session(item, sameContext[0], ImportMatchConfidence.Probable,
                        "Совпадают хост, порт, логин и контекст группы; требуется подтверждение",
                        ImportDecision.KeepCurrent, endpointMatches));
                    continue;
                }
            }
            if (endpointMatches.Count == 1)
            {
                sessions.Add(Session(item, endpointMatches[0], ImportMatchConfidence.Probable,
                    "Совпадают хост, порт и логин; требуется подтверждение", ImportDecision.KeepCurrent,
                    endpointMatches));
                continue;
            }

                sessions.Add(Session(item, null, ImportMatchConfidence.None,
                endpointMatches.Count > 1 ? "Несколько возможных совпадений" : "Новая сессия",
                endpointMatches.Count > 1 ? ImportDecision.Skip : ImportDecision.Add, endpointMatches));
        }

        var groups = AnalyzeGroups(current, incoming, sessions, currentLocations, incomingLocations);
        var entryPoints = EntryPointServerIds(current, incoming);
        var fields = AnalyzeFields(sessions, currentLocations, incomingLocations, entryPoints);
        var links = AnalyzeLinks(current, incoming, sessions);
        var incomingServers = incoming.AllServers().ToDictionary(x => x.Id);
        var proxies = incoming.BaseProxies.Select(proxy =>
        {
            var byId = current.BaseProxies.FirstOrDefault(x => x.Id == proxy.Id);
            var byEndpoint = byId ?? current.BaseProxies.SingleOrDefaultSafe(x =>
                string.Equals(x.Host, proxy.Host, StringComparison.OrdinalIgnoreCase) && x.Port == proxy.Port);
            return new ImportProxyDecision
            {
                IncomingId = proxy.Id, CurrentId = byEndpoint?.Id, Name = proxy.Name,
                StartupName = proxy.StartupServerId is Guid startup && incomingServers.TryGetValue(startup, out var startupServer)
                    ? startupServer.Name : "не задана",
                Confidence = byId is not null ? ImportMatchConfidence.Exact :
                    byEndpoint is not null ? ImportMatchConfidence.Probable : ImportMatchConfidence.None,
                Decision = byEndpoint is null ? ImportDecision.Add : ImportDecision.KeepCurrent
            };
        }).ToList();
        return new ImportWizardPlan { Sessions = sessions, Fields = fields, Groups = groups, Proxies = proxies, Links = links };
    }

    // Сессии, через которые стартуют jumphost-маршруты: их поля по умолчанию не заменяются,
    // чтобы импорт случайно не сломал рабочие точки входа.
    private static HashSet<Guid> EntryPointServerIds(ManagerConfig current, ManagerConfig incoming) =>
        current.BaseProxies.Select(x => x.StartupServerId)
            .Concat(incoming.BaseProxies.Select(x => x.StartupServerId))
            .Where(x => x is not null).Select(x => x!.Value).ToHashSet();

    public static IReadOnlyList<string> DescribePlan(ImportWizardPlan plan)
    {
        var lines = new List<string>
        {
            $"IMPORT PLAN sessions={plan.Sessions.Count} groups={plan.Groups.Count} proxies={plan.Proxies.Count} links={plan.Links.Count}"
        };
        lines.AddRange(plan.Groups.Select(x =>
            $"IMPORT GROUP incoming=\"{LogText(x.IncomingPath)}\" current=\"{LogText(x.CurrentPath)}\" decision={x.Decision}"));
        lines.AddRange(plan.Sessions.Select(x =>
            $"IMPORT SESSION incoming=\"{LogText(x.IncomingName)}\" current=\"{LogText(x.CurrentName)}\" group=\"{LogText(x.IncomingGroup)}\" decision={x.Decision} moveGroup={x.UseIncomingGroup}"));
        lines.AddRange(plan.Sessions
            .Where(x => plan.Fields.Any(f => f.IncomingSessionId == x.IncomingId && f.UseIncoming))
            .Select(x => $"IMPORT SESSION FIELDS incoming=\"{LogText(x.IncomingName)}\" selected=" +
                string.Join(",", plan.Fields
                    .Where(f => f.IncomingSessionId == x.IncomingId && f.UseIncoming)
                    .Select(f => f.PropertyName))));
        lines.AddRange(plan.Proxies.Select(x =>
            $"IMPORT JH name=\"{LogText(x.Name)}\" decision={x.Decision} confidence={x.Confidence}"));
        lines.AddRange(plan.Links.Select(x =>
            $"IMPORT LINK from=\"{LogText(x.FromName)}\" to=\"{LogText(x.ToName)}\" decision={x.Decision}"));
        return lines;
    }

    public static string DescribeResult(ManagerConfig before, ManagerConfig after) =>
        $"IMPORT RESULT sessions={before.AllServers().Count()}->{after.AllServers().Count()} " +
        $"groups={FlattenGroups(before.Groups).Count()}->{FlattenGroups(after.Groups).Count()} " +
        $"proxies={before.BaseProxies.Count}->{after.BaseProxies.Count} links={before.Links.Count}->{after.Links.Count}";

    private static string LogText(string value) => value.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');

    public static void RefreshSessionDependencies(
        ManagerConfig current, ManagerConfig incoming, ImportWizardPlan plan)
    {
        var currentLocations = Locations(current);
        var incomingLocations = Locations(incoming);
        Replace(plan.Fields, AnalyzeFields(plan.Sessions, currentLocations, incomingLocations,
            EntryPointServerIds(current, incoming)));
        Replace(plan.Groups, AnalyzeGroups(current, incoming, plan.Sessions,
            currentLocations, incomingLocations));
        Replace(plan.Links, AnalyzeLinks(current, incoming, plan.Sessions));

        static void Replace<T>(List<T> target, IEnumerable<T> values)
        {
            target.Clear();
            target.AddRange(values);
        }
    }

    public static ManagerConfig Merge(ManagerConfig current, ManagerConfig incoming, ImportWizardPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateGroupChoices(plan);
        var result = Clone(current);
        var incomingById = incoming.AllServers().ToDictionary(x => x.Id);
        var serverMap = new Dictionary<Guid, Guid>();
        var importedServerIds = new HashSet<Guid>();
        var importedFields = new Dictionary<Guid, HashSet<string>>();
        var groupMap = BuildGroupMap(result, incoming, plan.Groups);

        foreach (var row in plan.Sessions)
        {
            if (!incomingById.TryGetValue(row.IncomingId, out var source) || row.Decision == ImportDecision.Skip)
                continue;
            if (row.CurrentId is Guid currentId && result.FindServer(currentId) is not null)
            {
                if (row.Decision == ImportDecision.Add)
                {
                    var duplicate = Clone(source); duplicate.Id = Guid.NewGuid();
                    serverMap[row.IncomingId] = duplicate.Id;
                    AddToMappedGroup(result, incoming, row.IncomingId, duplicate, groupMap);
                    importedServerIds.Add(duplicate.Id);
                    continue;
                }
                serverMap[row.IncomingId] = currentId;
                if (row.Decision == ImportDecision.UseIncoming)
                {
                    var replacement = Clone(source);
                    replacement.Id = currentId;
                    ReplaceServerInPlace(result, currentId, replacement);
                    importedServerIds.Add(currentId);
                }
                else
                {
                    var selectedFields = plan.Fields.Where(x => x.IncomingSessionId == row.IncomingId && x.UseIncoming).ToArray();
                    ApplySelectedFields(result.FindServer(currentId)!, source, selectedFields);
                    importedFields[currentId] = selectedFields.Select(x => x.PropertyName).ToHashSet();
                }
                if (row.UseIncomingGroup)
                    MoveToMappedGroup(result, incoming, row.IncomingId, currentId, groupMap);
                continue;
            }
            if (row.Decision != ImportDecision.Add && row.Decision != ImportDecision.UseIncoming) continue;
            var added = Clone(source);
            if (result.FindServer(added.Id) is not null) added.Id = Guid.NewGuid();
            serverMap[row.IncomingId] = added.Id;
            AddToMappedGroup(result, incoming, row.IncomingId, added, groupMap);
            importedServerIds.Add(added.Id);
        }

        var proxyMap = new Dictionary<Guid, Guid>();
        var importedProxyIds = new HashSet<Guid>();
        foreach (var row in plan.Proxies)
        {
            var source = incoming.BaseProxies.FirstOrDefault(x => x.Id == row.IncomingId);
            if (source is null || row.Decision == ImportDecision.Skip) continue;
            if (row.CurrentId is Guid currentId && result.BaseProxies.Any(x => x.Id == currentId))
            {
                if (row.Decision == ImportDecision.Add)
                {
                    var duplicate = Clone(source); duplicate.Id = Guid.NewGuid();
                    result.BaseProxies.Add(duplicate); proxyMap[row.IncomingId] = duplicate.Id;
                    importedProxyIds.Add(duplicate.Id);
                    continue;
                }
                proxyMap[row.IncomingId] = currentId;
                if (row.Decision != ImportDecision.UseIncoming) continue;
                var replacement = Clone(source); replacement.Id = currentId;
                var index = result.BaseProxies.FindIndex(x => x.Id == currentId);
                result.BaseProxies[index] = replacement;
                importedProxyIds.Add(currentId);
            }
            else if (row.Decision is ImportDecision.Add or ImportDecision.UseIncoming)
            {
                var added = Clone(source);
                if (result.BaseProxies.Any(x => x.Id == added.Id)) added.Id = Guid.NewGuid();
                result.BaseProxies.Add(added); proxyMap[row.IncomingId] = added.Id;
                importedProxyIds.Add(added.Id);
            }
        }

        RemapReferences(result, serverMap, proxyMap, importedServerIds, importedProxyIds, importedFields);
        ApplyPreferredRoutes(current, incoming, result, plan.Sessions, serverMap, proxyMap);
        ApplyLinkDecisions(result, incoming, plan.Links, serverMap, proxyMap);
        return result;
    }

    public static void ValidateGroupChoices(ImportWizardPlan plan)
    {
        var conflict = plan.Groups
            .Where(x => x.Decision == ImportGroupDecision.MergeUseIncomingName && x.CurrentId is not null)
            .GroupBy(x => x.CurrentId!.Value)
            .FirstOrDefault(x => x.Count() > 1);
        if (conflict is null) return;
        var names = string.Join(", ", conflict.Select(x => $"«{x.IncomingPath}»"));
        throw new InvalidDataException(
            $"Одну текущую группу нельзя одновременно переименовать из нескольких групп: {names}. " +
            "Выберите «Добавить в существующую и переименовать» только у одной из них.");
    }

    public static readonly Guid NoMatchCandidateId = Guid.Empty;

    private static ImportSessionDecision Session(
        ServerLocation incoming, ServerLocation? current, ImportMatchConfidence confidence,
        string reason, ImportDecision decision, IEnumerable<ServerLocation> candidates) => new()
    {
        IncomingId = incoming.Server.Id, CurrentId = current?.Server.Id,
        IncomingName = incoming.Server.Name, IncomingEndpoint = incoming.Server.Endpoint,
        CurrentName = current?.Server.Name ?? "", CurrentEndpoint = current?.Server.Endpoint ?? "",
        IncomingGroup = incoming.Path, CurrentGroup = current?.Path ?? "",
        Confidence = confidence, Reason = reason, Decision = decision,
        UseIncomingGroup = current is not null &&
            !string.Equals(incoming.Path, current.Path, StringComparison.OrdinalIgnoreCase),
        Candidates = candidates.Select(item => new ImportSessionCandidate(item.Server.Id,
            $"{item.Server.Name} — {item.Server.Endpoint} — {item.Path}")).ToList()
    };

    // «Взять данные из файла» означает полную замену: группа и все поля
    // отмечаются автоматически, отдельные галочки потом можно снять.
    public static void ApplyFullImportCascade(ImportWizardPlan plan, IEnumerable<Guid> incomingIds)
    {
        var ids = incomingIds.ToHashSet();
        foreach (var row in plan.Sessions.Where(x => ids.Contains(x.IncomingId) && x.Decision == ImportDecision.UseIncoming))
            row.UseIncomingGroup = true;
        foreach (var field in plan.Fields.Where(x => ids.Contains(x.IncomingSessionId)))
            field.UseIncoming = true;
    }

    private static List<ImportFieldDecision> AnalyzeFields(List<ImportSessionDecision> sessions,
        List<ServerLocation> current, List<ServerLocation> incoming, HashSet<Guid> entryPoints)
    {
        var result = new List<ImportFieldDecision>();
        foreach (var row in sessions.Where(x => x.CurrentId is not null))
        {
            var before = current.First(x => x.Server.Id == row.CurrentId).Server;
            var after = incoming.First(x => x.Server.Id == row.IncomingId).Server;
            // По умолчанию берём всё из файла, кроме сессий-точек входа (их ломать опасно).
            var takeByDefault = !entryPoints.Contains(row.CurrentId!.Value);
            Add("Name", "Название", before.Name, after.Name);
            Add("Host", "Хост", before.Host, after.Host);
            Add("Port", "Порт", before.Port.ToString(), after.Port.ToString());
            Add("Username", "Логин", before.Username, after.Username);
            Add("Password", "Пароль", before.Password, after.Password, true);
            Add("PrivateKeyPath", "Путь к ключу", before.PrivateKeyPath, after.PrivateKeyPath, keepCurrent: true);
            Add("PrivateKeyPassphrase", "Пароль ключа", before.PrivateKeyPassphrase, after.PrivateKeyPassphrase, true);
            Add("UseKeyboardInteractive", "Keyboard-interactive", before.UseKeyboardInteractive.ToString(), after.UseKeyboardInteractive.ToString());
            Add("RootLogin", "Логин повышения прав", before.RootLogin, after.RootLogin);
            Add("RootPassword", "Пароль повышения прав", before.RootPassword, after.RootPassword, true);
            Add("ShellPrompt", "Shell prompt", before.ShellPrompt, after.ShellPrompt);
            Add("ImportedCommand", "Команда входа", before.ImportedCommand, after.ImportedCommand);
            Add("IgnoreImportedCommand", "Игнорировать команду входа", before.IgnoreImportedCommand.ToString(), after.IgnoreImportedCommand.ToString());
            Add("HostKeyFingerprint", "Host key", before.HostKeyFingerprint, after.HostKeyFingerprint);
            Add("RequiredPreviousServerId", "Обязательная предыдущая сессия", before.RequiredPreviousServerId?.ToString() ?? "не задана", after.RequiredPreviousServerId?.ToString() ?? "не задана");
            Add("TryDirectWithoutJumphost", "Пробовать без JH", before.TryDirectWithoutJumphost.ToString(), after.TryDirectWithoutJumphost.ToString());
            Add("PreferredProxyId", "Предпочтительная JH", before.PreferredProxyId?.ToString() ?? "не задана", after.PreferredProxyId?.ToString() ?? "не задана");
            Add("BackupEndpoints", "Резервные адреса", EndpointSummary(before.BackupEndpoints), EndpointSummary(after.BackupEndpoints));
            Add("WebInterfaces", "Веб-интерфейсы", WebSummary(before.WebInterfaces), WebSummary(after.WebInterfaces));

            void Add(string property, string label, string oldValue, string newValue, bool secret = false, bool keepCurrent = false)
            {
                if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) return;
                result.Add(new ImportFieldDecision
                {
                    IncomingSessionId = row.IncomingId, SessionName = row.IncomingName,
                    PropertyName = property, Label = label,
                    CurrentValue = secret ? SecretState(oldValue) : oldValue,
                    IncomingValue = secret ? SecretState(newValue) : newValue,
                    UseIncoming = keepCurrent ? false : takeByDefault
                });
            }
        }
        return result;
    }

    private static List<ImportLinkDecision> AnalyzeLinks(ManagerConfig current, ManagerConfig incoming,
        List<ImportSessionDecision> sessions)
    {
        var incomingServers = incoming.AllServers().ToDictionary(x => x.Id);
        var mapped = sessions.Where(x => x.CurrentId is not null &&
                                         x.Decision is ImportDecision.KeepCurrent or ImportDecision.UseIncoming)
            .ToDictionary(x => x.IncomingId, x => x.CurrentId!.Value);
        var result = new List<ImportLinkDecision>();
        for (var index = 0; index < incoming.Links.Count; index++)
        {
            var link = incoming.Links[index];
            var endpointsKnown = sessions.Any(x => x.IncomingId == link.FromServerId && x.Decision != ImportDecision.Skip) &&
                                 sessions.Any(x => x.IncomingId == link.ToServerId && x.Decision != ImportDecision.Skip);
            ServerLink? existing = null;
            if (mapped.TryGetValue(link.FromServerId, out var from) && mapped.TryGetValue(link.ToServerId, out var to))
                existing = current.Links.FirstOrDefault(x => x.FromServerId == from && x.ToServerId == to);
            var conflict = existing is not null;
            result.Add(new ImportLinkDecision
            {
                IncomingIndex = index,
                FromName = incomingServers.GetValueOrDefault(link.FromServerId)?.Name ?? link.FromServerId.ToString(),
                ToName = incomingServers.GetValueOrDefault(link.ToServerId)?.Name ?? link.ToServerId.ToString(),
                ExistingConflict = conflict,
                Reason = !endpointsKnown ? "Сначала однозначно сопоставьте обе сессии" :
                    conflict ? LinkDifferenceSummary(existing!, link) : "Новая связь",
                Decision = !endpointsKnown ? ImportDecision.Skip : conflict ? ImportDecision.KeepCurrent : ImportDecision.Add
            });
        }
        return result;
    }

    private static string LinkDifferenceSummary(ServerLink current, ServerLink incoming)
    {
        var fields = new List<string>();
        if (current.Discovered != incoming.Discovered) fields.Add("тип обнаружения");
        if (!string.Equals(current.LastStrategy, incoming.LastStrategy, StringComparison.Ordinal)) fields.Add("стратегия");
        if (current.LastSuccessfulProxyId != incoming.LastSuccessfulProxyId) fields.Add("успешная JH");
        if (current.LastLatencyMs != incoming.LastLatencyMs) fields.Add("задержка");
        if (current.LastSuccessUtc != incoming.LastSuccessUtc) fields.Add("время проверки");
        if (current.ProxyStatistics.Count != incoming.ProxyStatistics.Count) fields.Add("статистика JH");
        return fields.Count == 0 ? "Связь уже существует с теми же параметрами" :
            "Конфликт связи: отличаются " + string.Join(", ", fields);
    }

    private static string SecretState(string value) => string.IsNullOrEmpty(value) ? "не задан" : "задан";
    private static string EndpointSummary(IEnumerable<ServerEndpoint> values) =>
        string.Join(", ", values.Select(x => $"{x.Host}:{x.Port}"));
    private static string WebSummary(IEnumerable<WebInterface> values) => string.Join(", ",
        values.Select(x => $"{x.Name}: {x.Url} (учётные данные: {(x.Username.Length > 0 || x.Password.Length > 0 ? "заданы" : "не заданы")})"));

    private static void ApplySelectedFields(ManagedServer target, ManagedServer source,
        IEnumerable<ImportFieldDecision> fields)
    {
        foreach (var field in fields)
            switch (field.PropertyName)
            {
                case "Name": target.Name = source.Name; break;
                case "Host": target.Host = source.Host; break;
                case "Port": target.Port = source.Port; break;
                case "Username": target.Username = source.Username; break;
                case "Password": target.Password = source.Password; break;
                case "PrivateKeyPath": target.PrivateKeyPath = source.PrivateKeyPath; break;
                case "PrivateKeyPassphrase": target.PrivateKeyPassphrase = source.PrivateKeyPassphrase; break;
                case "UseKeyboardInteractive": target.UseKeyboardInteractive = source.UseKeyboardInteractive; break;
                case "RootLogin": target.RootLogin = source.RootLogin; break;
                case "RootPassword": target.RootPassword = source.RootPassword; break;
                case "ShellPrompt": target.ShellPrompt = source.ShellPrompt; break;
                case "ImportedCommand": target.ImportedCommand = source.ImportedCommand; break;
                case "IgnoreImportedCommand": target.IgnoreImportedCommand = source.IgnoreImportedCommand; break;
                case "HostKeyFingerprint":
                    target.HostKeyFingerprint = source.HostKeyFingerprint;
                    target.HostKeyAlgorithm = source.HostKeyAlgorithm;
                    target.HostKeyBits = source.HostKeyBits;
                    break;
                case "RequiredPreviousServerId": target.RequiredPreviousServerId = source.RequiredPreviousServerId; break;
                case "TryDirectWithoutJumphost": target.TryDirectWithoutJumphost = source.TryDirectWithoutJumphost; break;
                case "PreferredProxyId": target.PreferredProxyId = source.PreferredProxyId; break;
                case "BackupEndpoints": target.BackupEndpoints = Clone(source.BackupEndpoints); break;
                case "WebInterfaces": target.WebInterfaces = Clone(source.WebInterfaces); break;
            }
    }

    private static List<ImportGroupSuggestion> AnalyzeGroups(
        ManagerConfig current, ManagerConfig incoming, List<ImportSessionDecision> sessions,
        List<ServerLocation> currentLocations, List<ServerLocation> incomingLocations)
    {
        var result = new List<ImportGroupSuggestion>();
        foreach (var incomingGroup in FlattenGroups(incoming.Groups))
        {
            var incomingIds = DescendantIds(incomingGroup.Group).ToHashSet();
            var mappedCurrentIds = sessions.Where(x => incomingIds.Contains(x.IncomingId) && x.CurrentId is not null)
                .Select(x => x.CurrentId!.Value).ToHashSet();
            var best = FlattenGroups(current.Groups)
                .Select(x => (x, count: DescendantIds(x.Group).Count(mappedCurrentIds.Contains)))
                .OrderByDescending(x => x.count).ThenBy(x => x.x.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            var samePath = FlattenGroups(current.Groups).FirstOrDefault(x =>
                string.Equals(x.Path, incomingGroup.Path, StringComparison.OrdinalIgnoreCase));
            var incomingParent = FindParent(incoming.Groups, incomingGroup.Group.Id);
            var parentWillBeCreated = incomingParent is not null && result.Any(x =>
                x.IncomingId == incomingParent.Id && x.Decision == ImportGroupDecision.CreateNew);
            var choice = parentWillBeCreated
                ? default
                : best.count > 0 && best.count == mappedCurrentIds.Count ? best.x : samePath;
            result.Add(new ImportGroupSuggestion
            {
                IncomingId = incomingGroup.Group.Id, IncomingPath = incomingGroup.Path,
                CurrentId = choice.Group?.Id, CurrentPath = choice.Path ?? "",
                MatchedSessions = best.count, IncomingSessions = incomingIds.Count,
                Decision = choice.Group is null
                    ? ImportGroupDecision.CreateNew
                    : ImportGroupDecision.MergeKeepCurrentName
            });
        }
        return result;
    }

    private static Dictionary<Guid, Guid> BuildGroupMap(
        ManagerConfig result, ManagerConfig incoming, IEnumerable<ImportGroupSuggestion> decisions)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var row in decisions)
        {
            if (row.Decision != ImportGroupDecision.CreateNew && row.CurrentId is Guid existing && FindGroup(result.Groups, existing) is { } group)
            {
                if (row.Decision == ImportGroupDecision.MergeUseIncomingName)
                    group.Name = FindGroup(incoming.Groups, row.IncomingId)?.Name ?? group.Name;
                map[row.IncomingId] = existing;
            }
        }
        return map;
    }

    private static void AddToMappedGroup(ManagerConfig result, ManagerConfig incoming, Guid incomingServerId,
        ManagedServer server, Dictionary<Guid, Guid> groupMap)
    {
        var location = Locations(incoming).First(x => x.Server.Id == incomingServerId);
        if (location.GroupId is null) { result.UngroupedServers.Add(server); return; }
        if (!groupMap.TryGetValue(location.GroupId.Value, out var targetId))
        {
            targetId = EnsureTargetGroup(result, incoming, location.GroupId.Value, groupMap);
        }
        FindGroup(result.Groups, targetId)!.Servers.Add(server);
    }

    private static void MoveToMappedGroup(ManagerConfig result, ManagerConfig incoming, Guid incomingServerId,
        Guid currentServerId, Dictionary<Guid, Guid> groupMap)
    {
        var server = result.FindServer(currentServerId)!;
        result.UngroupedServers.RemoveAll(x => x.Id == currentServerId);
        foreach (var group in FlattenGroups(result.Groups).Select(x => x.Group))
            group.Servers.RemoveAll(x => x.Id == currentServerId);
        AddToMappedGroup(result, incoming, incomingServerId, server, groupMap);
    }

    private static Guid EnsureTargetGroup(ManagerConfig result, ManagerConfig incoming, Guid incomingId,
        Dictionary<Guid, Guid> groupMap)
    {
        if (groupMap.TryGetValue(incomingId, out var mapped)) return mapped;
        var source = FindGroup(incoming.Groups, incomingId)
            ?? throw new InvalidDataException("Группа импортируемой сессии не найдена.");
        var parent = FindParent(incoming.Groups, incomingId);
        List<ServerGroup> targetCollection;
        if (parent is null) targetCollection = result.Groups;
        else
        {
            var targetParentId = EnsureTargetGroup(result, incoming, parent.Id, groupMap);
            targetCollection = FindGroup(result.Groups, targetParentId)!.Groups;
        }
        var created = new ServerGroup
        {
            Id = Guid.NewGuid(), Name = UniqueGroupName(targetCollection, source.Name)
        };
        targetCollection.Add(created);
        groupMap[incomingId] = created.Id;
        return created.Id;
    }

    private static void ReplaceServerInPlace(ManagerConfig config, Guid id, ManagedServer replacement)
    {
        foreach (var group in FlattenGroups(config.Groups).Select(x => x.Group))
        {
            var index = group.Servers.FindIndex(x => x.Id == id);
            if (index >= 0) { group.Servers[index] = replacement; return; }
        }
        var ungrouped = config.UngroupedServers.FindIndex(x => x.Id == id);
        if (ungrouped >= 0) config.UngroupedServers[ungrouped] = replacement;
    }

    private static void ApplyLinkDecisions(ManagerConfig result, ManagerConfig incoming,
        IEnumerable<ImportLinkDecision> decisions, Dictionary<Guid, Guid> servers, Dictionary<Guid, Guid> proxies)
    {
        foreach (var decision in decisions)
        {
            if (decision.Decision is ImportDecision.Skip or ImportDecision.KeepCurrent ||
                decision.IncomingIndex < 0 || decision.IncomingIndex >= incoming.Links.Count) continue;
            var source = incoming.Links[decision.IncomingIndex];
            if (!servers.TryGetValue(source.FromServerId, out var from) || !servers.TryGetValue(source.ToServerId, out var to)) continue;
            var link = Clone(source); link.FromServerId = from; link.ToServerId = to;
            if (link.LastSuccessfulProxyId is Guid proxy)
                link.LastSuccessfulProxyId = proxies.TryGetValue(proxy, out var mappedProxy) ? mappedProxy : null;
            link.ProxyStatistics = link.ProxyStatistics.Where(x => proxies.ContainsKey(x.ProxyId))
                .Select(x => { var copy = Clone(x); copy.ProxyId = proxies[x.ProxyId]; return copy; }).ToList();
            var existing = result.Links.FindIndex(x => x.FromServerId == from && x.ToServerId == to);
            if (existing >= 0)
            {
                if (decision.Decision == ImportDecision.UseIncoming) result.Links[existing] = link;
            }
            else if (decision.Decision is ImportDecision.Add or ImportDecision.UseIncoming) result.Links.Add(link);
        }
    }

    private static void RemapReferences(ManagerConfig result, Dictionary<Guid, Guid> servers,
        Dictionary<Guid, Guid> proxies, HashSet<Guid> importedServerIds, HashSet<Guid> importedProxyIds,
        Dictionary<Guid, HashSet<string>> importedFields)
    {
        foreach (var server in result.AllServers())
        {
            bool Imported(string field) => importedServerIds.Contains(server.Id) ||
                importedFields.TryGetValue(server.Id, out var fields) && fields.Contains(field);
            if (Imported(nameof(server.RequiredPreviousServerId)) && server.RequiredPreviousServerId is Guid previous)
                server.RequiredPreviousServerId = servers.TryGetValue(previous, out var mapped) ? mapped : null;
            if (Imported(nameof(server.PreferredProxyId)) && server.PreferredProxyId is Guid proxy)
                server.PreferredProxyId = proxy == Guid.Empty ? Guid.Empty :
                    proxies.TryGetValue(proxy, out var mappedProxy) ? mappedProxy : null;
            if (Imported(nameof(server.EndpointPreferences)))
            {
                server.EndpointPreferences = server.EndpointPreferences.Where(item =>
                    (item.PreviousServerId is null || servers.ContainsKey(item.PreviousServerId.Value)) &&
                    (item.ProxyId is null || item.ProxyId == Guid.Empty || proxies.ContainsKey(item.ProxyId.Value))).ToList();
                foreach (var item in server.EndpointPreferences)
                {
                    if (item.PreviousServerId is Guid previousId) item.PreviousServerId = servers[previousId];
                    if (item.ProxyId is Guid proxyId && proxyId != Guid.Empty) item.ProxyId = proxies[proxyId];
                }
            }
        }
        foreach (var proxy in result.BaseProxies.Where(x => importedProxyIds.Contains(x.Id)))
        {
            if (proxy.StartupServerId is Guid startup)
                proxy.StartupServerId = servers.TryGetValue(startup, out var mapped) ? mapped : null;
            proxy.AccessProbeServerIds = proxy.AccessProbeServerIds.Where(servers.ContainsKey).Select(x => servers[x]).Distinct().ToList();
        }
    }

    private static void ApplyPreferredRoutes(
        ManagerConfig current, ManagerConfig incoming, ManagerConfig result,
        IEnumerable<ImportSessionDecision> sessions,
        Dictionary<Guid, Guid> serverMap, Dictionary<Guid, Guid> proxyMap)
    {
        var currentServers = current.AllServers().ToDictionary(x => x.Id);
        var incomingServers = incoming.AllServers().ToDictionary(x => x.Id);
        var resultServers = result.AllServers().ToDictionary(x => x.Id);
        var resultProxies = result.BaseProxies.ToDictionary(x => x.Id);

        foreach (var row in sessions)
        {
            if (row.Decision is ImportDecision.Skip) continue;
            if (!serverMap.TryGetValue(row.IncomingId, out var targetId) ||
                !resultServers.TryGetValue(targetId, out var targetServer))
                continue;

            ManagedServer? currentServer = null;
            if (row.CurrentId is Guid currId && row.Decision != ImportDecision.Add)
                currentServers.TryGetValue(currId, out currentServer);

            if (!incomingServers.TryGetValue(row.IncomingId, out var incomingServer))
                continue;

            var incomingRoute = incomingServer.PreferredRoute;
            CachedRoute? mappedIncomingRoute = null;
            if (incomingRoute is not null && incomingRoute.ServerIds.Count > 0)
            {
                var mappedProxyId = incomingRoute.ProxyId == Guid.Empty
                    ? Guid.Empty
                    : proxyMap.TryGetValue(incomingRoute.ProxyId, out var mpId)
                        ? mpId
                        : resultProxies.ContainsKey(incomingRoute.ProxyId)
                            ? incomingRoute.ProxyId
                            : (Guid?)null;

                if (mappedProxyId is not null)
                {
                    var mappedServerIds = incomingRoute.ServerIds
                        .Select(id => serverMap.TryGetValue(id, out var sid) ? sid : id)
                        .ToList();

                    if (mappedServerIds.Count > 0 &&
                        mappedServerIds[^1] == targetId &&
                        mappedServerIds.All(resultServers.ContainsKey))
                    {
                        mappedIncomingRoute = new CachedRoute
                        {
                            ProxyId = mappedProxyId.Value,
                            ServerIds = mappedServerIds,
                            LatencyMs = incomingRoute.LatencyMs,
                            LastSuccessUtc = incomingRoute.LastSuccessUtc
                        };
                    }
                }
            }

            var currentRoute = currentServer?.PreferredRoute;

            if (mappedIncomingRoute is not null)
            {
                if (currentRoute is null || mappedIncomingRoute.ServerIds.Count < currentRoute.ServerIds.Count)
                    targetServer.PreferredRoute = mappedIncomingRoute;
                else
                    targetServer.PreferredRoute = Clone(currentRoute);
            }
            else if (currentRoute is not null)
            {
                targetServer.PreferredRoute = Clone(currentRoute);
            }
            else
            {
                targetServer.PreferredRoute = null;
            }
        }
    }

    private static List<ServerLocation> Locations(ManagerConfig config)
    {
        var result = config.UngroupedServers.Select(x => new ServerLocation(x, null, "Без группы")).ToList();
        void Walk(IEnumerable<ServerGroup> groups, string parent)
        {
            foreach (var group in groups)
            {
                var path = parent.Length == 0 ? group.Name : $"{parent} / {group.Name}";
                result.AddRange(group.Servers.Select(x => new ServerLocation(x, group.Id, path)));
                Walk(group.Groups, path);
            }
        }
        Walk(config.Groups, ""); return result;
    }

    private static IEnumerable<(ServerGroup Group, string Path)> FlattenGroups(IEnumerable<ServerGroup> groups, string parent = "")
    {
        foreach (var group in groups)
        {
            var path = parent.Length == 0 ? group.Name : $"{parent} / {group.Name}";
            yield return (group, path);
            foreach (var child in FlattenGroups(group.Groups, path)) yield return child;
        }
    }

    private static IEnumerable<Guid> DescendantIds(ServerGroup group) =>
        group.Servers.Select(x => x.Id).Concat(group.Groups.SelectMany(DescendantIds));
    private static ServerGroup? FindGroup(IEnumerable<ServerGroup> groups, Guid id) =>
        groups.SelectMany(x => new[] { x }.Concat(FlattenGroups(x.Groups).Select(y => y.Group))).FirstOrDefault(x => x.Id == id);
    private static ServerGroup? FindParent(IEnumerable<ServerGroup> groups, Guid childId)
    {
        foreach (var group in groups)
        {
            if (group.Groups.Any(x => x.Id == childId)) return group;
            var nested = FindParent(group.Groups, childId); if (nested is not null) return nested;
        }
        return null;
    }
    private static string UniqueGroupName(IEnumerable<ServerGroup> groups, string wanted)
    {
        var names = groups.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(wanted)) return wanted;
        for (var i = 2; ; i++) if (!names.Contains($"{wanted} ({i})")) return $"{wanted} ({i})";
    }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
    private sealed record ServerLocation(ManagedServer Server, Guid? GroupId, string Path);
    private static T? SingleOrDefaultSafe<T>(this IEnumerable<T> source, Func<T, bool> predicate) where T : class
    {
        using var e = source.Where(predicate).Take(2).GetEnumerator();
        if (!e.MoveNext()) return null; var value = e.Current; return e.MoveNext() ? null : value;
    }
}
