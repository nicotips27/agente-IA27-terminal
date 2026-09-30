using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ECnet;

public static class NotificationHelper
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool MessageBeep(uint uType);

    private const uint MB_ICONHAND = 0x00000010;
    private const uint MB_ICONASTERISK = 0x00000040;

    private static readonly string LogDir = Path.Combine(AppContext.BaseDirectory, "notificacion");

    public static void NotifyModelFinished(string preview)
    {
        MessageBeep(MB_ICONASTERISK);
        Log("MODELO_TERMINADO", preview);
    }

    public static void NotifyPermissionRequired(string action)
    {
        MessageBeep(MB_ICONHAND);
        Log("PERMISO_REQUERIDO", action);
    }

    public static void NotifyError(string error)
    {
        MessageBeep(MB_ICONHAND);
        Log("ERROR", error);
    }

    private static void Log(string tipo, string mensaje)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var file = Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
            var linea = $"[{DateTime.Now:HH:mm:ss}] {tipo}: {mensaje.Replace("\r", " ").Replace("\n", " ")}";
            File.AppendAllText(file, linea + Environment.NewLine);
        }
        catch
        {
        }
    }
}
