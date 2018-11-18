using System.Runtime.InteropServices;
using Xunit;

namespace FastTests
{
    public class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) == false)
            {
                Skip = "Test can be run only on Windows machine";
            }
        }
    }
}
