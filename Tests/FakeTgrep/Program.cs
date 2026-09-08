using System.Text.Json;

if (args[0] == "echo") { Console.WriteLine(JsonSerializer.Serialize(args.Skip(1))); return; }
if (args[0] == "sleep") { Console.WriteLine(Environment.ProcessId); await Task.Delay(TimeSpan.FromMinutes(2)); return; }
if (args[0] == "malformed")
{
    Console.WriteLine("not-json"); Console.Out.Flush();
    while (true) { Console.Error.WriteLine("still alive"); await Task.Delay(30); }
}
if (args[0] == "stderr")
{
    for (int i = 0; i < 20000; i++) Console.Error.WriteLine(new string('x', 128));
    Console.WriteLine("done"); return;
}
throw new ArgumentException("Unknown test mode");
