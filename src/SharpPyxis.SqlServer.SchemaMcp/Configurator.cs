using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.SchemaMcp;

/// <summary>
/// The <c>configure</c> sub-command: the one place where credentials are typed.
/// </summary>
/// <remarks>
/// It runs in the foreground, started by the user. The server never asks for anything: a stdio
/// process blocked on a prompt misses the handshake the client waits for, and a window would open
/// without focus, behind the client or on another desktop.
/// </remarks>
internal static class Configurator
{
    /// <summary>Runs the sub-command and returns the process exit code.</summary>
    public static int Run(ConnectionStore store, string[] arguments)
    {
        var verb = arguments.Length > 1 ? arguments[1].ToLowerInvariant() : "add";

        return verb switch
        {
            "add" or "--add" => Add(store),
            "list" or "--list" => List(store),
            "remove" or "--remove" => Remove(store, arguments),
            _ => Usage(),
        };
    }

    private static int Add(ConnectionStore store)
    {
        Console.WriteLine("New connection. An empty answer keeps the value unset.");

        var server = Ask("Server (host, host\\instance or host,port)");
        if (server.Length == 0)
        {
            Console.Error.WriteLine("A server is required.");
            return 1;
        }

        var database = Ask("Database (empty to choose it per session)");
        var login = Ask("SQL login (empty for Windows authentication)");
        var password = login.Length == 0 ? string.Empty : AskSecret("Password");
        var label = Ask("Label, to name this target in plain words (optional)");
        var trust = AskYesNo("Trust a server certificate the machine does not trust?");

        var entry = new ConnectionEntry
        {
            Id = 0,
            Server = server,
            Database = database.Length == 0 ? null : database,
            Login = login.Length == 0 ? null : login,
            Password = password.Length == 0 ? null : password,
            Label = label.Length == 0 ? null : label,
            TrustServerCertificate = trust,
        };

        if (!Verify(entry))
            return 1;

        var stored = store.Add(entry);
        Console.WriteLine($"Saved as {stored.Id}: {stored.Describe()}");
        Console.WriteLine($"File: {store.FilePath}");
        return 0;
    }

    private static int List(ConnectionStore store)
    {
        var connections = store.Load();
        if (connections.Count == 0)
        {
            Console.WriteLine($"No connection stored. File: {store.FilePath}");
            return 0;
        }

        // Seeing them listed is also what shows where logins have been left lying around.
        foreach (var entry in connections)
            Console.WriteLine($"{entry.Id}\t{entry.Describe()}");

        Console.WriteLine($"File: {store.FilePath}");
        return 0;
    }

    private static int Remove(ConnectionStore store, string[] arguments)
    {
        if (arguments.Length < 3 || !int.TryParse(arguments[2], out var id))
        {
            Console.Error.WriteLine("Usage: configure remove <id>");
            return 1;
        }

        if (!store.Remove(id))
        {
            Console.Error.WriteLine($"No connection {id}.");
            return 1;
        }

        Console.WriteLine($"Connection {id} removed. The other identifiers do not move.");
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: configure [add | list | remove <id>]");
        return 1;
    }

    // Failing here, in front of the person who typed the values, beats failing later inside a client
    // that only shows that the server stopped.
    private static bool Verify(ConnectionEntry entry)
    {
        Console.Write("Testing the connection... ");
        try
        {
            using var connection = new SqlConnection(entry.BuildConnectionString());
            connection.Open();
            Console.WriteLine("ok.");
            return true;
        }
        catch (SqlException exception)
        {
            Console.WriteLine("failed.");
            Console.Error.WriteLine(exception.Message);
            return false;
        }
    }

    private static string Ask(string prompt)
    {
        Console.Write($"{prompt}: ");
        return (Console.ReadLine() ?? string.Empty).Trim();
    }

    private static bool AskYesNo(string prompt)
    {
        Console.Write($"{prompt} [y/N]: ");
        var answer = (Console.ReadLine() ?? string.Empty).Trim();
        return answer.StartsWith('y') || answer.StartsWith('Y');
    }

    private static string AskSecret(string prompt)
    {
        Console.Write($"{prompt}: ");

        var secret = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                    secret.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar))
                secret.Append(key.KeyChar);
        }

        Console.WriteLine();
        return secret.ToString();
    }
}
