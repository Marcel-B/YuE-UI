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
/// A song that reaches it is cut off there. Above 9000 only with YueUI's worker extension (up to 15000, ten minutes).</param>
/// <param name="Abc">A score in ABC notation that replaces the model's own plan; needs Cot "full" or "melody".</param>
/// <param name="FullSteps">Synthesis steps at full quality, 1–64; null for the model's 32. Needs the worker extension.</param>
/// <param name="AbcSampling">Overrides of how the score is sampled; needs the worker extension.</param>
/// <param name="SemanticSampling">Overrides of how the song tokens are sampled; needs the worker extension.</param>
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
    string? Abc = null,
    int? FullSteps = null,
    SamplingOverrides? AbcSampling = null,
    SamplingOverrides? SemanticSampling = null)
{
    public const int MaxBatch = 8;
    // The worker's semantic sampling insists on at least 200 tokens (min_tokens). The worker caps at 9000; the
    // extension allows 15000, the longest limit that fits the 24576-token context beside prompt and score.
    public const int MinTokens = 200;
    public const int MaxTokensLimit = 15000;
    public const int MaxFullSteps = 64;

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
        if (FullSteps is < 1 or > MaxFullSteps)
        {
            errors["fullSteps"] = [$"Between 1 and {MaxFullSteps}."];
        }
        AbcSampling?.Validate("abcSampling", errors);
        SemanticSampling?.Validate("semanticSampling", errors);
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
        if (FullSteps is { } fullSteps)
        {
            command["full_steps"] = fullSteps;
        }
        if (AbcSampling?.ToWorker() is { Count: > 0 } abcSampling)
        {
            command["abc_sampling"] = abcSampling;
        }
        if (SemanticSampling?.ToWorker() is { Count: > 0 } semanticSampling)
        {
            command["semantic_sampling"] = semanticSampling;
        }
        return command;
    }
}

/// <summary>
/// Changes to one of the model's two sampling settings (the score's and the song tokens'); null keeps the model's
/// value. The ranges are those YuE2's <c>Sampling</c> accepts, with top-k capped at a sensible 1000.
/// </summary>
public sealed record SamplingOverrides(
    double? Temperature = null,
    double? TopP = null,
    int? TopK = null,
    double? RepetitionPenalty = null,
    int? PenaltyWindow = null)
{
    public void Validate(string prefix, Dictionary<string, string[]> errors)
    {
        if (Temperature is < 0 or > 5 || Temperature is { } t && !double.IsFinite(t))
        {
            errors[$"{prefix}.temperature"] = ["Between 0 and 5."];
        }
        if (TopP is <= 0 or > 1 || TopP is { } p && !double.IsFinite(p))
        {
            errors[$"{prefix}.topP"] = ["Above 0, at most 1."];
        }
        if (TopK is < 1 or > 1000)
        {
            errors[$"{prefix}.topK"] = ["Between 1 and 1000."];
        }
        if (RepetitionPenalty is <= 0 or > 5 || RepetitionPenalty is { } r && !double.IsFinite(r))
        {
            errors[$"{prefix}.repetitionPenalty"] = ["Above 0, at most 5."];
        }
        if (PenaltyWindow is < 1 or > 100)
        {
            errors[$"{prefix}.penaltyWindow"] = ["Between 1 and 100."];
        }
    }

    /// <summary>The set values under the worker's (YuE2's) names.</summary>
    public JsonObject ToWorker()
    {
        var values = new JsonObject();
        if (Temperature is { } temperature)
        {
            values["temperature"] = temperature;
        }
        if (TopP is { } topP)
        {
            values["top_p"] = topP;
        }
        if (TopK is { } topK)
        {
            values["top_k"] = topK;
        }
        if (RepetitionPenalty is { } penalty)
        {
            values["repetition_penalty"] = penalty;
        }
        if (PenaltyWindow is { } window)
        {
            values["penalty_window"] = window;
        }
        return values;
    }
}

/// <param name="Quality">"full" (the usual: a draft at full quality) or "draft".</param>
public sealed record RenderRequest(string Quality = "full", string? Engines = null);
