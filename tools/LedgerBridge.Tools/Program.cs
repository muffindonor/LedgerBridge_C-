using LedgerBridge.Tools;

if (args.Length == 0)
{
    Console.Error.WriteLine("Commands: generate, chaos, naive, naive-load");
    return 64;
}

Arguments options = Arguments.Parse(args[1..]);
return args[0] switch
{
    "generate" => Generator.Run(options),
    "chaos" => await Chaos.RunAsync(options),
    "naive" => await NaiveControl.RunAsync(options),
    "naive-load" => await NaiveControl.LoadAsync(options),
    _ => Unknown(args[0]),
};

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    return 64;
}
