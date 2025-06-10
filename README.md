# Tedd.ZeroAllocationLogger

## Zero allocating file logger

Uses native memory mapping of log file and various techniques do a fully allocation free buffered write to log file.

## Use case

This library is for very special use cases where you want to completely avoid heap allocations. Normally writing logs involves a lot of temporary string allocations. Though these are short lived and quickly cleaned up by the GC, they do add overhead to your application. This library is severely limited, only providing the most basic of logging functionality, but does not allocate anything and minimizes memory copying.

## How it works

Use the operating system memory paging system to map the log file into the applications memory area. This creates a virtual 1-1 mapping between the applications memory and the file on disk.

Write-commands for data types uses various techniques to write directly to this memory area. For instance an Int32 will be written as its string representation "-12345", not its underlying bytes. Since the string representation is variable length, a stack allocated buffer is used for temporary write, then the bytes are copied to memory area.

This means you have to make one write call per datatype. If you are doing multithreading you should fetch a lock object to ensure consistency in the writes.

## Buffered write

You can't tail this log file.

Why? Once file is opened, its size is immediately extended by 128MB (sparse records). If log has >65MB appended it will extend the file size with another 65MB. Once file is closed, whatever slack at the end of the file remains will be released. Since tail looks at file size, not content, it will not be able to follow the logs.

File is flushed when 6.4MB of data has been written to it, and when it is closed.

## Examples

### Opening and closing file

```c#
Log.Open("Log.txt");
Log.WriteDateTimeStamp();
Log.WriteLine(" Hello"u8);
Log.Close();
```

Note that the file is opened as shared, other applications can read from it while it is being used. But you can't open it for shared from within the same process as you are writing with.

### Strings

Strings may cause allocation (if they are more than 1024 bytes), so they are tagged as experimental. They also have to be converted from Unicode to bytes, causing extra processing and extra copy operations.

You need to explicitly disable the check for writing a string.

```c#
#pragma warning disable LOG_ALLOCATES
Log.WriteLine("Test");
#pragma warning restore LOG_ALLOCATES
```

Instead consider using u8 datatype which avoids both allocation and Unicode conversion:

```c#
Log.WriteLine("Test"u8);
```

### Thread safety

If you are writing to log with multiple threads you may want to lock the log. This will block other threads while the writing happens, so keep it short.

```c#
using (Log.AcquireScopedLock())
{
    Log.WriteDateTimeStamp(DateTime.UtcNow);
	Log.Write("Client "u8);
	Log.Write(socket.RemoteEndPoint);
	Log.WriteLine(" connected.");
}
```

# Benchmarks

Using this logger is a tradeoff between features and speed. Serilog is very fast, and I do recommend using it over this library.

That being said, this library clocks in at 3-5 times faster than Serilog through the Microsoft ILogger interface (buffered write).

```

BenchmarkDotNet v0.15.1, Windows 11 (10.0.26100.4061/24H2/2024Update/HudsonValley)
Unknown processor
.NET SDK 9.0.300
  [Host]         : .NET 9.0.5 (9.0.525.21509), X64 RyuJIT AVX2 [AttachedDebugger]
  ShortRunConfig : .NET 9.0.5 (9.0.525.21509), X64 RyuJIT AVX2

Job=ShortRunConfig  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | numTasks | Mean              | Error             | StdDev           | Ratio | RatioSD | Rank | Gen0       | Gen1      | Allocated   | Alloc Ratio |
|------------------------------- |--------- |------------------:|------------------:|-----------------:|------:|--------:|-----:|-----------:|----------:|------------:|------------:|
| ZeroAllocLogger_ComplexMessage | ?        |         289.36 ns |         696.48 ns |        38.176 ns |  0.33 |    0.04 |    1 |          - |         - |           - |        0.00 |
| NetLogger_ComplexMessage       | ?        |         876.48 ns |         611.17 ns |        33.500 ns |  1.00 |    0.05 |    2 |     0.0572 |         - |       968 B |        1.00 |
|                                |          |                   |                   |                  |       |         |      |            |           |             |             |
| ZeroAllocLogger_LogInt         | ?        |         207.99 ns |       2,351.47 ns |       128.892 ns |  0.55 |    0.30 |    1 |          - |         - |           - |        0.00 |
| NetLogger_LogInt               | ?        |         378.14 ns |          20.50 ns |         1.124 ns |  1.00 |    0.00 |    2 |     0.0319 |         - |       536 B |        1.00 |
|                                |          |                   |                   |                  |       |         |      |            |           |             |             |
| Multithreaded_ZeroAllocLogger  | 100000   |  12,887,498.96 ns |  32,417,771.33 ns | 1,776,927.443 ns |  0.33 |    0.04 |    1 |          - |         - |     11952 B |       0.000 |
| Multithreaded_NetLogger        | 100000   |  39,373,289.74 ns |   5,718,814.10 ns |   313,467.499 ns |  1.00 |    0.01 |    2 |  3230.7692 |  230.7692 |  53612634 B |       1.000 |
|                                |          |                   |                   |                  |       |         |      |            |           |             |             |
| Multithreaded_ZeroAllocLogger  | 1000000  |  92,837,614.29 ns | 100,821,689.42 ns | 5,526,377.027 ns |  0.24 |    0.01 |    1 |          - |         - |     11955 B |       0.000 |
| Multithreaded_NetLogger        | 1000000  | 392,107,766.67 ns |  19,836,275.10 ns | 1,087,293.177 ns |  1.00 |    0.00 |    2 | 32000.0000 | 1000.0000 | 536011176 B |       1.000 |
|                                |          |                   |                   |                  |       |         |      |            |           |             |             |
| ZeroAllocLogger_LogString      | ?        |          98.53 ns |         390.85 ns |        21.424 ns |  0.29 |    0.06 |    1 |          - |         - |           - |        0.00 |
| NetLogger_LogString            | ?        |         334.41 ns |          11.81 ns |         0.647 ns |  1.00 |    0.00 |    2 |     0.0272 |         - |       456 B |        1.00 |
|                                |          |                   |                   |                  |       |         |      |            |           |             |             |
| ZeroAllocLogger_LogUtf8Bytes   | ?        |          78.52 ns |         340.52 ns |        18.665 ns |  0.21 |    0.04 |    1 |          - |         - |           - |        0.00 |
| NetLogger_LogUtf8Bytes         | ?        |         373.72 ns |          12.40 ns |         0.680 ns |  1.00 |    0.00 |    2 |     0.0319 |         - |       536 B |        1.00 |
