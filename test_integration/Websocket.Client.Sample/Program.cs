using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;


namespace Websocket.Client.Sample
{
    class Program
    {
        private static readonly ManualResetEvent ExitEvent = new ManualResetEvent(false);

        static void Main(string[] args)
        {
            InitLogging();

            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
            AssemblyLoadContext.Default.Unloading += DefaultOnUnloading;
            Console.CancelKeyPress += ConsoleOnCancelKeyPress;

            Console.WriteLine("|=======================|");
            Console.WriteLine("|    WEBSOCKET CLIENT   |");
            Console.WriteLine("|=======================|");
            Console.WriteLine();

            Log.Debug("====================================");
            Log.Debug("              STARTING              ");
            Log.Debug("====================================");
           


            var url = new Uri("wss://www.bitmex.com/realtime");
            using (var client = new WebsocketClient(url))
            {
                client.Name = "Bitmex";
                client.ReconnectTimeoutMs = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;
                client.ReconnectionHappened.Subscribe(type =>
                    Log.Information($"Reconnection happened, type: {type}, url: {client.Url}"));
                client.DisconnectionHappened.Subscribe(type => 
                    Log.Warning($"Disconnection happened, type: {type}"));

                client.MessageReceived.Subscribe(msg => Log.Information($"Message received: {msg}"));

                client.Start().Wait();

                Task.Run(() => StartSendingPing(client));
                Task.Run(() => SwitchUrl(client));

                ExitEvent.WaitOne();
            }

            Log.Debug("====================================");
            Log.Debug("              STOPPING              ");
            Log.Debug("====================================");
            Log.CloseAndFlush();
        }

        private static async Task StartSendingPing(WebsocketClient client)
        {
            while (true)
            {
                await Task.Delay(1000);
                await client.Send("ping");
            }
        }

        private static async Task SwitchUrl(WebsocketClient client)
        {
            while (true)
            {
                await Task.Delay(10000);
                
                var production = new Uri("wss://www.bitmex.com/realtime");
                var testnet = new Uri("wss://testnet.bitmex.com/realtime");

                var selected = client.Url == production ? testnet : production;
                client.Url = selected;
                await client.Reconnect();
            }
        }

        private static void InitLogging()
        {
            var executingDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            var logPath = Path.Combine(executingDir, "logs", "verbose.log");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .WriteTo.ColoredConsole(LogEventLevel.Verbose, 
                    outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message} {NewLine}{Exception}")
                .CreateLogger();
        }

        private static void CurrentDomainOnProcessExit(object sender, EventArgs eventArgs)
        {
            Log.Warning("Exiting process");
            ExitEvent.Set();
        }

        private static void DefaultOnUnloading(AssemblyLoadContext assemblyLoadContext)
        {
            Log.Warning("Unloading process");
            ExitEvent.Set();
        }

        private static void ConsoleOnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Log.Warning("Canceling process");
            e.Cancel = true;
            ExitEvent.Set();
        }
    }
}
