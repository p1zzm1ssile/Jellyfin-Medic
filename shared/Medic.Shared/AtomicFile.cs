using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Medic.Shared;

/// <summary>
/// Writes files without ever leaving half of one behind, and sets aside files that can't be read.
/// Medic Picks and Medic Profiles each compile this file in.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes to a temporary file, flushes it to disk, then swaps it in, so a crash or power cut mid-write leaves
    /// the old file whole. With <paramref name="ownerOnly"/>, only the account Jellyfin runs as can read the new
    /// file on Unix; Windows keeps its own folder permissions. Callers writing one file from several threads lock.
    /// </summary>
    public static void Write(string path, string contents, bool ownerOnly = false)
    {
        string tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (ownerOnly && !OperatingSystem.IsWindows())
        {
            // Permissions only apply to a file being created, so a temporary file left behind can't be reused.
            File.Delete(tmp);
            options.Mode = FileMode.CreateNew;
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(tmp, options))
        {
            stream.Write(Utf8.GetBytes(contents));
            stream.Flush(flushToDisk: true); // without this the rename can reach the disk before the data does
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Moves a file that can't be read out of the way, so the next write doesn't replace what's in it, and returns
    /// where it went: beside it, named with the time (UTC), so a later one never replaces an earlier one.
    /// </summary>
    public static string SetAside(string path)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string aside = $"{path}.{stamp}.bad";
        for (int n = 2; File.Exists(aside); n++)
        {
            aside = $"{path}.{stamp}-{n.ToString(CultureInfo.InvariantCulture)}.bad";
        }

        File.Move(path, aside);
        return aside;
    }
}
