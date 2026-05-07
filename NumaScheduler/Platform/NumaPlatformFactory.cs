using System.Runtime.InteropServices;

namespace NumaScheduler.Platform;

/// <summary>
/// Returns the <see cref="INumaPlatform"/> implementation appropriate for the
/// current operating system.  The singleton is resolved once at process startup.
/// </summary>
internal static class NumaPlatformFactory
{
    private static readonly INumaPlatform _instance = Create();

    internal static INumaPlatform GetPlatform() => _instance;

    private static INumaPlatform Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsNumaPlatform();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxNumaPlatform();

        // macOS, FreeBSD, and any other platforms use the graceful fallback.
        return new FallbackNumaPlatform();
    }
}
