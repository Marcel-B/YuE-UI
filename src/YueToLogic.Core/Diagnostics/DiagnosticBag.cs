namespace YueToLogic.Core.Diagnostics;

internal sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    public bool HasErrors => _items.Exists(d => d.Severity == DiagnosticSeverity.Error);

    public void Info(string code, string message, int? line = null, int? column = null) =>
        _items.Add(new Diagnostic(DiagnosticSeverity.Info, code, message, line, column));

    public void Warning(string code, string message, int? line = null, int? column = null) =>
        _items.Add(new Diagnostic(DiagnosticSeverity.Warning, code, message, line, column));

    public void Error(string code, string message, int? line = null, int? column = null) =>
        _items.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, line, column));

    public IReadOnlyList<Diagnostic> ToList() => _items.ToArray();
}
