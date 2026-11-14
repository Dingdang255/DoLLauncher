namespace DoLLauncher.Platform.Abstractions;

/// <summary>文件读写的最小抽象，便于应用服务层脱离具体文件系统实现。</summary>
public interface IStorage
{
    /// <summary>读取文本文件全部内容。</summary>
    string ReadAllText(string path);

    /// <summary>写入文本文件，覆盖原有内容。</summary>
    void WriteAllText(string path, string content);

    /// <summary>文件是否存在。</summary>
    bool Exists(string path);

    /// <summary>删除文件（不存在时忽略）。</summary>
    void Delete(string path);

    /// <summary>确保目录存在。</summary>
    void EnsureDirectory(string path);

    /// <summary>枚举目录下的文件。</summary>
    IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false);
}
