using Suni.Core.Domain;
using Suni.Core.Settings;

namespace Suni.Core.Abstractions;

public interface IPipelineOrchestrator
{
    Session? CurrentSession { get; }

    /// <summary>Mở phiên mới cho nguồn đã chọn. Nếu đang chạy phiên khác thì dừng phiên đó trước.</summary>
    Task<Session> StartAsync(AudioSource source, AppSettings settings, CancellationToken ct = default);

    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
}
