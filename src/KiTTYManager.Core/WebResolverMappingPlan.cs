using System.Net;

namespace KiTTYManager.Core;

public static class WebResolverMappingPlan
{
    public static IReadOnlyList<KeyValuePair<string, string>> Build(ManagedServer server)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var web in server.WebInterfaces)
        {
            if (!Uri.TryCreate(web.Url, UriKind.Absolute, out var uri) ||
                IPAddress.TryParse(uri.Host, out _)) continue;
            var address = string.IsNullOrWhiteSpace(web.ResolverAddress)
                ? server.CleanHost
                : web.ResolverAddress.Trim();
            if (!IPAddress.TryParse(address, out _))
                throw new InvalidOperationException(
                    $"Для веб-интерфейса «{web.Name}» укажите IP в поле «Резолвить домен по адресу». " +
                    $"Адрес сервера «{server.CleanHost}» также не является IP.");
            result.Add(new(uri.DnsSafeHost, address));
        }
        return result;
    }
}
