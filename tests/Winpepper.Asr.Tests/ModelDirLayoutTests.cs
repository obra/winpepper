using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public class ModelDirLayoutTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("modeldir-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Fact]
    public void Resolve_finds_gguf_and_nested_runtime_dir()
    {
        Touch("model.gguf");
        Touch(Path.Combine("runtime", "transcribe-native-windows-x86_64-cpu-vulkan", "transcribe.dll"));
        var r = ModelDirLayout.Resolve(_dir);
        r.GgufPath.ShouldBe(Path.Combine(_dir, "model.gguf"));
        r.RuntimeDir.ShouldBe(Path.Combine(_dir, "runtime", "transcribe-native-windows-x86_64-cpu-vulkan"));
    }

    [Fact]
    public void Resolve_accepts_flat_runtime_dir()
    {
        Touch("model.gguf");
        Touch(Path.Combine("runtime", "transcribe.dll"));
        ModelDirLayout.Resolve(_dir).RuntimeDir.ShouldBe(Path.Combine(_dir, "runtime"));
    }

    [Fact]
    public void Resolve_rejects_zero_or_multiple_ggufs()
    {
        Should.Throw<InvalidOperationException>(() => ModelDirLayout.Resolve(_dir))
            .Message.ShouldContain("exactly one");
        Touch("a.gguf");
        Touch("b.gguf");
        Should.Throw<InvalidOperationException>(() => ModelDirLayout.Resolve(_dir))
            .Message.ShouldContain("exactly one");
    }

    [Fact]
    public void Resolve_rejects_missing_runtime()
    {
        Touch("model.gguf");
        Should.Throw<InvalidOperationException>(() => ModelDirLayout.Resolve(_dir))
            .Message.ShouldContain("transcribe.dll");
    }
}
