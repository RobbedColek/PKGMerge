namespace PKGMerge.Models;

public enum StatusSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record StatusMessage(StatusSeverity Severity, string Text, bool FromMerge = false)
{
    public bool IsInfo => Severity == StatusSeverity.Info;

    public bool IsSuccess => Severity == StatusSeverity.Success;

    public bool IsWarning => Severity == StatusSeverity.Warning;

    public bool IsError => Severity == StatusSeverity.Error;
}
