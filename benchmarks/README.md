# Websocket.Client Benchmarks

This folder contains BenchmarkDotNet benchmarks for allocation-sensitive Websocket.Client hot paths.

The representative .NET 10 results below were captured on Windows 11, AMD Ryzen 9 3900X, .NET SDK 10.0.201, running the benchmarks on .NET 10.0.5 with BenchmarkDotNet `ShortRun`.

The .NET Framework results were captured on the same machine with .NET SDK 10.0.300, BenchmarkDotNet 0.15.8, and .NET Framework 4.8.1 through the benchmark project's `net472` target.

Run all .NET 10 benchmarks:

```powershell
dotnet run --configuration Release --framework net10.0 --project benchmarks\Websocket.Client.Benchmarks -- --filter "*"
```

Run the most relevant .NET 10 receive/send benchmarks:

```powershell
dotnet run --configuration Release --framework net10.0 --project benchmarks\Websocket.Client.Benchmarks -- --filter "*ReceiveBufferBenchmarks*"
dotnet run --configuration Release --framework net10.0 --project benchmarks\Websocket.Client.Benchmarks -- --filter "*TextSendEncodingBenchmarks*"
dotnet run --configuration Release --framework net10.0 --project benchmarks\Websocket.Client.Benchmarks -- --filter "*TraceLoggingBenchmarks*"
dotnet run --configuration Release --framework net10.0 --project benchmarks\Websocket.Client.Benchmarks -- --filter "*RequestMessageBenchmarks*"
dotnet run --configuration Release --framework net10.0 --project benchmarks\Websocket.Client.Benchmarks -- --filter "*SendSequenceFramingBenchmarks*"
```

Run the .NET Framework client receive benchmark on Windows:

```powershell
dotnet run --configuration Release --framework net472 --project benchmarks\Websocket.Client.Benchmarks -- --filter "*ClientReceiveBenchmarks*"
```

Results are written to `BenchmarkDotNet.Artifacts\results`.

## What The Benchmarks Cover

- `ReceiveBufferBenchmarks` compares the old per-message `RecyclableMemoryStream` receive path, PR #158's `ArrayBufferWriter<byte>` path, and the current pooled receive buffer. This benchmark is .NET 10-only because the comparison uses `ArrayBufferWriter<byte>`.
- `TextSendEncodingBenchmarks` compares outgoing text encoding through `Encoding.GetBytes(string)` against encoding into an `ArrayPool<byte>` buffer.
- `ResponseMessageBenchmarks` measures the `ResponseMessage.ToString()` stream-copy fix.
- `ObservablePropertyBenchmarks` measures cached observable properties versus creating an `AsObservable()` wrapper on every access.
- `TraceLoggingBenchmarks` measures disabled trace logging before and after explicit `IsEnabled(LogLevel.Trace)` guards.
- `RequestMessageBenchmarks` measures the queued text-send request envelope before and after replacing an internal class wrapper with a value type.
- `SendSequenceFramingBenchmarks` measures multi-segment `ReadOnlySequence<byte>` framing before and after removing the extra empty final websocket frame.
- `ClientReceiveBenchmarks` measures the current public `WebsocketClient` receive path using an in-memory scripted `WebSocket`.

Use the Ratio and Allocated columns for the quick answer. Values below 1.00 in Ratio are faster than the baseline, and lower Allocated values mean less GC pressure.

## Representative Results

### Receive Buffering

`ReceiveBufferBenchmarks` compares the original receive path, PR #158, and the current pooled buffer. These benchmarks include UTF-8 decoding to `string`, so large text messages are dominated by the required output string allocation.

| Message size | Before: RecyclableMemoryStream | PR #158: ArrayBufferWriter | Current: pooled buffer | Current impact |
| ---: | ---: | ---: | ---: | --- |
| 128 B | 264.17 ns, 560 B | 41.68 ns, 280 B | 36.26 ns, 280 B | 86% faster, 50% less allocation |
| 4 KB | 784.50 ns, 8496 B | 558.66 ns, 8216 B | 485.90 ns, 8216 B | 38% faster, slightly less allocation |
| 32 KB | 4.033 us, 65840 B | 4.202 us, 65560 B | 4.405 us, 65560 B | roughly neutral; output string dominates |
| 128 KB | 65.438 us, 262476 B | 64.906 us, 262196 B | 68.104 us, 262224 B | roughly neutral; avoids retaining a large receive buffer |

### Text Send Encoding

`TextSendEncodingBenchmarks` measures the change from `Encoding.GetBytes(string)` to encoding into a rented `ArrayPool<byte>` buffer before calling `WebSocket.SendAsync`.

| Message length | Before: `Encoding.GetBytes` | Current: ArrayPool encode | Current impact |
| ---: | ---: | ---: | --- |
| 32 chars | 13.24 ns, 56 B | 22.35 ns, 0 B | removes allocation with a tiny CPU cost |
| 1024 chars | 102.36 ns, 1048 B | 71.05 ns, 0 B | 31% faster, allocation-free |
| 8192 chars | 738.40 ns, 8216 B | 419.79 ns, 0 B | 43% faster, allocation-free |

### Disabled Trace Logging

`TraceLoggingBenchmarks` measures hot receive/send trace logging when the logger has trace disabled, which is the common production path.

| Scenario | Mean | Allocated | Impact |
| --- | ---: | ---: | --- |
| Before: unguarded `LogTrace` | 28.72 ns | 64 B | baseline |
| Current: `IsEnabled(LogLevel.Trace)` guard | ~0 ns | 0 B | removes disabled trace allocation entirely |

### Queued Request Messages

`RequestMessageBenchmarks` measures the queued text-send request envelope. The public `Send(string)` API is unchanged, but the internal queue item is now a value type instead of a small class hierarchy.

| Scenario | Mean | Allocated | Impact |
| --- | ---: | ---: | --- |
| Before: class request envelope | 31.54 ns | 24 B | baseline |
| Current: struct request envelope | 29.65 ns | 0 B | removes one allocation per queued text request |

### Multi-Segment Send Framing

`SendSequenceFramingBenchmarks` isolates the CPU cost of framing a multi-segment `ReadOnlySequence<byte>`. The loop cost is effectively neutral, but the current implementation sends `SegmentCount` websocket frames instead of `SegmentCount + 1` by completing the final real segment rather than sending an extra empty final frame.

| Segments | Previous frame count | Current frame count | CPU benchmark result |
| ---: | ---: | ---: | --- |
| 2 | 3 | 2 | 6.57 ns to 7.03 ns, 0 B allocated |
| 4 | 5 | 4 | 10.60 ns to 10.56 ns, 0 B allocated |
| 8 | 9 | 8 | 17.90 ns to 17.50 ns, 0 B allocated |

### Binary Message Logging

`ResponseMessageBenchmarks` measures `ResponseMessage.ToString()` for stream-backed binary messages. The previous implementation copied the stream with `ToArray()` just to print the length.

| Message size | Before: stream `ToArray()` | Current: stream `Length` | Current impact |
| ---: | ---: | ---: | --- |
| 64 B | 38.08 ns, 160 B | 38.17 ns, 96 B | less allocation, neutral CPU |
| 4 KB | 259.38 ns, 4192 B | 41.16 ns, 96 B | 84% faster, 98% less allocation |
| 32 KB | 1.149 us, 32872 B | 44.60 ns, 104 B | 96% faster, near-zero payload allocation |

### Observable Property Access

`ObservablePropertyBenchmarks` measures repeated access to public observable properties such as `MessageReceived`.

| Scenario | Mean | Allocated |
| --- | ---: | ---: |
| Before: `AsObservable()` per access | 9.27 ns | 24 B |
| Current: cached observable wrapper | 0.66 ns | 0 B |

### Current Client Receive Path

`ClientReceiveBenchmarks` measures the current public `WebsocketClient` receive flow using an in-memory scripted `WebSocket`. It is not a before/after benchmark; it is a package-level smoke benchmark for the real client path.

#### .NET 10.0

| Messages | Message size | Mean | Allocated |
| ---: | ---: | ---: | ---: |
| 100 | 64 B | 10.19 us | 25.42 KB |
| 100 | 1024 B | 25.09 us | 212.93 KB |
| 1000 | 64 B | 81.07 us | 201.21 KB |
| 1000 | 1024 B | 215.61 us | 2076.21 KB |

#### .NET Framework 4.8.1

The `net472` benchmark target consumes the library's `netstandard2.0` asset, so this measures the compatibility path used by full .NET Framework applications.

| Messages | Message size | Mean | Allocated |
| ---: | ---: | ---: | ---: |
| 100 | 64 B | 63.85 us | 51.31 KB |
| 100 | 1024 B | 137.56 us | 242.04 KB |
| 1000 | 64 B | 447.49 us | 421.71 KB |
| 1000 | 1024 B | 1.062 ms | 2312.21 KB |
