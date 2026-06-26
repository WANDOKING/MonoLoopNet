namespace TestMonoLoopServer;

using MonoLoop.Server.Network;
using TestMonoLoopServer.Logger;

internal class Program
{
    private static void Main(string[] args)
    {
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

        builder.AddOnTickHandler(deltaTime =>
        {
            Console.WriteLine($"Tick: {DateTime.Now}, deltaTime={deltaTime.TotalMilliseconds}ms");
        });

        var server = builder.Build();

        server.Run();
    }
}
