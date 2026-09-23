using System.Text.Json.Nodes;

namespace YueUI.Api;

/// <summary>A new run: what the web form sends. Maps onto the worker's <c>generate</c> command.</summary>
/// <param name="Batch">Songs with consecutive seeds; the worker tokenizes up to four together at the cost of one.</param>
/// <param name="Quality">"draft" (few synthesis steps, minutes) or "full".</param>
/// <param name="Cot">How the model plans before the audio: "full" (melody and structure), "melody" or "off".</param>
/// <param name="Seed">Null for a random one.</param>
/// <param name="Engines">"gpu" or "gpu+ane"; null leaves the worker's choice (Neural Engine for full quality).</param>
/// <param name="DraftSteps">Synthesis steps of a draft, 1–32 (the worker defaults to 8).</param>
/// <param name="MaxTokens">Upper limit of song tokens (25 per second of audio); null for the worker's 9000 (six minutes).
/// A song that reaches it is cut off there.</param>
/// <param name="Abc">A score in ABC notation that replaces the model's own plan; needs Cot "full" or "melody".</param>
public sealed record GenerateRequest(
    string Style,
    string Lyrics,
    string? Title = null,
    int Batch = 1,
    string Quality = "draft",
    string Cot = "full",
    long? Seed = null,
    bool Instrumental = false,
    string? Engines = null,
    int? DraftSteps = null,
    int? MaxTokens = null,
    string? Abc = null)
{
    public const int MaxBatch = 8;
    public const int TokensPerSecond = 25;
    // The worker's semantic sampling insists on at least 200 tokens (min_tokens) and caps at 9000.
    public const int MinTokens = 200;
    public const int MaxTokensLimit = 9000;

    private bool HasAbc => !string.IsNullOrWhiteSpace(Abc);

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(Style))
        {
            errors["style"] = ["A style is required."];
        }
        // An instrumental keeps only the lyrics' section tags, and without any the worker uses
        // [Intro] [Verse] [Chorus] [Outro].
        if (string.IsNullOrWhiteSpace(Lyrics) && !Instrumental)
        {
            errors["lyrics"] = ["Lyrics are required (except for an instrumental)."];
        }
        if (Batch is < 1 or > MaxBatch)
        {
            errors["batch"] = [$"Between 1 and {MaxBatch} songs."];
        }
        if (Quality is not ("draft" or "full"))
        {
            errors["quality"] = ["Either 'draft' or 'full'."];
        }
        if (Cot is not ("full" or "melody" or "off"))
        {
            errors["cot"] = ["One of 'full', 'melody' or 'off'."];
        }
        if (Seed is < 0)
        {
            errors["seed"] = ["Not negative."];
        }
        if (Engines is not (null or "gpu" or "gpu+ane"))
        {
            errors["engines"] = ["Either 'gpu' or 'gpu+ane'."];
        }
        if (DraftSteps is < 1 or > 32)
        {
            errors["draftSteps"] = ["Between 1 and 32."];
        }
        if (MaxTokens is < MinTokens or > MaxTokensLimit)
        {
            errors["maxTokens"] = [$"Between {MinTokens} and {MaxTokensLimit}."];
        }
        // The model only reads a score in a planning mode; an instrumental switches "off" to "full" itself.
        if (HasAbc && Cot == "off" && !Instrumental)
        {
            errors["abc"] = ["A score needs planning 'full' or 'melody'."];
        }
        return errors;
    }

    public JsonObject ToWorkerCommand()
    {
        var command = new JsonObject
        {
            ["cmd"] = "generate",
            ["style"] = Style.Trim(),
            ["lyrics"] = Lyrics?.Trim() ?? "",
            ["title"] = Title?.Trim() ?? "",
            ["batch"] = Batch,
            ["quality"] = Quality,
            ["cot"] = Cot,
            ["instrumental"] = Instrumental,
        };
        if (Seed is { } seed)
        {
            command["seed"] = seed;
        }
        else
        {
            command["random_seed"] = true;
        }
        if (Engines is not null)
        {
            command["engines"] = Engines;
        }
        if (DraftSteps is { } steps)
        {
            command["draft_steps"] = steps;
        }
        if (MaxTokens is { } maxTokens)
        {
            command["max_tokens"] = maxTokens;
        }
        if (HasAbc)
        {
            command["abc"] = Abc!.Trim();
        }
        return command;
    }
}

/// <param name="Quality">"full" (the usual: a draft at full quality) or "draft".</param>
public sealed record RenderRequest(string Quality = "full", string? Engines = null);
