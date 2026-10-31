using Suni.Core.Domain;

namespace Suni.Translation;

/// <summary>
/// Chế độ offline nghiêm ngặt (SDD 9, TC-12): chặn mọi request HTTP tới host không phải máy này hoặc mạng nội bộ,
/// trước khi có bất kỳ gói tin nào rời máy.
/// </summary>
public sealed class OfflineStrictHandler(Func<bool> isEnabled) : DelegatingHandler
{
    public OfflineStrictHandler() : this(() => true) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (isEnabled() && request.RequestUri is { } uri && OllamaUrlClassifier.Classify(uri) == OllamaEndpointKind.Other)
            throw new OfflineModeViolationException(uri);
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class OfflineModeViolationException(Uri uri)
    : HttpRequestException($"Chế độ offline chặn kết nối tới {uri.Host} (không phải máy này hay mạng LAN).")
{
    public Uri Uri { get; } = uri;
}
