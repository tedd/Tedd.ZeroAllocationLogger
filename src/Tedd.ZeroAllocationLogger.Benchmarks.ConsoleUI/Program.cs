using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

using Perfolizer.Horology;

namespace Tedd.ZeroAllocationLogger.Benchmarks.ConsoleUI;

internal class Program
{
    private static void Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.ShortRun.WithId("ShortRunConfig"));

        BenchmarkRunner.Run<LoggingBenchmarks>(config);

        // For dotTrace profiling
        //if (File.Exists("Test.log"))
        //    File.Delete("Test.log");
        //Log.Open("Test.log");
        //for (var i = 0; i < 1_000_000; i++)
        //    Log.Write(i);
        //Log.Close();
    }
}
