using Suni.Core.Settings;

namespace Suni.App.Services;

/// <summary>
/// Nguồn cài đặt duy nhất trong app: mọi nơi đọc <see cref="Current"/> và sửa qua <see cref="UpdateAsync"/>,
/// tránh trường hợp hai màn hình giữ hai bản sao rồi ghi đè lên nhau.
/// </summary>
public sealed class SettingsService(ISettingsStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public event Action<AppSettings>? Changed;

    public async Task LoadAsync()
    {
        Current = await store.LoadAsync();
        Changed?.Invoke(Current);
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> change)
    {
        await _gate.WaitAsync();
        try
        {
            Current = change(Current);
            await store.SaveAsync(Current);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(Current);
    }
}
