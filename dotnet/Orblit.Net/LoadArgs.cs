using System.Globalization;

namespace Orblit.Net;

/// <summary>Turns command line arguments into a load run.</summary>
public static class LoadArgs
{
    public const string Usage = """
        Usage: orblit-load [options]
          --clients N    scripted clients (default 50)
          --seconds S    length of the run (default 10)
          --hz N         ticks per second (default 20)
          --script NAME  still, walk, random or scatter (default walk)
          --sockets      WebSockets on localhost instead of loopback links
          --slow-ms N    a system that takes N ms each tick (default 0)
          --cell N       tell each client only about entities within one cell of
                         its own, cells N units across (default: tell everyone)
          --help         this text

        The tick is judged over the last 300 ticks. Exits 1 when the host's average
        tick is longer than the period, and 2 when the host refused any input.
        """;

    private static readonly HashSet<string> ValueFlags = ["clients", "seconds", "hz", "script", "slow-ms", "cell"];

    /// <summary>
    /// Throws <see cref="FormatException"/> with a message fit to show, or
    /// <see cref="ArgumentException"/> from <see cref="LoadOptions"/>.
    /// </summary>
    public static (LoadOptions Options, string Script) Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var flags = Flags(args);
        var name = flags.GetValueOrDefault("script") ?? "walk";
        if (!LoadScripts.All.TryGetValue(name, out var script))
        {
            throw new FormatException($"--script is one of {string.Join(", ", LoadScripts.All.Keys)}.");
        }

        var hz = Number(flags, "hz", 20);
        var slow = Number(flags, "slow-ms", 0, min: 0);
        var ticks = (long)Number(flags, "seconds", 10) * hz;
        if (ticks > int.MaxValue)
        {
            throw new FormatException("--seconds and --hz ask for more ticks than a run can count.");
        }

        var options = new LoadOptions(Number(flags, "clients", 50), (int)ticks, script)
        {
            Period = TimeSpan.FromMicroseconds(1_000_000 / hz),
            Sockets = flags.ContainsKey("sockets"),
            ServerWork = slow > 0 ? _ => Thread.Sleep(slow) : null,
            CellSize = flags.ContainsKey("cell") ? Number(flags, "cell", 1) : null,
        };
        return (options, name);
    }

    /// <summary>Reads <c>--name value</c> and <c>--name=value</c>. A bare <c>--sockets</c> is a switch.</summary>
    private static Dictionary<string, string> Flags(string[] args)
    {
        var flags = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            var (name, value) = Split(args[i]);
            if (name == "sockets" && value is null)
            {
                flags[name] = string.Empty;
            }
            else if (name is null || !ValueFlags.Contains(name))
            {
                throw new FormatException($"Unknown option: {args[i]}");
            }
            else
            {
                flags[name] = value ?? (i + 1 < args.Length ? args[++i] : throw new FormatException($"--{name} needs a value."));
            }
        }

        return flags;
    }

    private static (string? Name, string? Value) Split(string arg)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            return (null, null);
        }

        var eq = arg.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? (arg[2..], null) : (arg[2..eq], arg[(eq + 1)..]);
    }

    private static int Number(Dictionary<string, string> flags, string name, int fallback, int min = 1)
    {
        var text = flags.GetValueOrDefault(name) ?? fallback.ToString(CultureInfo.InvariantCulture);
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value >= min
            ? value
            : throw new FormatException($"--{name} wants a whole number of {min} or more.");
    }
}
