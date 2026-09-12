using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpPyxis.SqlServer.SchemaMcp;

/// <summary>
/// The stored connections, encrypted on disk with DPAPI under the current user.
/// </summary>
/// <remarks>
/// What this protects is the theft of the file and its accidental exposure — opened to edit
/// something else, backed up, copied to another machine, shown on a shared screen. A process running
/// as the same user decrypts it exactly as this one does. That is proportionate to what the secret
/// opens: a login that can only read object definitions.
/// </remarks>
internal sealed class ConnectionStore(string filePath)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>Where the file lives unless <c>DDL_CONFIG_PATH</c> says otherwise.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SharpPyxis.SqlServer.SchemaMcp",
        "connections.dat");

    public string FilePath => filePath;

    /// <summary>Reads the connections, or an empty list when nothing has been configured yet.</summary>
    public IReadOnlyList<ConnectionEntry> Load() => ReadFile().Connections;

    /// <summary>Adds a connection and returns it, with the identifier it was given.</summary>
    public ConnectionEntry Add(ConnectionEntry entry)
    {
        var file = ReadFile();
        var stored = entry with { Id = file.NextId };

        WriteFile(new StoredConnections(file.NextId + 1, [.. file.Connections, stored]));
        return stored;
    }

    /// <summary>Replaces a connection, matched on its identifier.</summary>
    public void Replace(ConnectionEntry entry)
    {
        var file = ReadFile();
        WriteFile(file with
        {
            Connections = [.. file.Connections.Select(stored => stored.Id == entry.Id ? entry : stored)],
        });
    }

    /// <summary>
    /// Removes a connection. The identifiers of the others do not move: a rank that renumbers would
    /// make "the 3" name a different connection than the one just discussed.
    /// </summary>
    public bool Remove(int id)
    {
        var file = ReadFile();
        var kept = file.Connections.Where(entry => entry.Id != id).ToArray();

        if (kept.Length == file.Connections.Count)
            return false;

        WriteFile(new StoredConnections(file.NextId, kept));
        return true;
    }

    private StoredConnections ReadFile()
    {
        if (!File.Exists(filePath))
            return new StoredConnections(1, []);

        // Inline rather than extracted: the platform analyser only recognises the guard in place.
        if (!OperatingSystem.IsWindows())
            throw NotOnWindows();

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(File.ReadAllBytes(filePath), null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            // Encrypted under the current user: another account, another machine or a recreated
            // profile cannot read it back. Saying so beats an unexplained failure.
            throw new InvalidOperationException(
                $"'{filePath}' cannot be decrypted. It is encrypted for the Windows account that wrote it, "
                + "so it does not travel between accounts or machines. Run 'configure' to write it again.",
                exception);
        }

        return JsonSerializer.Deserialize<StoredConnections>(plain, SerializerOptions)
               ?? new StoredConnections(1, []);
    }

    private void WriteFile(StoredConnections connections)
    {
        if (!OperatingSystem.IsWindows())
            throw NotOnWindows();

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(connections, SerializerOptions));
        File.WriteAllBytes(filePath, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }

    // DPAPI is Windows only; elsewhere CONNECTION_STRING is the way in.
    private static PlatformNotSupportedException NotOnWindows() =>
        new("The encrypted connection file needs Windows. Set CONNECTION_STRING instead.");

    private sealed record StoredConnections(int NextId, IReadOnlyList<ConnectionEntry> Connections);
}
