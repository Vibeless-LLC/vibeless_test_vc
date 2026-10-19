namespace Gridline.Cli;

/// <summary>A failure with a definite exit code; <see cref="CliApp"/> turns it into one `error:` stderr line.</summary>
public sealed class CliException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
