using System.Globalization;

namespace Aurora21.UltimateTeam.Server.Hosting;

/// <summary>An argument error: reported with usage and exit code 2 (like argparse).</summary>
public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message)
    {
    }
}

/// <summary>Minimal <c>--name value</c> / <c>--flag</c> / <c>--name=value</c> parser.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly List<string> _positionals = new();
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    public CommandLine(IEnumerable<string> args, IReadOnlySet<string> flags)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                _positionals.Add(arg);
                continue;
            }
            var equals = arg.IndexOf('=');
            if (equals > 0)
            {
                _options[arg[..equals]] = arg[(equals + 1)..];
            }
            else if (flags.Contains(arg))
            {
                _options[arg] = null;
            }
            else if (i + 1 < list.Count)
            {
                _options[arg] = list[++i];
            }
            else
            {
                throw new UsageException($"argument {arg}: expected one argument");
            }
        }
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public bool Flag(string name)
    {
        _consumed.Add(name);
        return _options.ContainsKey(name);
    }

    public string? Value(string name)
    {
        _consumed.Add(name);
        return _options.TryGetValue(name, out var value) ? value : null;
    }

    public string Value(string name, string fallback) => Value(name) ?? fallback;

    public long? Long(string name)
    {
        var value = Value(name);
        if (value is null)
        {
            return null;
        }
        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new UsageException($"argument {name}: invalid int value: '{value}'");
    }

    public string Choice(string name, string fallback, params string[] choices)
    {
        var value = Value(name, fallback);
        return choices.Contains(value)
            ? value
            : throw new UsageException($"argument {name}: invalid choice: '{value}' (choose from {string.Join(", ", choices.Select(c => $"'{c}'"))})");
    }

    /// <summary>Rejects options that no command read.</summary>
    public void EnsureAllConsumed()
    {
        var unknown = _options.Keys.Where(key => !_consumed.Contains(key)).ToList();
        if (unknown.Count > 0)
        {
            throw new UsageException($"unrecognized arguments: {string.Join(" ", unknown)}");
        }
    }
}
