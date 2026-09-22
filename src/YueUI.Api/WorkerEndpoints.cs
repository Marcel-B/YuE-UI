using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using YueUI.Api.Library;
using YueUI.Api.Worker;

namespace YueUI.Api;

/// <summary>Starting, rendering and cancelling songs, and the live state of the worker (snapshot and event stream).</summary>
public static class WorkerEndpoints
{
    /// <summary>
    /// A comment-free "ping" event keeps idle streams open: proxies such as <c>tailscale serve</c> drop connections
    /// that stay silent, and a phone's browser notices a dead one only when it next reads.
    /// </summary>
    public static TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(20);

    public static RouteGroupBuilder MapWorkerEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/status", (WorkerHost host) => host.Snapshot());
        api.MapGet("/events", (WorkerHost host, CancellationToken cancellationToken) =>
            TypedResults.ServerSentEvents(Stream(host, cancellationToken)));

        api.MapPost("/generate", Generate);
        api.MapPost("/songs/{run}/{song}/render", Render);
        api.MapPost("/songs/{run}/{song}/cancel", async (string run, string song, WorkerHost host, CancellationToken cancellationToken) =>
            await host.CancelAsync($"{run}/{song}", cancellationToken)
                ? Results.Accepted()
                : Results.Problem(title: "Not in the queue", statusCode: StatusCodes.Status404NotFound));
        api.MapPost("/stop", async (WorkerHost host, CancellationToken cancellationToken) =>
        {
            await host.StopAllAsync(cancellationToken);
            return Results.Accepted();
        });
        api.MapPost("/worker/shutdown", async (WorkerHost host) =>
        {
            await host.ShutdownWorkerAsync();
            return Results.NoContent();
        });
        return api;
    }

    private static async Task<IResult> Generate(GenerateRequest request, WorkerHost host, CancellationToken cancellationToken)
    {
        var errors = request.Validate();
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        return await Send(() => host.GenerateAsync(request.ToWorkerCommand(), cancellationToken));
    }

    private static async Task<IResult> Render(
        string run, string song, RenderRequest? request, SongLibrary library, WorkerHost host, CancellationToken cancellationToken)
    {
        request ??= new RenderRequest();
        if (request.Quality is not ("draft" or "full") || request.Engines is not (null or "gpu" or "gpu+ane"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["quality"] = ["Either 'draft' or 'full', engines 'gpu' or 'gpu+ane'."] });
        }
        if (library.SongDirectory(run, song) is not { } directory)
        {
            return Results.Problem(title: "No such song", statusCode: StatusCodes.Status404NotFound);
        }
        if (!File.Exists(Path.Combine(directory, "semantic.npy")))
        {
            return Results.Problem(title: "The song's tokens were not saved, so it cannot be rendered again.", statusCode: StatusCodes.Status409Conflict);
        }
        return await Send(() => host.RenderAsync(directory, request.Quality, request.Engines, cancellationToken));
    }

    /// <summary>The worker answers asynchronously (its "started" event reaches the browsers), so a sent command is 202.</summary>
    private static async Task<IResult> Send(Func<Task> send)
    {
        try
        {
            await send();
            return Results.Accepted();
        }
        catch (WorkerUnavailableException exception)
        {
            return Results.Problem(title: "YuE Studio not found", detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async IAsyncEnumerable<SseItem<object>> Stream(WorkerHost host, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = host.Subscribe();
        yield return new SseItem<object>(subscription.Snapshot, "snapshot");

        while (!cancellationToken.IsCancellationRequested)
        {
            ServerEvent? next;
            using (var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                heartbeat.CancelAfter(Heartbeat);
                try
                {
                    next = await subscription.Reader.ReadAsync(heartbeat.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    next = null;
                }
                catch (ChannelClosedException)
                {
                    // Dropped for not keeping up: ending the stream makes the EventSource reconnect with a new snapshot.
                    yield break;
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
            yield return next is null ? new SseItem<object>(new { }, "ping") : new SseItem<object>(next.Data, next.Type);
        }
    }
}
