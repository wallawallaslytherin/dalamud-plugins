using Airwave.Relay;

if (RelayHost.IsHostCommand(args))
{
    Environment.ExitCode = RelayHost.Run(args);
    return;
}

try
{
    await using var app = RelayApplication.Build(args);
    await app.RunAsync();
}
catch (ArgumentException)
{
    Console.Error.WriteLine("Airwave relay configuration is invalid. Set distinct random publisher and listener keys and an explicit permitted bind address.");
    Environment.ExitCode = 2;
}
catch (Exception)
{
    Console.Error.WriteLine("Airwave relay could not run. Check the bind address and transport configuration.");
    Environment.ExitCode = 1;
}
