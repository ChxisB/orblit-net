using System.Diagnostics;

namespace Orblit.Net.Tests;

/// <summary>
/// The Dart package, run as a process from <c>tool/cross_peer.dart</c>. Either it hosts a scene and
/// waits for stdin to end, or it joins a host and prints the world it holds.
/// </summary>
internal sealed class DartPeer : IAsyncDisposable
{
    /// <summary>The collection every test class that starts a Dart process joins, so they run one at a time.</summary>
    internal const string Collection = "DartPeer";

    // A cold start compiles the package and runs its build hooks.
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(120);

    private readonly Process _process;
    private readonly Task<string> _error;

    private DartPeer(Process process)
    {
        _process = process;
        _error = process.StandardError.ReadToEndAsync();
    }

    /// <summary>Why a Dart peer cannot run here, or null when one can.</summary>
    internal static string? Unavailable { get; } = FindUnavailable();

    /// <summary>The address the Dart host announced.</summary>
    internal Uri Address { get; private set; } = null!;

    /// <summary>Starts a Dart host and waits until it has announced its address.</summary>
    internal static async Task<DartPeer> Host()
    {
        var peer = new DartPeer(Launch("host"));
        try
        {
            while (await peer.ReadLine() is { } line)
            {
                if (line.StartsWith("orblit-net ws://", StringComparison.Ordinal))
                {
                    peer.Address = new Uri(line["orblit-net ".Length..]);
                    return peer;
                }
            }

            throw new InvalidOperationException($"The Dart host ended before it announced an address.\n{await peer.Failure()}");
        }
        catch
        {
            await peer.DisposeAsync();
            throw;
        }
    }

    /// <summary>Joins <paramref name="address"/> as a Dart client and answers the one line of JSON it prints.</summary>
    internal static async Task<string> Client(Uri address, int ticks)
    {
        await using var peer = new DartPeer(Launch("client", address.ToString(), ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        string? last = null;
        while (await peer.ReadLine() is { } line)
        {
            last = line;
        }

        await peer._process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return peer._process.ExitCode == 0 && last is not null
            ? last
            : throw new InvalidOperationException($"The Dart client exited with {peer._process.ExitCode}.\n{await peer.Failure()}");
    }

    /// <summary>Waits for the Dart host to finish its scene and answers the world it holds, as JSON.</summary>
    internal async Task<string> Finished()
    {
        while (await ReadLine() is { } line)
        {
            if (line.StartsWith("done ", StringComparison.Ordinal))
            {
                return line["done ".Length..];
            }
        }

        throw new InvalidOperationException($"The Dart host ended before it finished.\n{await Failure()}");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.StandardInput.Close();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        await _process.WaitForExitAsync();
        _process.Dispose();
    }

    private static Process Launch(params string[] arguments)
    {
        var start = new ProcessStartInfo(FindDart() ?? throw new InvalidOperationException(Unavailable))
        {
            WorkingDirectory = RepositoryRoot() ?? throw new InvalidOperationException(Unavailable),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var word in (string[])["run", "--enable-asserts", "tool/cross_peer.dart", .. arguments])
        {
            start.ArgumentList.Add(word);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The Dart process did not start");
    }

    private static string? RepositoryRoot()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "tool", "cross_peer.dart")))
        {
            if (Path.GetDirectoryName(root) is not { } parent)
            {
                return null;
            }

            root = parent;
        }

        return root;
    }

    private static string? FindUnavailable()
    {
        if (FindDart() is null)
        {
            return "The Dart SDK was not found. Set DART or put dart on the PATH.";
        }

        return RepositoryRoot() is { } root && File.Exists(Path.Combine(root, ".dart_tool", "package_config.json"))
            ? null
            : "The Dart package is not resolved. Run tool/link_local.sh or dart pub get.";
    }

    private static string? FindDart()
    {
        var name = OperatingSystem.IsWindows() ? "dart.exe" : "dart";
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "development", "flutter", "bin"));
        return Environment.GetEnvironmentVariable("DART") is { Length: > 0 } set && File.Exists(set)
            ? set
            : folders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }

    private async Task<string?> ReadLine()
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(Patience);
        return await _process.StandardOutput.ReadLineAsync(limit.Token);
    }

    private async Task<string> Failure()
    {
        await _process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return await _error;
    }
}
