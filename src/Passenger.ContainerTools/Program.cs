using System.Globalization;
using Passenger.ContainerTools;
using Passenger.Profiler;

// Passenger's container tools, run by the IDE through docker exec. The first word is the mode; a mode or flag it does not
// know is fatal (exit code 2), so a typo never runs something other than what was asked.
const string Usage = """
    passenger-tools MODE [FLAGS]
      dap        Passenger's debug adapter on standard input and output (dap --help lists its flags)
      processes  this container's .NET programs as a JSON array, those the debugger cannot attach to saying why
      trace      --pid PID [--resume] [--requests]: the profiler's EventPipe session of a program, its .nettrace on standard
                 output until standard input ends; --resume lets a program held at its start go once recording; --requests
                 records a web app's requests and their database commands instead (the Requests window's)
      heap       --pid PID: a memory snapshot of a program for each line 'snapshot' on standard input, its heap graph on
                 standard output, until standard input ends
      sources    the source file and line of each method given a line (TOKEN, a tab, MODULE) from its module's PDB
      version    the tools' version, which is the IDE's that they came with
    """;

if (args is [] or ["--help" or "-h"])
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 2 : 0;
}

string[] rest = args[1..];
switch (args[0])
{
    case "dap":
        return await Passenger.DebugAdapter.AdapterProgram.RunAsync(rest, "passenger-tools dap");
    case "processes":
        if (rest.Length > 0)
        {
            Console.Error.WriteLine($"processes takes no flags, not '{string.Join(' ', rest)}'");
            return 2;
        }
        Processes.Write(Console.Out);
        return 0;
    case "trace":
    {
        if (Target("trace", rest, ["--resume", "--requests"]) is not (int process, IReadOnlySet<string> switches))
        {
            return 2;
        }
        await using Stream input = Console.OpenStandardInput();
        await using Stream output = Console.OpenStandardOutput();
        return await InContainer.TraceAsync(process, switches.Contains("--resume"), input, output, Console.Error, switches.Contains("--requests"));
    }
    case "heap":
    {
        if (Target("heap", rest, []) is not (int process, _))
        {
            return 2;
        }
        await using Stream input = Console.OpenStandardInput();
        await using Stream output = Console.OpenStandardOutput();
        return await InContainer.HeapAsync(process, input, output, Console.Error);
    }
    case "sources":
        if (rest.Length > 0)
        {
            Console.Error.WriteLine($"sources takes no flags, not '{string.Join(' ', rest)}'");
            return 2;
        }
        return InContainer.Sources(Console.In, Console.Out, Console.Error);
    case "version":
        if (rest.Length > 0)
        {
            Console.Error.WriteLine($"version takes no flags, not '{string.Join(' ', rest)}'");
            return 2;
        }
        // The informational version, less the commit after its '+'.
        Console.WriteLine((System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Processes).Assembly)
            ?.InformationalVersion ?? "0.0.0").Split('+')[0]);
        return 0;
    default:
        Console.Error.WriteLine($"Unknown mode '{args[0]}'");
        Console.Error.WriteLine(Usage);
        return 2;
}

// A profiling mode's flags: --pid with a process id of this container, once, and the switches it allows; null, said why,
// for anything else.
static (int Process, IReadOnlySet<string> Switches)? Target(string mode, string[] flags, string[] allowed)
{
    int? process = null;
    HashSet<string> switches = new(StringComparer.Ordinal);
    for (int i = 0; i < flags.Length; i++)
    {
        string flag = flags[i];
        if (flag == "--pid" && process is null && i + 1 < flags.Length
            && int.TryParse(flags[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id > 0)
        {
            process = id;
            i++;
        }
        else if (!allowed.Contains(flag) || !switches.Add(flag))
        {
            Console.Error.WriteLine(flag == "--pid"
                ? $"{mode} takes --pid once, with a process id of this container"
                : $"{mode} takes --pid PID{string.Concat(allowed.Select(a => $" [{a}]"))}, not '{flag}'");
            return null;
        }
    }
    if (process is not int found)
    {
        Console.Error.WriteLine($"{mode} needs --pid PID, the process id of the program in this container");
        return null;
    }
    return (found, switches);
}
