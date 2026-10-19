namespace Gridline.Cli;

/// <summary>Process exit codes of the gridline CLI (§10.1, R29).</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Usage = 1;
    public const int FileIo = 2;
    public const int Format = 3;
    /// <summary>Exit code when a script printed at least one ERR line.</summary>
    public const int ScriptError = 4;
    public const int InvalidAddress = 5;
}
