using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tedd.ZeroAllocationLogger;
internal class Constants
{
    public const int FlushThreshold = 6_553_600; // How many bytes to write before flushing (64KB).
    public const long FlushBeforeRemapping = FlushThreshold * 100; // If we exceed this, we remap the file to a larger size (MB)
    public const long FlushBeforeRemappingSafety = FlushThreshold * 200; // This is safety margin to handle writes exceeding FlushBeforeRemapping before we have time to remap (16MB).
    public const int MaxStackAllocSize = 1024; // If string is larger than this, allocate a temporary object on heap instead of stack to convert it from Unicode.
    public const string ExperimentalAllocString = "LOG_ALLOCATES";
}
