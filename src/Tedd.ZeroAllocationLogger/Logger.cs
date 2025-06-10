using System;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Tedd.ZeroAllocationLogger;

/// <summary>
/// A high-performance, thread-safe logger using a cached pointer to a memory-mapped file.
/// This implementation uses System.Threading.Lock and provides a disposable token for atomic multi-write operations.
/// Note: Requires compilation with the <AllowUnsafeBlocks>true</AllowUnsafeBlocks> project setting.
/// </summary>
public unsafe class Logger
{
    private string? _filePath;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private byte* _basePtr = null; // The cached base pointer.

    private long _capacity;
    private long _position;
    private int _unflushedBytes;
    private long _remapAt;

    private readonly System.Threading.Lock _lock = new(); // Use the new, dedicated Lock object from .NET 9 for optimized locking.
    private readonly ReadOnlyMemory<byte> NewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine).AsMemory();
    private readonly ReadOnlyMemory<byte> TrueBytes = "True"u8.ToArray().AsMemory();
    private readonly ReadOnlyMemory<byte> FalseBytes = "False"u8.ToArray().AsMemory();
    private readonly ReadOnlyMemory<byte> SPACE = " "u8.ToArray().AsMemory();

    public void Open(string filePath)
    {
        _filePath = filePath;
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or whitespace.", nameof(filePath));

        lock (_lock)
        {
            var newFileSize = GetMemoryMappedFileAllocationSize(filePath);
            OpenMemoryMapped(filePath, newFileSize);

            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; // Unsubscribe first to prevent duplicates
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
    }

    public void Close()
    {
        CloseMemoryMapped();
    }

    private static long SafeGetFileLength(string filePath)
    {
        var fi = new FileInfo(filePath);
        if (fi.Exists)
            return fi.Length;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetMemoryMappedFileAllocationSize(string filePath) => SafeGetFileLength(filePath) + Constants.FlushBeforeRemappingSafety;


    #region Locking

    /// <summary>
    /// A disposable token representing an acquired lock for atomic logging operations.
    /// Use with a 'using' statement to ensure the lock is always released.
    /// </summary>
#pragma warning disable CA1815
#pragma warning disable CA1034
    public readonly struct ScopedLockToken(Lock @lock) : IDisposable
#pragma warning restore CA1034
#pragma warning restore CA1815
    {
        public void Dispose()
        {
            @lock.Exit();
        }
    }

    /// <summary>
    /// Acquires a lock for a transactional scope, allowing multiple writes to be treated as an atomic operation.
    /// </summary>
    /// <returns>A disposable token that releases the lock upon disposal.</returns>
    public ScopedLockToken AcquireScopedLock()
    {
        _lock.Enter();
        return new ScopedLockToken(_lock);
    }

    #endregion

    #region Write
    // The core write method is now significantly leaner.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> data)
    {
        InternalWrite(data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSpace() => InternalWrite(SPACE.Span);

    private void InternalWrite(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        //lock (_lock)
        {
            //EnsureInitialized();
            //if (_position + data.Length > _capacity)
            //{
            //    _position = 0; // Circular buffer
            //}
            if (_position + data.Length > _capacity)
#pragma warning disable CA2201
                throw new OutOfMemoryException(
                    $"Log line would exceed the memory mapped file capacity: Current position {_position} + data.Length {data.Length} = {_position + data.Length} > capacity {_capacity}");
#pragma warning restore CA2201

            // Directly use the cached pointer.
            byte* destinationPtr = _basePtr + _position;
            var destinationSpan = new Span<byte>(destinationPtr, data.Length);
            data.CopyTo(destinationSpan);

            //_position += data.Length;
            Interlocked.Add(ref _position, data.Length);

            //_unflushedBytes += data.Length;
            Interlocked.Add(ref _unflushedBytes, data.Length);
            CheckFlushThreshold();
        }
    }

    #region Lifecycle and Helper Methods
    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        Dispose(true);
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseMemoryMapped();
        }
    }
    #endregion

    #region Open/Close Memory Mapped File
    private void OpenMemoryMapped(string filePath, long maxSizeInBytes)
    {
        // Dispose of existing resources before re-initializing.
        lock (_lock)
        {
            CloseMemoryMapped();

            _capacity = maxSizeInBytes;
            _position = SafeGetFileLength(filePath);
            _unflushedBytes = 0;
            _remapAt = _position + Constants.FlushBeforeRemapping; // Remap when we exceed this size.

            try
            {
                using var fileStream = new FileStream(
                    filePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read); // Allow other processes to read the file

                _mmf = MemoryMappedFile.CreateFromFile(
                    fileStream,
                    null, // mapName
                    _capacity,
                    MemoryMappedFileAccess.ReadWrite,
                    HandleInheritability.None,
                    leaveOpen: false);

                _accessor = _mmf.CreateViewAccessor(0, _capacity, MemoryMappedFileAccess.Write);

                // Acquire the pointer once and cache it for reuse.
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);
            }
            catch
            {
                // Ensure cleanup happens on failure during initialization.
                Dispose(true);
                throw;
            }
        }
    }

    private void CloseMemoryMapped()
    {
        lock (_lock)
        {
            InternalFlush();
            var wasOpen = false;
            // Release the pointer FIRST, while the accessor is still valid.
            if (_basePtr != null && _accessor != null)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _basePtr = null;
                wasOpen = true;
            }

            if (_accessor != null)
            {
                if (_unflushedBytes > 0) _accessor.Flush();
                _accessor.Dispose();
                _accessor = null;
            }

            if (_mmf != null)
            {
                _mmf.Dispose();
                _mmf = null;
            }

            // Truncate the file to the actual log length (_position)
            if (!wasOpen) return;

            using var fs = new FileStream(_filePath!, FileMode.Open, FileAccess.Write, FileShare.Read);
            fs.SetLength(_position);
        }
    }

    private void CheckFlushThreshold()
    {
        if (_unflushedBytes < Constants.FlushThreshold)
            return;

        lock (_lock)
        {
            if (_unflushedBytes >= Constants.FlushThreshold)
            {
                InternalFlush();
            }

            if (_position >= _remapAt)
            {
                CheckRemap();
            }
        }
    }

    private void CheckRemap()
    {
        if (_position < _remapAt)
            return;

        lock (_lock)
        {
            if (_position < _remapAt)
                return;

            CloseMemoryMapped();
            var size = GetMemoryMappedFileAllocationSize(_filePath!);
            OpenMemoryMapped(_filePath!, size);
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            InternalFlush();
        }
    }

    private void InternalFlush()
    {
        _accessor?.Flush();
        _unflushedBytes = 0;
    }

    #endregion

    // String writing
    [Experimental(Constants.ExperimentalAllocString)]
    public void Write(string data)
    {
        if (string.IsNullOrEmpty(data)) return;
        Write(data.AsSpan()); // Create a span view of the string (no allocation)
    }

    [Experimental(Constants.ExperimentalAllocString)]
    public void Write(ReadOnlySpan<char> data)
    {
        if (data.IsEmpty) return;

        // Calculate required size for the UTF-8 destination buffer
        var byteCount = Encoding.UTF8.GetByteCount(data);

        // STRATEGY 1: Use the stack for small buffers
        if (byteCount <= Constants.MaxStackAllocSize)
        {
            Span<byte> buffer = stackalloc byte[byteCount]; // Allocation on the STACK
            Encoding.UTF8.GetBytes(data, buffer);           // Perform transformation
            InternalWrite(buffer);                             // Write the result
        }
        else // STRATEGY 2: Use the pool for large buffers
        {
            byte[] rentedArray = ArrayPool<byte>.Shared.Rent(byteCount); // Rent from POOL
            try
            {
                Span<byte> buffer = rentedArray.AsSpan(0, byteCount);
                Encoding.UTF8.GetBytes(data, buffer);
                InternalWrite(buffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedArray); // Return to pool for reuse
            }
        }
    }


    // --- Core Data Writing Methods ---

    #region Integral Type Overloads

    public void Write(byte data)
    {
        Span<byte> buffer = stackalloc byte[3]; // Max: "255"
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }

    public void Write(sbyte data)
    {
        Span<byte> buffer = stackalloc byte[4]; // Max: "-128"
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }

    public void Write(short data)
    {
        Span<byte> buffer = stackalloc byte[6]; // Max: "-32768"
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }

    public void Write(ushort data)
    {
        Span<byte> buffer = stackalloc byte[5]; // Max: "65535"
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int data)
    {
        Span<byte> buffer = stackalloc byte[11]; // Max: "-2147483648"
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }

    public void Write(uint data)
    {
        Span<byte> buffer = stackalloc byte[10]; // Max: "4294967295"
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }

    public void Write(long data)
    {
        Span<byte> buffer = stackalloc byte[20]; // Max: "-9223372036854775808"
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }

    public void Write(ulong data)
    {
        Span<byte> buffer = stackalloc byte[20]; // Max: "18446744073709551615"
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }

    #endregion

    #region Floating-Point Type Overloads

    public void Write(float data)
    {
        // Utf8Formatter for float/double can be verbose. 32 is a safe buffer size.
        Span<byte> buffer = stackalloc byte[32];
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }

    public void Write(double data)
    {
        // Utf8Formatter for float/double can be verbose. 32 is a safe buffer size.
        Span<byte> buffer = stackalloc byte[32];
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }

    public void Write(decimal data)
    {
        // Decimal has the largest string representation.
        Span<byte> buffer = stackalloc byte[32];
        if (Utf8Formatter.TryFormat(data, buffer, out int bytesWritten)) InternalWrite(buffer.Slice(0, bytesWritten));
    }

    #endregion

    #region Other Primitive Types

    public void Write(bool data) => InternalWrite(data ? TrueBytes.Span : FalseBytes.Span);
    [Experimental(Constants.ExperimentalAllocString)]
    public void Write(char data)
    {
        Span<char> charSpan = stackalloc char[1];
        charSpan[0] = data;
        Write(charSpan);
    }

    public void Write(EndPoint endPoint)
    {
        Span<byte> buffer = stackalloc byte[64]; // Allocate a buffer large enough for most endpoints
        EndPointFormatter.WriteEndPointAscii(endPoint, buffer);
        InternalWrite(buffer.Slice(0, buffer.IndexOf((byte)0))); // Write only the used portion
    }

    #endregion

    #region DateTime
    /// <summary>
    /// Writes the date portion of a DateTime to the log in yyyy-MM-dd format.
    /// </summary>
    public void WriteDate(DateTime dt)
    {
        Span<byte> buffer = stackalloc byte[10];
        Utf8Formatter.TryFormat(dt.Year, buffer.Slice(0, 4), out _, new StandardFormat('D', 4));
        buffer[4] = (byte)'-';
        Utf8Formatter.TryFormat(dt.Month, buffer.Slice(5, 2), out _, new StandardFormat('D', 2));
        buffer[7] = (byte)'-';
        Utf8Formatter.TryFormat(dt.Day, buffer.Slice(8, 2), out _, new StandardFormat('D', 2));
        InternalWrite(buffer);
    }

    /// <summary>
    /// Writes the time portion of a DateTime to the log in HH:mm:ss format.
    /// </summary>
    public void WriteTime(DateTime dt)
    {
        Span<byte> buffer = stackalloc byte[8];
        Utf8Formatter.TryFormat(dt.Hour, buffer.Slice(0, 2), out _, new StandardFormat('D', 2));
        buffer[2] = (byte)':';
        Utf8Formatter.TryFormat(dt.Minute, buffer.Slice(3, 2), out _, new StandardFormat('D', 2));
        buffer[5] = (byte)':';
        Utf8Formatter.TryFormat(dt.Second, buffer.Slice(6, 2), out _, new StandardFormat('D', 2));
        InternalWrite(buffer);
    }

    /// <summary>
    /// Writes the time portion of a DateTime to the log in HH:mm:ss.fff format.
    /// </summary>
    public void WriteTimeMs(DateTime dt)
    {
        Span<byte> buffer = stackalloc byte[12];
        Utf8Formatter.TryFormat(dt.Hour, buffer.Slice(0, 2), out _, new StandardFormat('D', 2));
        buffer[2] = (byte)':';
        Utf8Formatter.TryFormat(dt.Minute, buffer.Slice(3, 2), out _, new StandardFormat('D', 2));
        buffer[5] = (byte)':';
        Utf8Formatter.TryFormat(dt.Second, buffer.Slice(6, 2), out _, new StandardFormat('D', 2));
        buffer[8] = (byte)'.';
        Utf8Formatter.TryFormat(dt.Millisecond, buffer.Slice(9, 3), out _, new StandardFormat('D', 3));
        InternalWrite(buffer);
    }

    /// <summary>
    /// Writes a full timestamp of the current local time in [yyyy-MM-dd HH:mm:ss] format, including a trailing space.
    /// </summary>
    public void WriteDateTimeStamp(DateTime date)
    {
        Span<byte> buffer = stackalloc byte[22];


        buffer[0] = (byte)'[';
        Utf8Formatter.TryFormat(date.Year, buffer.Slice(1, 4), out _, new StandardFormat('D', 4));
        buffer[5] = (byte)'-';
        Utf8Formatter.TryFormat(date.Month, buffer.Slice(6, 2), out _, new StandardFormat('D', 2));
        buffer[8] = (byte)'-';
        Utf8Formatter.TryFormat(date.Day, buffer.Slice(9, 2), out _, new StandardFormat('D', 2));
        buffer[11] = (byte)' ';
        Utf8Formatter.TryFormat(date.Hour, buffer.Slice(12, 2), out _, new StandardFormat('D', 2));
        buffer[14] = (byte)':';
        Utf8Formatter.TryFormat(date.Minute, buffer.Slice(15, 2), out _, new StandardFormat('D', 2));
        buffer[17] = (byte)':';
        Utf8Formatter.TryFormat(date.Second, buffer.Slice(18, 2), out _, new StandardFormat('D', 2));
        buffer[20] = (byte)']';
        buffer[21] = (byte)' ';

        InternalWrite(buffer);
    }
    #endregion

    #region WriteLine Overloads

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine() => InternalWrite(NewLineBytes.Span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Experimental(Constants.ExperimentalAllocString)]
    public void WriteLine(string data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(byte data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(sbyte data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(short data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(ushort data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(int data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(uint data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(long data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(ulong data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(float data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(double data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(decimal data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(bool data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Experimental(Constants.ExperimentalAllocString)]
    public void WriteLine(char data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(ReadOnlySpan<byte> data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(EndPoint data) { using (AcquireScopedLock()) { Write(data); WriteLine(); } }

    #endregion
    #endregion
    /// <summary>
    /// Returns the current content of the log file without closing it.
    /// Useful for testing and monitoring.
    /// </summary>
    [Experimental(Constants.ExperimentalAllocString)]
    public string ReadCurrentContent()
    {
        lock (_lock)
        {
            if (_basePtr == null || _position == 0)
                return string.Empty;

            InternalFlush(); // Ensure all content is flushed
            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(_basePtr, (int)_position));
        }
    }

}