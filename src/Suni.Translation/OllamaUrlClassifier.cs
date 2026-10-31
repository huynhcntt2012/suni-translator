using System.Net;
using System.Net.Sockets;
using Suni.Core.Domain;

namespace Suni.Translation;

/// <summary>Phân loại endpoint Ollama: PC này / LAN nội bộ / khác (SRS 5.3).</summary>
public static class OllamaUrlClassifier
{
    public static bool TryParse(string? input, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(input))
            return false;
        if (!Uri.TryCreate(input.Trim().TrimEnd('/'), UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        uri = parsed;
        return true;
    }

    public static OllamaEndpointKind Classify(Uri uri)
    {
        var host = uri.IdnHost;
        if (uri.IsLoopback || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return OllamaEndpointKind.Local;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            !host.Contains('.') && uri.HostNameType == UriHostNameType.Dns)
            return OllamaEndpointKind.Lan;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsPrivate(ip))
            return OllamaEndpointKind.Lan;
        return OllamaEndpointKind.Other;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;

        var b = ip.GetAddressBytes();
        return b[0] == 10
            || b[0] == 172 && b[1] >= 16 && b[1] <= 31
            || b[0] == 192 && b[1] == 168
            || b[0] == 169 && b[1] == 254;
    }
}
