namespace Inventory.Data;

public sealed record RepositoryFile(string RelativePath, long Size, DateTime ModifiedUtc);

/// <summary>Dateiablage der Pakete: ein Ordner je Paket unterhalb des Repository-Verzeichnisses.</summary>
public sealed class PackageRepository(string root)
{
    public string Root { get; } = root;

    public string DirectoryFor(int packageId) => Path.Combine(Root, packageId.ToString());

    public List<RepositoryFile> List(int packageId)
    {
        var directory = DirectoryFor(packageId);
        if (!Directory.Exists(directory))
            return [];
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => new RepositoryFile(Path.GetRelativePath(directory, f.FullName).Replace('/', '\\'), f.Length, f.LastWriteTimeUtc))
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SaveAsync(int packageId, string fileName, Stream content, CancellationToken ct = default)
    {
        var path = Resolve(packageId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Erst vollständig schreiben, dann umbenennen: ein abgebrochener Upload hinterlässt keine halbe Installationsdatei.
        var temp = path + ".upload";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            await content.CopyToAsync(file, ct);
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(int packageId, string relativePath)
    {
        var path = Resolve(packageId, relativePath);
        if (File.Exists(path))
            File.Delete(path);
    }

    public void DeleteAll(int packageId)
    {
        var directory = DirectoryFor(packageId);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    /// <summary>Lokaler Pfad einer Paketdatei; lehnt Pfade ab, die aus dem Paketordner herausführen.</summary>
    public string Resolve(int packageId, string relativePath)
    {
        var directory = Path.GetFullPath(DirectoryFor(packageId));
        var path = Path.GetFullPath(Path.Combine(directory, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Invalid file path: {relativePath}", nameof(relativePath));
        return path;
    }
}
