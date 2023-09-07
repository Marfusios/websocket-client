![Logo](https://raw.githubusercontent.com/Marfusios/websocket-client/master/websocket-logo-modern.png)
# Websocket .NET client [![NuGet version](https://badge.fury.io/nu/Websocket.Client.svg)](https://www.nuget.org/packages/Websocket.Client) [![Nuget downloads](https://img.shields.io/nuget/dt/Websocket.Client)](https://www.nuget.org/packages/Websocket.Client)

This is a wrapper over native C# class `ClientWebSocket` with built-in reconnection and error handling. 

[Releases and breaking changes](https://github.com/Marfusios/websocket-client/releases)

### License: 
    MIT

### Features

* installation via NuGet ([Websocket.Client](https://www.nuget.org/packages/Websocket.Client))
* targeting .NET Standard 2.0 (.NET Core, Linux/MacOS compatible) + Standard 2.1, .NET 5 and .NET 6
* reactive extensions ([Rx.NET](https://github.com/Reactive-Extensions/Rx.NET))
* integrated logging abstraction ([LibLog](https://github.com/damianh/LibLog))
* using Channels for high performance sending queue

### Usage

```csharp
var exitEvent = new ManualResetEvent(false);
var url = new Uri("wss://xxx");

using (var client = new WebsocketClient(url))
{
    client.ReconnectTimeout = TimeSpan.FromSeconds(30);
    client.ReconnectionHappened.Subscribe(info =>
        Log.Information($"Reconnection happened, type: {info.Type}"));

    client.MessageReceived.Subscribe(msg => Log.Information($"Message received: {msg}"));
    client.Start();

    Task.Run(() => client.Send("{ message }"));

    exitEvent.WaitOne();
}
```

More usage examples:
* integration tests ([link](test_integration/Websocket.Client.Tests.Integration))
* console sample ([link](test_integration/Websocket.Client.Sample/Program.cs))
* .net framework sample ([link](test_integration/Websocket.Client.Sample.NetFramework))
* blazor sample ([link](test_integration/Websocket.Client.Sample.Blazor))


**Pull Requests are welcome!**

### Advanced configuration

To set some advanced configurations, which are available on the native `ClientWebSocket` class, 
you have to provide the factory method as a second parameter to WebsocketClient. 
That factory method will be called on every reconnection to get a new instance of the `ClientWebSocket`. 

```csharp
var factory = new Func<ClientWebSocket>(() => new ClientWebSocket
{
    Options =
    {
        KeepAliveInterval = TimeSpan.FromSeconds(5),
        Proxy = ...
        ClientCertificates = ...
    }
});

var client = new WebsocketClient(url, factory);
client.Start();
```

Also, you can access the current native class via `client.NativeClient`. 
But use it with caution, on every reconnection there will be a new instance.

#### Change URL on the fly

It is possible to change the remote server URL dynamically. Example: 

```csharp
client.Url = new Uri("wss://my_new_url");;
await client.Reconnect();
```


### Reconnecting

A built-in reconnection invokes after 1 minute (default) of not receiving any messages from the server. 
It is possible to configure that timeout via `communicator.ReconnectTimeout`. 
In addition, a stream `ReconnectionHappened` sends information about the type of reconnection. 
However, if you are subscribed to low-rate channels, you will likely encounter that timeout - higher it to a few minutes or implement `ping-pong` interaction on your own every few seconds. 

In the case of a remote server outage, there is a built-in functionality that slows down reconnection requests 
(could be configured via `client.ErrorReconnectTimeout`, the default is 1 minute).

Usually, websocket servers do not keep a persistent connection between reconnections. Every new connection creates a new session. 
Because of that, you most likely **need to resubscribe to channels/groups/topics** inside `ReconnectionHappened` stream. 

```csharp
client.ReconnectionHappened.Subscribe(info => {
    client.Send("{type: subscribe, topic: xyz}")
});
```


### Multi-threading

Observables from Reactive Extensions are single threaded by default. It means that your code inside subscriptions is called synchronously and as soon as the message comes from websocket API. It brings a great advantage of not to worry about synchronization, but if your code takes a longer time to execute it will block the receiving method, buffer the messages and may end up losing messages. For that reason consider to handle messages on the other thread and unblock receiving thread as soon as possible. I've prepared a few examples for you: 

#### Default behavior

Every subscription code is called on a main websocket thread. Every subscription is synchronized together. No parallel execution. It will block the receiving thread. 

```csharp
client
    .MessageReceived
    .Where(msg => msg.Text != null)
    .Where(msg => msg.Text.StartsWith("{"))
    .Subscribe(obj => { code1 });

client
    .MessageReceived
    .Where(msg => msg.Text != null)
    .Where(msg => msg.Text.StartsWith("["))
    .Subscribe(arr => { code2 });

// 'code1' and 'code2' are called in a correct order, according to websocket flow
// ----- code1 ----- code1 ----- ----- code1
// ----- ----- code2 ----- code2 code2 -----
```

#### Parallel subscriptions 

Every single subscription code is called on a separate thread. Every single subscription is synchronized, but different subscriptions are called in parallel. 

```csharp
client
    .MessageReceived
    .Where(msg => msg.Text != null)
    .Where(msg => msg.Text.StartsWith("{"))
    .ObserveOn(TaskPoolScheduler.Default)
    .Subscribe(obj => { code1 });

client
    .MessageReceived
    .Where(msg => msg.Text != null)
    .Where(msg => msg.Text.StartsWith("["))
    .ObserveOn(TaskPoolScheduler.Default)
    .Subscribe(arr => { code2 });

// 'code1' and 'code2' are called in parallel, do not follow websocket flow
// ----- code1 ----- code1 ----- code1 -----
// ----- code2 code2 ----- code2 code2 code2
```

 #### Parallel subscriptions with synchronization

In case you want to run your subscription code on the separate thread but still want to follow websocket flow through every subscription, use synchronization with gates: 

```csharp
private static readonly object GATE1 = new object();
client
    .MessageReceived
    .Where(msg => msg.Text != null)
    .Where(msg => msg.Text.StartsWith("{"))
    .ObserveOn(TaskPoolScheduler.Default)
    .Synchronize(GATE1)
    .Subscribe(obj => { code1 });

client
    .MessageReceived
    .Where(msg => msg.Text != null)
    .Where(msg => msg.Text.StartsWith("["))
    .ObserveOn(TaskPoolScheduler.Default)
    .Synchronize(GATE1)
    .Subscribe(arr => { code2 });

// 'code1' and 'code2' are called concurrently and follow websocket flow
// ----- code1 ----- code1 ----- ----- code1
// ----- ----- code2 ----- code2 code2 ----
```

### Async/Await integration

Using `async/await` in your subscribe methods is a bit tricky. Subscribe from Rx.NET doesn't `await` tasks, 
so it won't block stream execution and cause sometimes undesired concurrency. For example: 

```csharp
client
    .MessageReceived
    .Subscribe(async msg => {
        // do smth 1
        await Task.Delay(5000); // waits 5 sec, could be HTTP call or something else
        // do smth 2
    });
```

That `await Task.Delay` won't block stream and subscribe method will be called multiple times concurrently. 
If you want to buffer messages and process them one-by-one, then use this: 

```csharp
client
    .MessageReceived
    .Select(msg => Observable.FromAsync(async () => {
        // do smth 1
        await Task.Delay(5000); // waits 5 sec, could be HTTP call or something else
        // do smth 2
    }))
    .Concat() // executes sequentially
    .Subscribe();
```

If you want to process them concurrently (avoid synchronization), then use this

```csharp
client
    .MessageReceived
    .Select(msg => Observable.FromAsync(async () => {
        // do smth 1
        await Task.Delay(5000); // waits 5 sec, could be HTTP call or something else
        // do smth 2
    }))
    .Merge() // executes concurrently
    // .Merge(4) you can limit concurrency with a parameter
    // .Merge(1) is same as .Concat() (sequentially)
    // .Merge(0) is invalid (throws exception)
    .Subscribe();
```

More info on [Github issue](https://github.com/dotnet/reactive/issues/459).

Don't worry about websocket connection, those sequential execution via `.Concat()` or `.Merge(1)` has no effect on receiving messages. 
It won't affect receiving thread, only buffers messages inside `MessageReceived` stream. 

But beware of [producer-consumer problem](https://en.wikipedia.org/wiki/Producer%E2%80%93consumer_problem) when the consumer will be too slow. Here is a [StackOverflow issue](https://stackoverflow.com/questions/11010602/with-rx-how-do-i-ignore-all-except-the-latest-value-when-my-subscribe-method-is/15876519#15876519) 
with an example how to ignore/discard buffered messages and always process only the last one. 


### Available for help
I do consulting, please don't hesitate to contact me if you need a paid help  
([web](http://mkotas.cz/), [nostr](https://snort.social/p/npub1dd668dyr9un9nzf9fjjkpdcqmge584c86gceu7j97nsp4lj2pscs0xk075), <m@mkotas.cz>)
