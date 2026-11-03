using System.Collections;
using System.Globalization;
using System.Reflection;

namespace PowerTrove.ReleaseTools.Cli;

/// <summary>Small BCL-only command and option adapter, scoped to ReleaseTools.</summary>
internal abstract class CliOption
{
    protected CliOption(string name, string description)
    {
        if (!name.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Long option name required.", nameof(name));
        Name = name;
        Description = description;
    }

    public string Name { get; }
    public string Description { get; }
    public bool IsRequired { get; set; }
    internal abstract bool IsFlag { get; }
    internal abstract object? DefaultValue();
    internal abstract bool TryRead(string text, out object? result);
}

internal sealed class Option<T> : CliOption
{
    private readonly Func<T>? _default;
    public Option(string name, string description) : base(name, description) { }
    public Option(string name, Func<T> defaultValue, string description) : base(name, description)
    {
        _default = defaultValue ?? throw new ArgumentNullException(nameof(defaultValue));
    }

    internal override bool IsFlag => typeof(T) == typeof(bool);
    internal override object? DefaultValue() => _default is null ? default(T) : _default();
    internal override bool TryRead(string text, out object? result)
    {
        if (typeof(T) == typeof(string))
        {
            result = text;
            return true;
        }
        if (typeof(T) == typeof(bool) && bool.TryParse(text, out var flag))
        {
            result = flag;
            return true;
        }
        if (typeof(T) == typeof(int?) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            result = (int?)number;
            return true;
        }
        result = null;
        return false;
    }
}

internal sealed class ParsedOptions
{
    private readonly Dictionary<CliOption, object?> _values;
    public ParsedOptions(Dictionary<CliOption, object?> values) => _values = values;

    public T Get<T>(Option<T> option) =>
        (T)(_values.TryGetValue(option, out var value) ? value : option.DefaultValue())!;
}

internal class Command : IEnumerable<CliOption>
{
    private readonly List<CliOption> _options = [];
    private Func<ParsedOptions, Task>? _handler;

    public Command(string name, string description)
    {
        Name = name;
        Description = description;
    }
    public string Name { get; }
    public string Description { get; }
    internal IReadOnlyList<CliOption> Options => _options;
    public void Add(CliOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (_options.Any(x => x.Name == option.Name))
            throw new ArgumentException("Duplicate option: " + option.Name);
        _options.Add(option);
    }
    public IEnumerator<CliOption> GetEnumerator() => _options.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal void SetHandlerCore(Func<ParsedOptions, Task> handler) => _handler = handler;
    internal Task Execute(ParsedOptions options) => _handler?.Invoke(options) ?? Task.CompletedTask;
}

internal sealed class RootCommand : Command
{
    private readonly Dictionary<string, Command> _commands = new(StringComparer.Ordinal);
    public RootCommand(string description) : base("PowerTrove.ReleaseTools", description) { }

    public void AddCommand(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_commands.TryAdd(command.Name, command))
            throw new ArgumentException("Duplicate command: " + command.Name);
    }

    public async Task<int> InvokeAsync(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        try
        {
            if (args.Length == 0)
            {
                // A missing command is an error, as it was with System.CommandLine.
                error.WriteLine("Required command was not provided.");
                error.WriteLine();
                PrintHelp(error, null);
                return 1;
            }
            if (IsHelp(args[0]))
            {
                PrintHelp(output, null);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--version")
            {
                output.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
                return 0;
            }
            if (!_commands.TryGetValue(args[0], out var command))
                throw new CliSyntaxException("Unrecognized command or argument '" + args[0] + "'.");

            // The help alias belongs to every command and bypasses required-option checks.
            if (args.Skip(1).Any(IsHelp))
            {
                PrintHelp(output, command);
                return 0;
            }
            var parsed = Parse(command, args.AsSpan(1));
            await command.Execute(parsed).ConfigureAwait(false);
            return 0;
        }
        catch (CliSyntaxException exception)
        {
            error.WriteLine("Error: " + exception.Message);
            error.WriteLine("Use --help for more information.");
            return 1;
        }
        catch (Exception exception)
        {
            // Release failures are diagnosed from CI logs, so keep the full stack trace.
            error.WriteLine("Unhandled exception: " + exception);
            return 1;
        }
    }

    private static bool IsHelp(string value) => value is "--help" or "-h" or "-?";

    private static ParsedOptions Parse(Command command, ReadOnlySpan<string> args)
    {
        var known = command.Options.ToDictionary(o => o.Name, StringComparer.Ordinal);
        var values = new Dictionary<CliOption, object?>();
        for (int index = 0; index < args.Length; index++)
        {
            var token = args[index];
            int equals = token.IndexOf('=');
            if (equals < 0) equals = token.IndexOf(':');
            string name = equals < 0 ? token : token[..equals];
            if (!known.TryGetValue(name, out var option))
                throw new CliSyntaxException("Unrecognized option or argument '" + token + "'.");

            if (values.ContainsKey(option))
                throw new CliSyntaxException("Option '" + name + "' may only be specified once.");

            string? value = equals >= 0 ? token[(equals + 1)..] : null;
            if (option.IsFlag && value is null)
            {
                if (index + 1 < args.Length &&
                    bool.TryParse(args[index + 1], out _))
                    value = args[++index];
                else value = "true";
            }
            else if (value is null)
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                    IsHelp(args[index + 1]))
                    throw new CliSyntaxException("Option '" + name + "' requires an argument.");
                value = args[++index];
            }
            if (!option.TryRead(value, out var parsed))
                throw new CliSyntaxException("Invalid value '" + value + "' for option '" + name + "'.");
            values[option] = parsed;
        }
        foreach (var option in command.Options)
        {
            if (option.IsRequired && !values.ContainsKey(option))
                throw new CliSyntaxException("Required option '" + option.Name + "' was not provided.");
        }
        return new ParsedOptions(values);
    }

    private void PrintHelp(TextWriter writer, Command? command)
    {
        writer.WriteLine("Description:");
        writer.WriteLine("  " + (command?.Description ?? Description));
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  PowerTrove.ReleaseTools" + (command is null ? " [command] [options]" : " " + command.Name + " [options]"));
        writer.WriteLine();
        writer.WriteLine("Options:");
        var rows = new List<(string Label, string Text)>();
        if (command is null) rows.Add(("--version", "Show version information"));
        else
        {
            foreach (var option in command.Options)
            {
                string label = option.Name + (option.IsFlag ? "" : " <" + option.Name[2..] + ">") +
                    (option.IsRequired ? " (REQUIRED)" : "");
                string description = option.Description;
                var defaultValue = option.DefaultValue();
                if (defaultValue is not null && defaultValue is not false)
                    description += " [default: " + defaultValue + "]";
                rows.Add((label, description));
            }
        }
        rows.Add(("-?, -h, --help", "Show help and usage information"));
        WriteColumns(writer, rows);
        if (command is null)
        {
            writer.WriteLine();
            writer.WriteLine("Commands:");
            WriteColumns(writer, _commands.Values.Select(child => (child.Name, child.Description)).ToList());
        }
    }

    /// <summary>Aligns the second column two spaces after the longest label.</summary>
    private static void WriteColumns(TextWriter writer, IReadOnlyList<(string Label, string Text)> rows)
    {
        int width = rows.Max(row => row.Label.Length) + 2;
        foreach (var (label, text) in rows)
            writer.WriteLine("  " + label.PadRight(width) + text);
    }

    private sealed class CliSyntaxException(string message) : Exception(message) { }
}

internal static class HandlerBindings
{
    public static void SetHandler<T1, T2>(this Command command, Func<T1, T2, Task> handler, Option<T1> option1, Option<T2> option2)
        => command.SetHandlerCore(values => handler(values.Get(option1), values.Get(option2)));

    public static void SetHandler<T1, T2, T3>(this Command command, Func<T1, T2, T3, Task> handler, Option<T1> option1, Option<T2> option2, Option<T3> option3)
        => command.SetHandlerCore(values => handler(values.Get(option1), values.Get(option2), values.Get(option3)));

    public static void SetHandler<T1, T2, T3, T4>(this Command command, Func<T1, T2, T3, T4, Task> handler, Option<T1> option1, Option<T2> option2, Option<T3> option3, Option<T4> option4)
        => command.SetHandlerCore(values => handler(values.Get(option1), values.Get(option2), values.Get(option3), values.Get(option4)));

    public static void SetHandler<T1, T2, T3, T4, T5, T6>(this Command command, Func<T1, T2, T3, T4, T5, T6, Task> handler, Option<T1> option1, Option<T2> option2, Option<T3> option3, Option<T4> option4, Option<T5> option5, Option<T6> option6)
        => command.SetHandlerCore(values => handler(values.Get(option1), values.Get(option2), values.Get(option3), values.Get(option4), values.Get(option5), values.Get(option6)));

    public static void SetHandler<T1, T2, T3, T4, T5, T6>(this Command command, Action<T1, T2, T3, T4, T5, T6> handler, Option<T1> option1, Option<T2> option2, Option<T3> option3, Option<T4> option4, Option<T5> option5, Option<T6> option6)
        => command.SetHandlerCore(values => { handler(values.Get(option1), values.Get(option2), values.Get(option3), values.Get(option4), values.Get(option5), values.Get(option6)); return Task.CompletedTask; });

    public static void SetHandler<T1, T2, T3, T4, T5, T6, T7>(this Command command, Func<T1, T2, T3, T4, T5, T6, T7, Task> handler, Option<T1> option1, Option<T2> option2, Option<T3> option3, Option<T4> option4, Option<T5> option5, Option<T6> option6, Option<T7> option7)
        => command.SetHandlerCore(values => handler(values.Get(option1), values.Get(option2), values.Get(option3), values.Get(option4), values.Get(option5), values.Get(option6), values.Get(option7)));
}
