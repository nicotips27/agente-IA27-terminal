using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ECnet;

public static class NotificationHelper
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool MessageBeep(uint uType);

    // winmm!PlaySound reproduce un .wav sin dependencias extra y con SND_ASYNC no bloquea
    // el streaming de tokens. SoundPlayer de System.Windows.Extensions no viene en net8.0
    // de consola, y llamar a PowerShell por cada sonido tarda ~1 s: unacceptable en el
    // camino del permiso, que se siente inmediato.
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);

    private const uint MB_ICONHAND = 0x00000010;
    private const uint MB_ICONASTERISK = 0x00000040;
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME = 0x00020000;

    public const string DoneSound = "Notificaciones 1.wav";
    public const string ErrorSound = "Notificaciones error.wav";

    public static void NotifyModelFinished(string preview)
        => Emit(DoneSound, MB_ICONASTERISK, "MODELO_TERMINADO", preview);

    public static void NotifyPermissionRequired(string action)
        => Emit(ErrorSound, MB_ICONHAND, "PERMISO_REQUERIDO", action);

    public static void NotifyError(string error)
        => Emit(ErrorSound, MB_ICONHAND, "ERROR", error);

    private static void Emit(string fileName, uint fallbackBeep, string tipo, string mensaje)
    {
        var path = ResolveSound(fileName);
        var played = false;
        if (path is not null)
        {
            try
            {
                played = PlaySoundW(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
            }
            catch (DllNotFoundException)
            {
                played = false;
            }
            catch (EntryPointNotFoundException)
            {
                played = false;
            }
        }

        if (!played)
        {
            try
            {
                MessageBeep(fallbackBeep);
            }
            catch (Exception)
            {
            }
        }

        Log(tipo, mensaje + (path is null ? " (sin archivo de sonido: se usó el beep del sistema)" : played ? string.Empty : " (PlaySound no pudo reproducir el .wav: se usó el beep del sistema)"));
    }

    /// <summary>
    /// Busca el .wav en la carpeta "notificacion" junto al ejecutable y en los niveles de arriba.
    /// Hace falta buscar hacia arriba porque el portable de desarrollo vive en publish\ y la
    /// entrega en la raíz de la carpeta del proyecto: en los dos casos los .wav están un nivel
    /// arriba, en ia_terminal\notificacion\.
    /// </summary>
    private static string? ResolveSound(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; level < 4 && directory is not null; level++)
        {
            var candidate = Path.Combine(directory.FullName, "notificacion", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void Log(string tipo, string mensaje)
    {
        try
        {
            var logDirectory = Path.Combine(AppContext.BaseDirectory, "notificacion");
            Directory.CreateDirectory(logDirectory);
            var file = Path.Combine(logDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
            var linea = $"[{DateTime.Now:HH:mm:ss}] {tipo}: {mensaje.Replace("\r", " ").Replace("\n", " ")}";
            File.AppendAllText(file, linea + Environment.NewLine);
        }
        catch
        {
        }
    }
}
