using Suni.Audio.Interop;

namespace Suni.Audio.Tests;

public class ProcessTreeTests
{
    // explorer(100) → chrome(200) → chrome utility(201) → chrome renderer(202)
    //               → suni(300) → asr worker(301)
    private static readonly ProcessTree Tree = new(
    [
        new(100, 4, "explorer.exe"),
        new(200, 100, "chrome.exe"),
        new(201, 200, "chrome.exe"),
        new(202, 201, "CHROME.EXE"),
        new(300, 100, "Suni.App.exe"),
        new(301, 300, "Suni.AsrWorker.exe"),
        new(400, 999, "orphan.exe"),       // cha đã thoát
        new(500, 501, "loop.exe"),         // vòng lặp do PID bị tái sử dụng
        new(501, 500, "loop.exe"),
    ]);

    [Theory]
    [InlineData(202, 200)]
    [InlineData(201, 200)]
    [InlineData(200, 200)]
    [InlineData(301, 301)] // khác tên exe → không gom vào cha
    [InlineData(400, 400)]
    [InlineData(12345, 12345)] // không có trong snapshot
    public void FindRoot_ClimbsWhileSameExeName(int pid, int expectedRoot) =>
        Assert.Equal(expectedRoot, Tree.FindRoot(pid));

    [Theory]
    [InlineData(202, 201)] // renderer → cha cùng exe (không leo tới ông 200: Windows chỉ gồm con trực tiếp)
    [InlineData(201, 200)]
    [InlineData(200, 200)] // cha là explorer → chính nó
    [InlineData(301, 301)]
    [InlineData(400, 400)]
    [InlineData(12345, 12345)]
    public void FindCaptureTarget_IsParentWithSameExe_AtMostOneLevel(int sessionPid, int expected) =>
        Assert.Equal(expected, Tree.FindCaptureTarget(sessionPid));

    [Fact]
    public void FindRoot_TerminatesOnCycle() => Assert.Contains(Tree.FindRoot(500), new[] { 500, 501 });

    [Theory]
    [InlineData(300, 300, true)]
    [InlineData(301, 300, true)]
    [InlineData(202, 300, false)]
    [InlineData(400, 300, false)]
    [InlineData(500, 300, false)]
    public void IsSelfOrDescendantOf(int pid, int ancestor, bool expected) =>
        Assert.Equal(expected, Tree.IsSelfOrDescendantOf(pid, ancestor));

    [Fact]
    public void Snapshot_ContainsCurrentProcess()
    {
        var tree = ProcessTree.Snapshot();
        Assert.NotNull(tree.Get(Environment.ProcessId));
    }
}
