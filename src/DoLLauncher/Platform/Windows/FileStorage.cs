using System.IO;
using DoLLauncher.Platform.Abstractions;

namespace DoLLauncher.Platform.Windows;

/// <summary>基于 System.IO 的存储实现。</summary>
public sealed class FileStorage : IStorage
{
    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);

    public bool Exists(string path) => File.Exists(path);

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path, searchPattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
}
