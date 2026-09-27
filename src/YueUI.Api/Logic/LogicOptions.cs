namespace YueUI.Api.Logic;

/// <summary>How songs become Logic Pro projects. Configured under <c>Logic</c> (<c>Logic__SplitSections</c> as an environment variable).</summary>
public sealed class LogicOptions
{
    public const string Section = "Logic";

    /// <summary>One region per song section (verse, chorus, …) instead of one per track.</summary>
    public bool SplitSections { get; set; }
}
