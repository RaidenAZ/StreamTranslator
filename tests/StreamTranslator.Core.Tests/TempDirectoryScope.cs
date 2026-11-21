namespace StreamTranslator.Core.Tests;

/// <summary>
/// 自动清理测试临时目录的作用域封装，避免反复运行单元测试在
/// %LOCALAPPDATA%\Temp 下残留大量 streamtranslator-* 文件夹。
/// </summary>
internal sealed class TempDirectoryScope : IDisposable
{
    private TempDirectoryScope(DirectoryInfo directory)
    {
        Directory = directory;
    }

    public DirectoryInfo Directory { get; }
    public string FullName => Directory.FullName;

    public static TempDirectoryScope Create(string prefix)
    {
        return new TempDirectoryScope(System.IO.Directory.CreateTempSubdirectory(prefix));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists)
            {
                Directory.Delete(recursive: true);
            }
        }
        catch
        {
            // 测试清理不掩盖断言结果
        }
    }
}
