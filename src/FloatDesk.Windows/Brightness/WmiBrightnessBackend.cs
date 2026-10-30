using System.Runtime.InteropServices;

namespace FloatDesk.Windows.Brightness;

internal static class WmiBrightnessBackend
{
    internal sealed record Screen(string Instance, int Percent);
    internal static List<Screen> Enumerate()
    {
        var screens = new List<Screen>();
        Query("SELECT * FROM WmiMonitorBrightness", item =>
        {
            if ((bool)item.Active) screens.Add(new((string)item.InstanceName, (byte)item.CurrentBrightness));
        });
        return screens;
    }

    internal static bool Set(string instance, int percent)
    {
        var success = false;
        Query("SELECT * FROM WmiMonitorBrightnessMethods", item =>
        {
            if (!string.Equals((string)item.InstanceName, instance, StringComparison.OrdinalIgnoreCase)) return;
            success = Convert.ToUInt32(item.WmiSetBrightness(0u, (byte)percent)) == 0;
        });
        return success;
    }

    private static void Query(string query, Action<dynamic> visit)
    {
        object? locator = null, service = null, collection = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!);
            service = ((dynamic)locator!).ConnectServer(".", "root\\wmi");
            collection = ((dynamic)service).ExecQuery(query);
            foreach (var item in (System.Collections.IEnumerable)collection)
                try { visit(item); } finally { Release(item); }
        }
        finally { Release(collection); Release(service); Release(locator); }
    }
    private static void Release(object? value)
    { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
