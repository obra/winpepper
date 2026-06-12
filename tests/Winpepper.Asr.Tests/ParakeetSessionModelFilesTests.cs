using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class ParakeetSessionModelFilesTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winpepper-asr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Touch(string dir, string name) =>
        File.WriteAllText(Path.Combine(dir, name), "stub");

    [Fact]
    public void ModelFilesPresent_DirectoryDoesNotExist_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winpepper-asr-test-" + Guid.NewGuid().ToString("N"));
        ParakeetSession.ModelFilesPresent(dir).ShouldBeFalse();
    }

    [Fact]
    public void ModelFilesPresent_EmptyDirectory_ReturnsFalse()
    {
        var dir = TempDir();
        try { ParakeetSession.ModelFilesPresent(dir).ShouldBeFalse(); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ModelFilesPresent_PartialInstall_ReturnsFalse()
    {
        var dir = TempDir();
        try
        {
            Touch(dir, "encoder-model.int8.onnx");
            Touch(dir, "vocab.txt");
            // decoder missing
            ParakeetSession.ModelFilesPresent(dir).ShouldBeFalse();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ModelFilesPresent_AllFilesPresent_ReturnsTrue()
    {
        var dir = TempDir();
        try
        {
            Touch(dir, "encoder-model.int8.onnx");
            Touch(dir, "decoder_joint-model.int8.onnx");
            Touch(dir, "vocab.txt");
            ParakeetSession.ModelFilesPresent(dir).ShouldBeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ModelFilesPresent_AcceptsAlternateNonInt8FileNames_ReturnsTrue()
    {
        var dir = TempDir();
        try
        {
            Touch(dir, "encoder-model.onnx");
            Touch(dir, "decoder_joint-model.onnx");
            Touch(dir, "vocab.txt");
            ParakeetSession.ModelFilesPresent(dir).ShouldBeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
