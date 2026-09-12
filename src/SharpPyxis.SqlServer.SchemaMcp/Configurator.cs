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
        // No verb prints the help. Starting an interactive prompt on a bare command hides the other
        // sub-commands from whoever did not already know they existed.
        if (arguments.Length <= 1)
            return Usage(store);

        return arguments[1].ToLowerInvariant() switch
        {
            "add" or "--add" => Add(store),
            "list" or "--list" => List(store),
            "remove" or "--remove" => Remove(store, arguments),
            "test" or "--test" => Test(store, arguments),
            "set-password" => SetPassword(store, arguments),
            _ => Usage(store),
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

        // A failed test does not throw the answers away: the instance may simply be down, and
        // retyping a password because of that is how a tool loses its user.
        if (!Verify(entry) && !AskYesNo("Save it anyway?"))
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

    /// <summary>The sub-commands as the help lists them, shared with server_info so the two never disagree.</summary>
    public const string Commands = """
          configure add                 record a new connection, testing it first
          configure list                show what is recorded
          configure test [id]           test one recorded connection, or all of them
          configure set-password <id>   replace the password of one connection
          configure remove <id>         delete one connection
        """;

    private static int Usage(ConnectionStore store)
    {
        Console.WriteLine($"""
            Manages the connections this server can work against.

            {Commands}

            Nothing is recorded until a connection has been tested, or kept despite a failed test.
            """);
        Console.WriteLine($"File: {store.FilePath}");
        return 1;
    }

    private static int Test(ConnectionStore store, string[] arguments)
    {
        var connections = store.Load();
        if (arguments.Length >= 3)
        {
            if (!int.TryParse(arguments[2], out var id) || connections.FirstOrDefault(entry => entry.Id == id) is not { } one)
            {
                Console.Error.WriteLine($"No connection {arguments[2]}.");
                return 1;
            }

            connections = [one];
        }

        if (connections.Count == 0)
        {
            Console.WriteLine("No connection recorded.");
            return 0;
        }

        var failures = 0;
        foreach (var entry in connections)
        {
            Console.Write($"{entry.Id}\t{entry.Describe()}... ");
            if (!Verify(entry, silent: true))
                failures++;
        }

        return failures == 0 ? 0 : 1;
    }

    // Replacing a password without retyping a server, a database and a login is what keeps a
    // rotated secret from being a chore.
    private static int SetPassword(ConnectionStore store, string[] arguments)
    {
        if (arguments.Length < 3
            || !int.TryParse(arguments[2], out var id)
            || store.Load().FirstOrDefault(entry => entry.Id == id) is not { } existing)
        {
            Console.Error.WriteLine("Usage: configure set-password <id>");
            return 1;
        }

        if (existing.Login is null)
        {
            Console.Error.WriteLine($"Connection {id} uses Windows authentication and has no password.");
            return 1;
        }

        var updated = existing with { Password = AskSecret($"New password for {existing.Describe()}") };

        if (!Verify(updated) && !AskYesNo("Save it anyway?"))
            return 1;

        store.Replace(updated);
        Console.WriteLine($"Password of {id} replaced.");
        return 0;
    }

    // Failing here, in front of the person who typed the values, beats failing later inside a client
    // that only shows that the server stopped.
    private static bool Verify(ConnectionEntry entry, bool silent = false)
    {
        if (!silent)
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
            Console.Error.WriteLine($"  {exception.Message}");
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

    // Masked, but not blind: one star per character, and the length restated at the end. Pasting a
    // password shows nothing otherwise, so a paste that silently failed is indistinguishable from
    // one that worked — which is exactly how a wrong secret reaches the connection test.
    private static string AskSecret(string prompt)
    {
        Console.Write($"{prompt} (paste with a right click, or Ctrl+V in Windows Terminal): ");

        var secret = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (char.IsControl(key.KeyChar))
                continue;

            secret.Append(key.KeyChar);
            Console.Write('*');
        }

        Console.WriteLine($"  ({secret.Length} characters)");
        return secret.ToString();
    }
}
