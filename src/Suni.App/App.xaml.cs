using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Suni.App.Hotkeys;
using Suni.App.Overlay;
using Suni.App.Tray;
using Suni.App.Services;
using Suni.App.ViewModels;
using Suni.Audio;
using Suni.Core.Abstractions;
using Suni.Core.Settings;
using Suni.Pipeline;
using Suni.Storage;
using Suni.Storage.Exporters;
using Suni.Translation;

namespace Suni.App;

/// <summary>Composition root: mọi module được nối với nhau qua DI ở đây.</summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>Đang thoát thật (từ menu khay hoặc đóng cửa sổ khi không dịch) — không thu nhỏ xuống khay nữa.</summary>
    public static bool IsExiting { get; set; }

    /// <summary>Thông báo cần hiện khi mở app (vd. đã khôi phục DB lịch sử bị hỏng).</summary>
    public static string? StartupNotice { get; private set; }

    // Chỉ một bản chạy: hai bản cùng lúc tranh phím tắt, icon khay, file cài đặt, và khoá file khi đóng gói lại.
    private const string InstanceName = @"Local\SuniAiLiveTranslator";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!isFirst)
        {
            _showSignal.Set(); // bản đang chạy (có thể đang ẩn ở khay) tự hiện cửa sổ
            Shutdown();
            return;
        }

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IEventBus, InMemoryEventBus>();
                services.AddSingleton<IAudioSourceEnumerator, AudioSourceEnumerator>();
                services.AddSingleton<IAudioCaptureFactory, AudioCaptureFactory>();
                services.AddSingleton<ISpeechDetectorFactory, SileroSpeechDetectorFactory>();
                services.AddSingleton(_ => new WhisperWorkerProvider(() => AppPaths.AsrWorkerPath, () => AppPaths.AsrModelDirectory));
                services.AddSingleton<IAsrEngineProvider>(sp => sp.GetRequiredService<WhisperWorkerProvider>());
                services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(JsonSettingsStore.DefaultPath));
                services.AddSingleton<SettingsService>();
                // Overlay: ViewModel tạo khi mở cửa sổ (Func để tránh vòng phụ thuộc với MainViewModel).
                services.AddTransient<OverlayViewModel>();
                services.AddSingleton<Func<OverlayViewModel>>(sp => () => sp.GetRequiredService<OverlayViewModel>());
                services.AddSingleton<OverlayService>();
                services.AddSingleton<HotkeyService>();
                services.AddSingleton<TtsService>();
                services.AddSingleton<TrayService>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<SettingsWindow>();
                services.AddSingleton<Func<SettingsWindow>>(sp => () => sp.GetRequiredService<SettingsWindow>());
                // Một HttpClient cho mọi kết nối Ollama; chế độ offline chặn host ngoài máy/LAN trước khi gửi.
                services.AddSingleton(_ => new HttpClient(new OfflineStrictHandler
                {
                    InnerHandler = new SocketsHttpHandler
                    {
                        ConnectTimeout = TimeSpan.FromSeconds(3),
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    },
                })
                {
                    Timeout = Timeout.InfiniteTimeSpan, // timeout theo từng yêu cầu do engine dịch quản lý
                });
                services.AddSingleton<ITranslationEngineFactory, OllamaTranslationEngineFactory>();
                services.AddSingleton<PipelineOrchestrator>();
                services.AddSingleton<IPipelineOrchestrator>(sp => sp.GetRequiredService<PipelineOrchestrator>());
                // Lịch sử SQLite: một file DB, ghi theo lô từ sự kiện pipeline.
                services.AddSingleton(_ => new HistoryStore(SuniDatabase.ForFile(SuniDatabase.DefaultPath)));
                services.AddSingleton<HistoryRecorder>();
                services.AddSingleton<ISubtitleExporter>(new SrtExporter());
                services.AddSingleton<ISubtitleExporter, TxtExporter>();
                services.AddSingleton<ISubtitleExporter, JsonExporter>();
                services.AddSingleton<ExportService>();
                services.AddTransient<HistoryViewModel>();
                services.AddTransient<HistoryWindow>();
                services.AddSingleton<Func<HistoryWindow>>(sp => () => sp.GetRequiredService<HistoryWindow>());
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        // DB hỏng → cứu dữ liệu sang DB mới (giữ bản hỏng), rồi tạo schema/migration.
        // Lỗi DB không chặn việc dùng app (chỉ mất lịch sử).
        try
        {
            if (await HistoryStore.RecoverIfCorruptAsync(SuniDatabase.DefaultPath) is { } r)
                StartupNotice = $"Lịch sử bị hỏng và đã được khôi phục: cứu được {r.SegmentsRecovered} câu trong " +
                                $"{r.SessionsRecovered + r.OrphanSessionsCreated} phiên. Bản hỏng được giữ tại {r.CorruptCopyPath}.";
            await _host.Services.GetRequiredService<HistoryStore>().InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Không mở được DB lịch sử: {ex}");
        }
        _ = _host.Services.GetRequiredService<HistoryRecorder>(); // bắt đầu nghe sự kiện

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // Lần mở thứ hai báo hiệu → hiện lại cửa sổ của bản này.
        var tray = _host.Services.GetRequiredService<TrayService>();
        var signal = _showSignal;
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(tray.ShowMain);
        }) { IsBackground = true, Name = "Suni single-instance" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is { } host)
        {
            // Gỡ icon khỏi khay ngay trên luồng UI (luồng này sắp chặn ở Wait bên dưới, không thể Invoke ngược về).
            host.Services.GetRequiredService<TrayService>().Dispose();

            // Dừng phiên và tắt ASR worker trước khi thoát. Chạy đồng bộ có giới hạn: process sắp kết thúc.
            Task.Run(async () =>
            {
                await host.Services.GetRequiredService<PipelineOrchestrator>().DisposeAsync();
                await host.Services.GetRequiredService<HistoryRecorder>().DisposeAsync(); // ghi nốt lô cuối
                await host.Services.GetRequiredService<TtsService>().DisposeAsync();
                await host.Services.GetRequiredService<WhisperWorkerProvider>().DisposeAsync();
                await host.StopAsync(TimeSpan.FromSeconds(3));
            }).Wait(TimeSpan.FromSeconds(10));
            host.Dispose();
            _instanceMutex?.ReleaseMutex();
        }
        _instanceMutex?.Dispose();
        _showSignal?.Dispose();
        base.OnExit(e);
    }
}
