namespace UnityFontReplacer.Core;

/// <summary>Keep one rollback copy per destination, including pre-existing output files.</summary>
internal sealed class ReplacementTransaction
{
    private readonly Dictionary<string, string?> _backups =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly List<string> _published = [];

    internal bool HasPublished(string path) => _published.Contains(Path.GetFullPath(path), _backups.Comparer);

    internal void Publish(string staging, string destination)
    {
        destination = Path.GetFullPath(destination);
        if (!_backups.ContainsKey(destination))
        {
            string? backup = null;
            if (File.Exists(destination))
            {
                backup = NewSiblingPath(destination, "rollback");
                try { File.Copy(destination, backup, overwrite: false); }
                catch { DeleteTemporary(backup); throw; }
            }
            _backups.Add(destination, backup);
        }
        File.Move(staging, destination, overwrite: true);
        if (!HasPublished(destination))
            _published.Add(destination);
    }

    internal void Commit()
    {
        foreach (var backup in _backups.Values)
            DeleteTemporary(backup);
        _published.Clear();
        _backups.Clear();
    }

    internal void Rollback()
    {
        var failures = new List<Exception>();
        for (int i = _published.Count - 1; i >= 0; i--)
        {
            string path = _published[i];
            string? backup = _backups[path];
            try
            {
                if (backup is null)
                    File.Delete(path);
                else
                    File.Move(backup, path, overwrite: true);
                _backups.Remove(path);
            }
            catch (Exception ex)
            {
                failures.Add(new IOException($"Rollback failed for '{path}'. Recovery copy: '{backup ?? "(none)"}'.", ex));
            }
        }

        foreach (var (path, backup) in _backups)
            if (!HasPublished(path))
                DeleteTemporary(backup);
        if (failures.Count != 0)
            throw new AggregateException("Some destinations could not be rolled back.", failures);
        _backups.Clear();
        _published.Clear();
    }

    internal static string NewSiblingPath(string destination, string purpose) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, $".ufr-{Guid.NewGuid():N}.{purpose}");

    internal static void DeleteTemporary(string? path)
    {
        if (path is null)
            return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not remove temporary file '{path}': {ex.Message}");
        }
    }

    internal static void WriteValidated(
        string destination, Action<string> write, Action<string> validate,
        Action? beforePublish, ReplacementTransaction? transaction)
    {
        string target = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string staging = NewSiblingPath(target, "staged");
        try
        {
            write(staging);
            validate(staging);
            beforePublish?.Invoke();
            if (transaction is null)
                File.Move(staging, target, overwrite: true);
            else
                transaction.Publish(staging, target);
        }
        finally
        {
            DeleteTemporary(staging);
        }
    }
}
