using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs; // Required for GroupBenchmarksBy
using BenchmarkDotNet.Diagnostics.dotTrace;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order; // Required for Orderer

using Microsoft.Extensions.Logging;

using Serilog;

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Tedd.ZeroAllocationLogger.Benchmarks.ConsoleUI;

[MemoryDiagnoser]
[RPlotExporter]
[CsvMeasurementsExporter]
// New attributes for structured comparison:
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)] // Group results by the categories defined below
[RankColumn] // Add a column ranking benchmarks within each group
[Orderer(SummaryOrderPolicy.FastestToSlowest)] // Order benchmarks within groups
// Add the DotTraceDiagnoser attribute to enable profiling.
// This will generate a dotTrace snapshot for each benchmark run.
//[DotTraceDiagnoser]
public class LoggingBenchmarks
{
    private const string ZeroAllocLogFileName = "ZeroAllocLog.log";
    private const string NetLoggerFileName = "NetLogger.log";

    private ILogger<LoggingBenchmarks> _netLogger;
    private ILoggerFactory _loggerFactory;

    private static readonly string TestString = "This is a test log message.";
    private static readonly byte[] TestUtf8String = Encoding.UTF8.GetBytes(TestString);
    private const int TestInt = 123456789;
    private static readonly EndPoint TestEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.1"), 8080);

    // --- Global Setup/Cleanup remains the same ---
    [GlobalSetup]
    public void GlobalSetup()
    {
        // Setup for Tedd.ZeroAllocationLogger
        if (File.Exists(ZeroAllocLogFileName))
            File.Delete(ZeroAllocLogFileName);
        Log.Open(ZeroAllocLogFileName);

        // Setup for Microsoft.Extensions.Logging
        if (File.Exists(NetLoggerFileName))
            File.Delete(NetLoggerFileName);

        var serilogLogger = new LoggerConfiguration()
            .WriteTo.File(
                NetLoggerFileName,
                buffered: true,
                outputTemplate: "{Message:lj}{NewLine}",
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: false)
            .CreateLogger();

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(logger: serilogLogger, dispose: true);
        });
        _netLogger = _loggerFactory.CreateLogger<LoggingBenchmarks>();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        Log.Close();
        if (File.Exists(ZeroAllocLogFileName))
            File.Delete(ZeroAllocLogFileName);

        _loggerFactory.Dispose();
        if (File.Exists(NetLoggerFileName))
            File.Delete(NetLoggerFileName);
    }

    // --- Benchmarks with Categories ---

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("String")]
    public void NetLogger_LogString()
    {
        _netLogger.LogInformation(TestString);
    }

    [Benchmark]
    [BenchmarkCategory("String")]
    public void ZeroAllocLogger_LogString()
    {
#pragma warning disable LOG_ALLOCATES
        Log.WriteLine(TestString);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Utf8Bytes")]
    public void NetLogger_LogUtf8Bytes()
    {
#pragma warning restore LOG_ALLOCATES
        _netLogger.LogInformation(Encoding.UTF8.GetString(TestUtf8String));
    }

    [Benchmark]
    [BenchmarkCategory("Utf8Bytes")]
    public void ZeroAllocLogger_LogUtf8Bytes()
    {
        Log.WriteLine(TestUtf8String);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Int")]
    public void NetLogger_LogInt()
    {
        _netLogger.LogInformation("{Number}", TestInt);
    }

    [Benchmark]
    [BenchmarkCategory("Int")]
    public void ZeroAllocLogger_LogInt()
    {
        Log.WriteLine(TestInt);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Complex")]
    public void NetLogger_ComplexMessage()
    {
        _netLogger.LogInformation("User {UserId} logged in from {IpAddress} at {Timestamp}", 101, TestEndPoint.ToString(), DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    [Benchmark]
    [BenchmarkCategory("Complex")]
    public void ZeroAllocLogger_ComplexMessage()
    {
        using (Log.AcquireScopedLock())
        {
            Log.Write("User "u8);
            Log.Write(101);
            Log.Write(" logged in from "u8);
            Log.Write(TestEndPoint);
            Log.Write(" at "u8);
            Log.WriteDateTimeStamp(DateTime.UtcNow);
            Log.WriteLine();
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Multithreaded")]
    [Arguments(100_000)]
    [Arguments(1_000_000)]
    public void Multithreaded_NetLogger(int numTasks)
    {
        Parallel.For(0, numTasks, i =>
        {
            _netLogger.LogInformation("Multithreaded log entry {Index}", i);
        });
    }

    [Benchmark]
    [BenchmarkCategory("Multithreaded")]
    [Arguments(100_000)]
    [Arguments(1_000_000)]
    public void Multithreaded_ZeroAllocLogger(int numTasks)
    {
        Parallel.For(0, numTasks, i =>
        {
            using (Log.AcquireScopedLock())
            {
                Log.Write("Multithreaded log entry "u8);
                Log.Write(i);
                Log.WriteLine();
            }
        });
    }
}