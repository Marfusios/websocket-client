using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.TestHost;

namespace Websocket.Client.Tests.Testserver
{
    public class TestserverApplicationFactory<T> : WebApplicationFactory<T>
        where T : class
    {
        protected override TestServer CreateServer(IWebHostBuilder builder) =>
            base.CreateServer(
                builder.UseSolutionRelativeContentRoot(""));

        protected override IWebHostBuilder CreateWebHostBuilder() =>
            WebHost.CreateDefaultBuilder()
                .UseStartup<T>();
    }
}
