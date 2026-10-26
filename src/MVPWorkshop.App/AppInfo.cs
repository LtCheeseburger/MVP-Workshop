using System.Reflection;

namespace MVPWorkshop.App;

/// <summary>Product name and version shown in the title bar, start page and log.</summary>
public static class AppInfo
{
    public const string Name = "MVP-Workshop";

    public static string Version { get; } = Read();

    private static string Read()
    {
        var v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
