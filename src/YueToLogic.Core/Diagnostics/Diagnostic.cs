using System.Globalization;

namespace YueToLogic.Core.Diagnostics;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A message produced while reading a score. Diagnostics are returned as data so that every host
/// (CLI, web service, desktop app) can decide how to present or localize them; <see cref="Code"/> is stable.
/// </summary>
/// <param name="Line">1-based line in the ABC source, if the message refers to a location.</param>
/// <param name="Column">1-based column in the ABC source, if the message refers to a location.</param>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    int? Line = null,
    int? Column = null)
{
    public override string ToString()
    {
        var location = (Line, Column) switch
        {
            ({ } line, { } column) => string.Create(CultureInfo.InvariantCulture, $" (line {line}, column {column})"),
            ({ } line, null) => string.Create(CultureInfo.InvariantCulture, $" (line {line})"),
            _ => string.Empty,
        };
        return $"{Severity.ToString().ToLowerInvariant()} {Code}{location}: {Message}";
    }
}
