using System.IO.Compression;

namespace YueToLogic.Core.Logic;

/// <summary>
/// Receives the files of a generated <c>.logicx</c> package. Keeps the writer free of file-system access:
/// a web host writes a ZIP, a desktop app or the CLI a folder.
/// </summary>
public interface ILogicPackageSink
{
    /// <summary>Creates a file inside the package; <paramref name="relativePath"/> uses '/' separators.</summary>
    Stream CreateFile(string relativePath);
}

/// <summary>Writes the package as <c>&lt;name&gt;.logicx/…</c> into a ZIP archive.</summary>
public sealed class ZipLogicPackageSink : ILogicPackageSink, IDisposable
{
    private readonly ZipArchive _archive;
    private readonly string _root;

    /// <param name="destination">Writable stream; it stays open after disposal.</param>
    /// <param name="packageName">Name of the package folder in the archive, without ".logicx".</param>
    public ZipLogicPackageSink(Stream destination, string packageName)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        _archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        _root = $"{packageName}.logicx/";
    }

    public Stream CreateFile(string relativePath)
    {
        // Audio is already compressed; storing it saves time without costing size.
        var level = relativePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
            ? CompressionLevel.NoCompression
            : CompressionLevel.Optimal;
        return _archive.CreateEntry(_root + relativePath, level).Open();
    }

    public void Dispose() => _archive.Dispose();
}
