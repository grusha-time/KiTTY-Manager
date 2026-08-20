using System.Net.Sockets;

namespace KiTTYManager.Core;

public sealed class HostResolutionException : IOException
{
    public HostResolutionException(string host, Exception innerException)
        : base(ActionableErrorFormatter.FormatUnknownHost(host), innerException)
    {
        Host = host;
    }

    public string Host { get; }
}

/// <summary>Creates short user-facing error text while preserving technical exceptions for logs.</summary>
public static class ActionableErrorFormatter
{
    public static string FormatUnknownHost(string host) =>
        $"Windows не смогла найти хост «{host}». " +
        $"Проверьте запись для него в C:\\Windows\\System32\\drivers\\etc\\hosts " +
        "или подключение к нужному DNS/VPN. SSH-маршрут мог уже успешно построиться, " +
        "но Firefox не был запущен.";

    public static string Format(Exception exception)
    {
        foreach (var current in Enumerate(exception))
        {
            if (current is HostResolutionException hostResolution)
                return hostResolution.Message;
        }

        return exception.Message;
    }

    public static bool IsUnknownHost(SocketException exception) =>
        exception.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain;

    private static IEnumerable<Exception> Enumerate(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            yield return current;
    }
}
