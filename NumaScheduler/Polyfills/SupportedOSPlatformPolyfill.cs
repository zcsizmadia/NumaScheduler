#if !NET5_0_OR_GREATER

// Stub implementations of the platform-guard attributes introduced in .NET 5.
// These are purely compile-time / analyzer annotations — the classes carry no
// runtime behaviour, so a no-op stub is fully correct for older targets.

namespace System.Runtime.Versioning
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class SupportedOSPlatformAttribute : Attribute
    {
        public SupportedOSPlatformAttribute(string platformName) { }
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class UnsupportedOSPlatformAttribute : Attribute
    {
        public UnsupportedOSPlatformAttribute(string platformName) { }
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class ObsoletedOSPlatformAttribute : Attribute
    {
        public ObsoletedOSPlatformAttribute(string platformName) { }
        public ObsoletedOSPlatformAttribute(string platformName, string message) { }
    }
}

#endif
