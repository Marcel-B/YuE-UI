using YueUI.Api.Data;
using YueUI.Api.Library;

namespace YueUI.Api;

/// <param name="SongIds">Song ids (<c>run/songN</c>) in the order they play.</param>
public sealed record PlaylistInfo(IReadOnlyList<string> SongIds);

/// <summary>The playlist: read it, or replace it as a whole (adding, removing and moving are all done in the browser).</summary>
public static class PlaylistEndpoints
{
    public static RouteGroupBuilder MapPlaylistEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/playlist", (SqlitePlaylistStore store, SongLibrary library) => new PlaylistInfo(Existing(store.SongIds(), library)));

        api.MapPut("/playlist", (PlaylistInfo request, SqlitePlaylistStore store, SongLibrary library) =>
        {
            var songIds = request.SongIds ?? [];
            if (songIds.FirstOrDefault(id => !IsSongId(id, library)) is { } invalid)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["songIds"] = [$"Not a song of the library: {invalid}"],
                });
            }
            // One entry per song, as the page shows it; a song twice would play twice after reordering.
            store.Replace([.. songIds.Distinct(StringComparer.Ordinal)]);
            return Results.Ok(new PlaylistInfo(store.SongIds()));
        });
        return api;
    }

    /// <summary>A deleted song stays in the database (it costs nothing) but is not handed out.</summary>
    private static IReadOnlyList<string> Existing(IEnumerable<string> songIds, SongLibrary library) =>
        [.. songIds.Where(id => IsSongId(id, library))];

    private static bool IsSongId(string? id, SongLibrary library) =>
        id?.Split('/') is [var run, var song] && library.SongDirectory(run, song) is not null;
}
