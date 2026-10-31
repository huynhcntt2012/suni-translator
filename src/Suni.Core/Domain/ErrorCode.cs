namespace Suni.Core.Domain;

/// <summary>Mã lỗi nghiệp vụ (SDD 2.4). Tên enum được lưu nguyên văn vào cột error_code.</summary>
public enum ErrorCode
{
    CapProcessNotFound,
    CapActivationFailed,
    CapUnsupportedOs,
    CapDeviceChanged,
    CapNoSignal,
    AsrWorkerCrashed,
    AsrModelMissing,
    AsrTimeout,
    AsrEmpty,
    LlmUnreachable,
    LlmTimeout,
    LlmModelNotFound,
    LlmBadOutput,
    PipeOverload,
    DbWriteFailed,
}

public static class ErrorCodeExtensions
{
    /// <summary>Lỗi tạm thời, có thể retry tự động.</summary>
    public static bool IsTransient(this ErrorCode code) =>
        code is ErrorCode.LlmUnreachable or ErrorCode.LlmTimeout or ErrorCode.AsrWorkerCrashed;
}
