using System.Net;

#pragma warning disable LOGSTRING

namespace Tedd.ZeroAllocationLogger.Tests;

public class StaticLoggerTests : IDisposable
{
    private readonly string _testFileName = "Test.log";

    public StaticLoggerTests()
    {
        // Setup - clean up any existing test file
        if (File.Exists(_testFileName))
            File.Delete(_testFileName);
    }

    public void Dispose()
    {
        // Clean up after each test
        Log.Close();
        if (File.Exists(_testFileName))
            File.Delete(_testFileName);
    }

    [Fact]
    public void SimpleWriteTest()
    {
        var filename = "Test.log";
        if (File.Exists(filename))
            File.Delete(filename);

        Log.Open(filename);
        Assert.True(File.Exists(filename));

        Log.WriteLine("Test1"u8);
        Log.WriteLine("Test2"u8);
        Log.Close();

        var lines = File.ReadAllLines(filename);
        Assert.Equal(2, lines.Length);
        Assert.Equal("Test1", lines[0]);
        Assert.Equal("Test2", lines[1]);
    }

    [Fact]
    public void WritePrimitiveTypes()
    {
        Log.Open(_testFileName);

        // Write different primitive types
        Log.WriteLine(42); // int
        Log.WriteLine(42.5f); // float
        Log.WriteLine(true); // bool
        Log.WriteLine((byte)255); // byte

        Log.Close();

        var lines = File.ReadAllLines(_testFileName);
        Assert.Equal(4, lines.Length);
        Assert.Equal("42", lines[0]);
        Assert.Equal("42.5", lines[1]);
        Assert.Equal("True", lines[2]);
        Assert.Equal("255", lines[3]);
    }

    [Fact]
    public void WriteWithoutNewline()
    {
        Log.Open(_testFileName);

        Log.Write("Hello "u8);
        Log.Write("World"u8);
        Log.WriteLine();
        Log.Write("Next Line"u8);

        Log.Close();

        var content = File.ReadAllText(_testFileName);
        Assert.Contains("Hello World", content);
        Assert.Contains("Next Line", content);
    }

    [Fact]
    public void DateTimeFormatting()
    {
        Log.Open(_testFileName);

        var testDate = new DateTime(2023, 12, 25, 13, 14, 15, 678);
        Log.WriteDate(testDate);
        Log.WriteLine();
        Log.WriteTime(testDate);
        Log.WriteLine();
        Log.WriteTimeMs(testDate);

        Log.Close();

        var lines = File.ReadAllLines(_testFileName);
        Assert.Equal(3, lines.Length);
        Assert.Equal("2023-12-25", lines[0]);
        Assert.Equal("13:14:15", lines[1]);
        Assert.Equal("13:14:15.678", lines[2]);
    }

    [Fact]
    public void TimestampIncludesProperFormat()
    {
        Log.Open(_testFileName);

        Log.WriteDateTimeStamp(DateTime.UtcNow);
        Log.WriteLine("Message after timestamp"u8);

        Log.Close();

        var content = File.ReadAllText(_testFileName);
        // Check that timestamp format is correct [yyyy-MM-dd HH:mm:ss]
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] Message after timestamp", content);
    }

    [Fact]
    public void AtomicOperationsWithScopedLock()
    {
        Log.Open(_testFileName);

        using (var token = Log.AcquireScopedLock())
        {
            Log.Write("This "u8);
            Log.Write("is "u8);
            Log.Write("atomic"u8);
            Log.WriteLine();
        }

        Log.Close();

        var content = File.ReadAllText(_testFileName);
        Assert.Contains("This is atomic", content);
    }

    [Fact]
    public void ParallelWriting()
    {
        Log.Open(_testFileName);

        // Perform parallel writes to test thread safety
        Parallel.For(0, 100, i =>
        {
#pragma warning disable LOG_ALLOCATES
            Log.WriteLine($"Line {i}");
#pragma warning restore LOG_ALLOCATES
        });

        Log.Flush();
        Log.Close();

        var lines = File.ReadAllLines(_testFileName);
        Assert.Equal(100, lines.Length);

        // Verify all lines were written
        for (int i = 0; i < 100; i++)
        {
            Assert.Contains($"Line {i}", lines);
        }
    }

    [Fact]
    public void FlushWorks()
    {
        Log.Open(_testFileName);

        Log.WriteLine("Line before flush"u8);
        Log.Flush();

        // Use the Log's internal access to read content instead of File.ReadAllText
#pragma warning disable LOG_ALLOCATES
        string content = Log.ReadCurrentContent();
#pragma warning restore LOG_ALLOCATES
        Assert.Contains("Line before flush", content);

        Log.Close();
    }

    [Fact]
    public void LogFileAppend()
    {
        Log.Open(_testFileName);
        Log.Write("1234"u8);
        Log.Close();

        Log.Open(_testFileName);
        Log.Write("5678"u8);
        Log.Close();

        var content = File.ReadAllText(_testFileName);
        Assert.Equal("12345678", content);
    }

    [Fact]
    public void LogFileSizeTruncating()
    {
        Log.Open(_testFileName);
        Log.Write("1234"u8);
        Log.Close();
        Assert.Equal(4, new FileInfo(_testFileName).Length);

        Log.Open(_testFileName);
        Log.Write("5678"u8);
        Log.Close();
        Assert.Equal(8, new FileInfo(_testFileName).Length);
    }


    [Fact]
    public void LogIPv4()
    {
        var ep = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234);
        Log.Open(_testFileName);
        Log.WriteLine(ep);
        Log.Close();

        var data = File.ReadAllLines(_testFileName);
        Assert.True(data.Length > 0);
        Assert.Contains(ep.ToString(), data[0]);
    }
    [Fact]
    public void LogIPv6()
    {
        var ep = new IPEndPoint(IPAddress.Parse("FE80::210:5aff:feaa:20a2"), 1234);
        Log.Open(_testFileName);
        Log.WriteLine(ep);
        Log.Close();

        var data = File.ReadAllLines(_testFileName);
        Assert.True(data.Length > 0);
        Assert.Contains(ep.ToString(), data[0]);
    }
}
