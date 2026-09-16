namespace KiTTYManager.Core;

public static class WebFieldValueResolver
{
    public static string? Resolve(WebInterface? web, string? propertyName)
    {
        if (web is null || string.IsNullOrWhiteSpace(propertyName)) return null;

        return propertyName switch
        {
            nameof(WebInterface.Name) => web.Name,
            nameof(WebInterface.Url) => web.Url,
            nameof(WebInterface.ResolverAddress) => web.ResolverAddress,
            nameof(WebInterface.Username) => web.Username,
            nameof(WebInterface.Password) => web.Password,
            _ => null
        };
    }

    public static string ResolveForSort(WebInterface web, string? propertyName)
    {
        return Resolve(web, propertyName) ?? web.Name;
    }
}
