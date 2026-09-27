using Microsoft.AspNetCore.Mvc;
using YueUI.Api.Data;
using YueUI.Api.Logic;

namespace YueUI.Api;

/// <summary>
/// The Logic page's instrument library: named MIDI port and channel combinations - a drum machine among them with the
/// notes of its drums - and which track plays which. On the server, so that every browser (the MacBook in the studio
/// as well as the phone) sees the same setup. Routes and JSON are yue-to-logic-pro's, whose page this is.
/// </summary>
public static class InstrumentEndpoints
{
    /// <summary>Track names are the Logic template's ("Vocal 8vb"); anything longer is not one.</summary>
    public const int MaxTrackLength = 64;

    public static RouteGroupBuilder MapInstrumentEndpoints(this RouteGroupBuilder api)
    {
        var instruments = api.MapGroup("/instruments");
        instruments.MapGet("/", (SqliteInstrumentStore store) => Results.Ok(store.List()));
        instruments.MapPost("/", Create);
        instruments.MapPut("/{id:long}", Update);
        instruments.MapDelete("/{id:long}", (long id, SqliteInstrumentStore store) => store.Delete(id) ? Results.NoContent() : NotFound(id));
        instruments.MapGet("/assignments", (SqliteInstrumentStore store) => Results.Ok(store.Assignments()));
        instruments.MapPut("/assignments/{track}", Assign);
        return api;
    }

    private static IResult Create([FromBody] InstrumentInput input, SqliteInstrumentStore store)
    {
        if (input.Problems() is { Count: > 0 } problems)
        {
            return Invalid(problems);
        }
        try
        {
            var instrument = store.Add(input.Cleaned());
            return Results.Created($"/api/instruments/{instrument.Id}", instrument);
        }
        catch (DuplicateInstrumentNameException exception)
        {
            return Taken(exception);
        }
    }

    private static IResult Update(long id, [FromBody] InstrumentInput input, SqliteInstrumentStore store)
    {
        if (input.Problems() is { Count: > 0 } problems)
        {
            return Invalid(problems);
        }
        try
        {
            return store.Update(id, input.Cleaned()) is { } instrument ? Results.Ok(instrument) : NotFound(id);
        }
        catch (DuplicateInstrumentNameException exception)
        {
            return Taken(exception);
        }
    }

    private static IResult Assign(string track, [FromBody] TrackAssignmentInput input, SqliteInstrumentStore store)
    {
        var name = track.Trim();
        if (name.Length is 0 or > MaxTrackLength)
        {
            return Results.Problem(
                title: "Invalid track",
                detail: $"A track name has between 1 and {MaxTrackLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (input.InstrumentId is not { } id)
        {
            store.Unassign(name);
            return Results.NoContent();
        }
        return store.Assign(name, id) ? Results.NoContent() : NotFound(id);
    }

    private static IResult Invalid(IReadOnlyList<string> problems) =>
        Results.Problem(title: "Invalid instrument", detail: string.Join("; ", problems) + ".", statusCode: StatusCodes.Status400BadRequest);

    private static IResult Taken(DuplicateInstrumentNameException exception) =>
        Results.Problem(title: "Name taken", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);

    private static IResult NotFound(long id) =>
        Results.Problem(title: "Unknown instrument", detail: $"There is no instrument with id {id}.", statusCode: StatusCodes.Status404NotFound);
}
