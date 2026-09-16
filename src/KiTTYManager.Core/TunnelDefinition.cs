using Renci.SshNet;

namespace KiTTYManager.Core;

public enum TunnelKind
{
    Local,
    Remote,
    Dynamic
}

public sealed class TunnelDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ServerId { get; set; }
    public string ServerName { get; set; } = "";
    public TunnelKind Kind { get; set; } = TunnelKind.Local;
    public string BindHost { get; set; } = "127.0.0.1";
    public int BindPort { get; set; }
    public string DestinationHost { get; set; } = "";
    public int DestinationPort { get; set; }

    public string KindDisplay => Kind switch
    {
        TunnelKind.Local => "Local (-L)",
        TunnelKind.Remote => "Remote (-R)",
        TunnelKind.Dynamic => "Dynamic (-D / SOCKS)",
        _ => Kind.ToString()
    };

    public string Summary => Kind switch
    {
        TunnelKind.Dynamic => $"{BindHost}:{BindPort} (SOCKS)",
        TunnelKind.Remote => $"{BindHost}:{BindPort} → {DestinationHost}:{DestinationPort}",
        _ => $"{BindHost}:{BindPort} → {DestinationHost}:{DestinationPort}"
    };
}

public static class TunnelPolicy
{
    public static void Validate(TunnelDefinition tunnel)
    {
        ArgumentNullException.ThrowIfNull(tunnel);

        if (tunnel.ServerId == Guid.Empty)
            throw new InvalidDataException("Для туннеля должна быть выбрана сессия.");

        if (string.IsNullOrWhiteSpace(tunnel.BindHost))
            throw new InvalidDataException("Укажите адрес привязки туннеля (например, 127.0.0.1).");

        if (tunnel.BindPort is < 1 or > 65535)
            throw new InvalidDataException("Порт привязки туннеля должен быть в диапазоне 1–65535.");

        switch (tunnel.Kind)
        {
            case TunnelKind.Local or TunnelKind.Remote:
                if (string.IsNullOrWhiteSpace(tunnel.DestinationHost))
                    throw new InvalidDataException("Укажите адрес назначения для туннеля.");
                if (tunnel.DestinationPort is < 1 or > 65535)
                    throw new InvalidDataException("Порт назначения должен быть в диапазоне 1–65535.");
                break;

            case TunnelKind.Dynamic:
                // For Dynamic (SOCKS), destination is dynamic and determined by client requests.
                break;

            default:
                throw new InvalidDataException($"Неизвестный тип туннеля: {tunnel.Kind}.");
        }
    }
}

public static class TunnelPortFactory
{
    public static ForwardedPort Create(TunnelDefinition tunnel)
    {
        TunnelPolicy.Validate(tunnel);

        return tunnel.Kind switch
        {
            TunnelKind.Local => new ForwardedPortLocal(
                tunnel.BindHost.Trim(),
                checked((uint)tunnel.BindPort),
                tunnel.DestinationHost.Trim(),
                checked((uint)tunnel.DestinationPort)),

            TunnelKind.Remote => new ForwardedPortRemote(
                tunnel.BindHost.Trim(),
                checked((uint)tunnel.BindPort),
                tunnel.DestinationHost.Trim(),
                checked((uint)tunnel.DestinationPort)),

            TunnelKind.Dynamic => new ForwardedPortDynamic(
                tunnel.BindHost.Trim(),
                checked((uint)tunnel.BindPort)),

            _ => throw new InvalidOperationException($"Неподдерживаемый тип туннеля: {tunnel.Kind}")
        };
    }
}
