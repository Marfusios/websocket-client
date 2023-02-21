using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Tests.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace Websocket.Client.Tests
{
#if DEBUG
    public class PerformanceTests
    {
        private readonly TestContext<SimpleStartup> _context;
        private readonly ITestOutputHelper _output;

        public PerformanceTests(ITestOutputHelper output)
        {
            _output = output;
            _context = new TestContext<SimpleStartup>(null);
        }

        [Theory]
        [InlineData(10)]
        [InlineData(100)]
        [InlineData(1000)]
        [InlineData(10000)]

        [InlineData(100000)]
        public async Task SendingRequests_SmallMessages(int messageCount)
        {
            using var client = _context.CreateClient();
            string received = null;
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client
                .MessageReceived
                .Subscribe(msg =>
                {
                    receivedCount++;
                    received = msg.Text;

                    if (receivedCount >= messageCount)
                        receivedEvent.Set();
                });

            await client.Start();

            var sw1 = Stopwatch.StartNew();
            for (int i = 0; i < messageCount; i++)
            {
                client.Send($"echo_fast:{i}");
            }
            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));
            sw1.Stop();

            receivedEvent.Reset();
            receivedCount = 0;

            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < messageCount; i++)
            {
                await client.SendInstant($"echo_fast:{i}");
            }
            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));
            sw2.Stop();

            _output.WriteLine($"Sending {messageCount} via queue took {sw1.ElapsedMilliseconds} ms");
            _output.WriteLine($"Sending {messageCount} via instant took {sw2.ElapsedMilliseconds} ms");

            Assert.NotNull(received);
            Assert.Equal(messageCount, messageCount);
        }

        [Theory]
        [InlineData(10)]
        [InlineData(100)]
        [InlineData(1000)]
        [InlineData(10000)]
        //[InlineData(100000)]

        public async Task SendingRequests_LargeMessages(int messageCount)
        {
            using var client = _context.CreateClient();
            string received = null;
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client
                .MessageReceived
                .Subscribe(msg =>
                {
                    receivedCount++;
                    received = msg.Text;

                    if (receivedCount >= messageCount)
                        receivedEvent.Set();
                });

            await client.Start();

            var sw1 = Stopwatch.StartNew();
            for (int i = 0; i < messageCount; i++)
            {
                var sign = 'A';
                var msg = new string(sign, 1024 * 9);
                client.Send($"echo_fast:{msg}");
            }
            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));
            sw1.Stop();

            receivedEvent.Reset();
            receivedCount = 0;

            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < messageCount; i++)
            {
                var sign = 'B';
                var msg = new string(sign, 1024 * 9);
                await client.SendInstant($"echo_fast:{msg}");
            }
            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));
            sw2.Stop();

            _output.WriteLine($"Sending {messageCount} via queue took {sw1.ElapsedMilliseconds} ms");
            _output.WriteLine($"Sending {messageCount} via instant took {sw2.ElapsedMilliseconds} ms");

            Assert.NotNull(received);
            Assert.Equal(messageCount, messageCount);
        }
    }
#endif
}
