using Suni.Core.Abstractions;
using Suni.Core.Settings;

namespace Suni.Pipeline;

/// <summary>
/// Cấp engine ASR đã nạp model cho cấu hình hiện tại. Giữ engine giữa các phiên (nạp model tốn 1–3 s),
/// chỉ tạo lại khi đổi model/thiết bị hoặc sau khi engine bị <see cref="Invalidate"/> (worker chết, huỷ giữa chừng).
/// </summary>
public interface IAsrEngineProvider
{
    Task<IAsrEngine> GetAsync(AsrSettings settings, CancellationToken ct = default);

    void Invalidate();
}
