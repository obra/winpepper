using System.Reflection;

namespace Winpepper.Core;

public static class BuildSignature
{
    public static string Describe()
    {
        var asm = typeof(BuildSignature).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var version = info?.InformationalVersion ?? "0.0.0";
#if WINPEPPER_SIGNED
        return version;
#else
        return $"{version} (unsigned build)";
#endif
    }

    public static bool IsSigned =>
#if WINPEPPER_SIGNED
        true;
#else
        false;
#endif
}
