namespace TestMonoLoopServer;

using MonoLoop.Server.Network;
using System.Reflection;
using TestMonoLoopServer.Logger;

internal class Program
{
    private static string GetAssemblyMetaData(string key)
    {
        string metaData = "N/A";

        var entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly is not null)
        {
            var assemblyMetadataAttribute = entryAssembly.GetCustomAttributes<AssemblyMetadataAttribute>();
            metaData = assemblyMetadataAttribute.FirstOrDefault(a => a.Key == key)?.Value ?? "N/A";
        }

        return metaData;
    }

    private static void Main(string[] args)
    {
        Console.WriteLine($"[BUILD] host={GetAssemblyMetaData("BuildHost")} time={GetAssemblyMetaData("BuildTime")}");

        var builder = new MonoLoopServerBuilder()
            .ConfigureLogger(() => new ConsoleLogger())
            .WithPort(9642);

        builder.AddOnConnectedHandler(session =>
        {
            Console.WriteLine($"session connected. Id={session.Id}");
        });

        builder.AddOnDisconnectedHandler(session =>
        {
            Console.WriteLine($"session disconnected. Id={session.Id}");
        });

        builder.AddOnMessageReceivedHandler((session, message) =>
        {
            Console.WriteLine($"message received. SessionId={session.Id}, Message={BitConverter.ToString(message)}");
            //session.Send(message);
        });

        builder.WithFrameRateLimit(1);

        builder.AddOnTickHandler(deltaTime =>
        {
            Console.WriteLine($"Tick: {DateTime.Now}, deltaTime={deltaTime.TotalMilliseconds}ms");
        });

        var server = builder.Build();

        server.Run();
    }
}
