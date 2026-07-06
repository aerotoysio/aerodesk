namespace AeroDesk.Core.Settings;

/// <summary>Crash-safe file writes: write to a temp file, then move over the target.</summary>
public static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
