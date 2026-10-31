using System.Collections.Concurrent;

namespace Suni.Tts.Tests;

public class VoicePickerTests
{
    private static readonly TtsVoice[] Voices =
    [
        new("george", "en-GB", "Microsoft George"),
        new("zira", "en-US", "Microsoft Zira"),
        new("an", "vi-VN", "Microsoft An"),
        new("hanhan", "zh-TW", "Microsoft Hanhan"),
    ];

    [Theory]
    [InlineData("vi", "an")]
    [InlineData("en", "george")]     // giọng đầu tiên cùng ngôn ngữ
    [InlineData("en-US", "zira")]    // khớp đầy đủ trước
    [InlineData("zh", "hanhan")]
    public void PicksByTargetLanguage(string lang, string expected) =>
        Assert.Equal(expected, VoicePicker.Pick(Voices, null, lang)!.Id);

    [Fact]
    public void PreferredVoice_WinsIfInstalled() => Assert.Equal("zira", VoicePicker.Pick(Voices, "zira", "vi")!.Id);

    [Fact]
    public void PreferredVoiceRemoved_FallsBackToLanguage() => Assert.Equal("an", VoicePicker.Pick(Voices, "gone", "vi")!.Id);

    [Fact]
    public void NoVoiceForLanguage_ReturnsNull() => Assert.Null(VoicePicker.Pick(Voices, null, "ja"));
}

public class EchoGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Press Start.")]
    [InlineData("press start")]          // ASR khác hoa/thường, dấu câu
    [InlineData("Press, start!")]
    public void RecentlySpokenText_IsEcho(string heard)
    {
        var guard = new EchoGuard();
        guard.Spoken("Press Start.", T0);
        Assert.True(guard.IsEcho(heard, T0.AddSeconds(3)));
    }

    [Fact]
    public void PartOfLongSpokenSentence_IsEcho()
    {
        var guard = new EchoGuard();
        guard.Spoken("The translated subtitles will appear on top of the video.", T0);
        Assert.True(guard.IsEcho("will appear on top of the video", T0.AddSeconds(4))); // ASR chỉ bắt được một phần
    }

    [Fact]
    public void OldOrDifferentText_IsNotEcho()
    {
        var guard = new EchoGuard();
        guard.Spoken("Press Start.", T0);
        Assert.False(guard.IsEcho("Press Start.", T0.AddSeconds(20)));   // quá cửa sổ thời gian
        Assert.False(guard.IsEcho("Open the settings.", T0.AddSeconds(2)));
        Assert.False(guard.IsEcho("Press", T0.AddSeconds(2)));            // mảnh quá ngắn không tính là trùng
        Assert.False(guard.IsEcho("", T0));
    }
}

public class TtsQueueTests
{
    /// <summary>Engine giả: mỗi câu "đọc" mất một khoảng; ghi lại câu đã đọc.</summary>
    private sealed class FakeEngine(TimeSpan duration) : ITtsEngine
    {
        public ConcurrentQueue<string> Spoken { get; } = new();
        public TaskCompletionSource FirstStarted { get; } = new();

        public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TtsVoice>>([]);
        public Task<IReadOnlyList<TtsOutputDevice>> ListOutputDevicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TtsOutputDevice>>([]);

        public async Task SpeakAsync(string text, TtsOptions options, CancellationToken ct = default)
        {
            FirstStarted.TrySetResult();
            if (text == "boom")
                throw new InvalidOperationException("engine lỗi");
            await Task.Delay(duration, ct);
            Spoken.Enqueue(text);
        }
    }

    private static TtsItem Item(string text) => new(text, text, new TtsOptions(null, null));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
    }

    [Fact]
    public async Task SpeaksInOrder()
    {
        var engine = new FakeEngine(TimeSpan.FromMilliseconds(20));
        await using var queue = new TtsQueue(engine, maxPending: 5);

        foreach (var t in new[] { "a", "b", "c" })
            queue.Enqueue(Item(t));
        await WaitUntil(() => engine.Spoken.Count == 3);

        Assert.Equal(["a", "b", "c"], engine.Spoken);
    }

    [Fact]
    public async Task WhenBehind_SkipsOldestWaiting_KeepsUpWithLatest()
    {
        var engine = new FakeEngine(TimeSpan.FromMilliseconds(150));
        await using var queue = new TtsQueue(engine, maxPending: 2);
        var skipped = new ConcurrentQueue<string>();
        queue.Skipped += i => skipped.Enqueue(i.Text);

        queue.Enqueue(Item("1"));
        await engine.FirstStarted.Task;           // "1" đang đọc
        foreach (var t in new[] { "2", "3", "4", "5" })
            queue.Enqueue(Item(t));               // chỉ giữ 2 câu chờ mới nhất
        await WaitUntil(() => engine.Spoken.Count == 3);

        Assert.Equal(["1", "4", "5"], engine.Spoken);
        Assert.Equal(["2", "3"], skipped);
    }

    [Fact]
    public async Task Clear_StopsCurrentAndDropsWaiting()
    {
        var engine = new FakeEngine(TimeSpan.FromSeconds(5));
        await using var queue = new TtsQueue(engine, maxPending: 3);
        var finished = new TaskCompletionSource();
        queue.Finished += _ => finished.TrySetResult();

        queue.Enqueue(Item("long"));
        queue.Enqueue(Item("next"));
        await engine.FirstStarted.Task;
        queue.Clear();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); // dừng ngay, không chờ 5 s

        await Task.Delay(100);
        Assert.Empty(engine.Spoken);
        Assert.Equal(0, queue.Pending);
    }

    [Fact]
    public async Task EngineError_IsReported_AndQueueContinues()
    {
        var engine = new FakeEngine(TimeSpan.FromMilliseconds(10));
        await using var queue = new TtsQueue(engine, maxPending: 5);
        Exception? error = null;
        queue.Failed += (_, ex) => error = ex;

        queue.Enqueue(Item("boom"));
        queue.Enqueue(Item("ok"));
        await WaitUntil(() => engine.Spoken.Count == 1);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(["ok"], engine.Spoken);
    }
}

/// <summary>Giọng Windows thật (offline). Âm lượng 0 để không phát ra loa khi chạy test.</summary>
public class WindowsTtsEngineTests
{
    [Fact]
    public async Task ListsInstalledVoicesAndDevices()
    {
        using var engine = new WindowsTtsEngine();
        var voices = await engine.ListVoicesAsync();
        var devices = await engine.ListOutputDevicesAsync();

        Assert.NotEmpty(voices);
        Assert.All(voices, v => Assert.False(string.IsNullOrEmpty(v.Language)));
        Assert.NotEmpty(devices);
        Assert.Single(devices, d => d.IsDefault);
    }

    [Fact]
    public async Task SpeaksToCompletion_AndCanBeCancelled()
    {
        using var engine = new WindowsTtsEngine();
        var voice = (await engine.ListVoicesAsync())[0];
        var options = new TtsOptions(voice.Id, null, Rate: 2, Volume: 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await engine.SpeakAsync("Test.", options).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(sw.ElapsedMilliseconds > 100, "phải thực sự phát rồi mới hoàn tất");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        sw.Restart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.SpeakAsync("This is a much longer sentence that would take several seconds to read aloud.", options with { Rate = 1 }, cts.Token));
        Assert.True(sw.ElapsedMilliseconds < 3000, $"huỷ phải dừng ngay, mất {sw.ElapsedMilliseconds} ms");
    }
}
