using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Memory;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class ModelMemoryTests : IDisposable
{
    private readonly TestApp _app = new();

    private ModelMemory Memory => _app.Services.GetRequiredService<ModelMemory>();

    private WorkerHost Host => _app.Services.GetRequiredService<WorkerHost>();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void Only_one_large_model_holds_the_memory()
    {
        Assert.True(Memory.TryClaim(LargeModel.Speech));

        Assert.False(Memory.TryClaim(LargeModel.Images));
        Assert.False(Memory.TryClaim(LargeModel.Speech));
        Assert.Equal(LargeModel.Speech, Memory.Holder);

        // Giving back what another model holds changes nothing.
        Memory.Release(LargeModel.Images);
        Assert.Equal(LargeModel.Speech, Memory.Holder);

        Memory.Release(LargeModel.Speech);
        Memory.Release(LargeModel.Speech);
        Assert.Null(Memory.Holder);
        Assert.True(Memory.TryClaim(LargeModel.Images));
    }

    [Fact]
    public void A_song_on_its_way_to_the_worker_keeps_the_memory_for_yue2()
    {
        Host.ExpectSongs();

        Assert.False(Memory.TryClaim(LargeModel.Voices));
        Assert.Null(Memory.Holder);

        Host.ExpectNoSongs();
        Assert.True(Memory.TryClaim(LargeModel.Voices));
    }

    [Fact]
    public void Claims_race_and_exactly_one_wins()
    {
        // Resolved before the race: the factory builds the app on first use, and not safely from several threads at once.
        var memory = Memory;
        var models = Enum.GetValues<LargeModel>();
        using var start = new Barrier(models.Length);

        var won = models.AsParallel().WithDegreeOfParallelism(models.Length).Where(model =>
        {
            start.SignalAndWait();
            return memory.TryClaim(model);
        }).ToList();

        Assert.Equal([memory.Holder!.Value], won);
    }

    [Fact]
    public void The_snapshot_says_who_holds_the_memory_and_subscribers_hear_it_change()
    {
        using var subscription = Host.Subscribe(checkStudio: false);
        Assert.Null(subscription.Snapshot.Memory!.Holder);

        Memory.TryClaim(LargeModel.Lyrics);
        Memory.Release(LargeModel.Lyrics);

        Assert.True(subscription.Reader.TryRead(out var claimed));
        Assert.Equal(("memory", LargeModel.Lyrics), (claimed.Type, ((MemoryInfo)claimed.Data).Holder));
        Assert.True(subscription.Reader.TryRead(out var released));
        Assert.Null(((MemoryInfo)released.Data).Holder);
        Assert.Null(Host.Snapshot().Memory!.Holder);
    }

    [Fact]
    public async Task Busy_names_the_model_that_holds_the_memory()
    {
        Memory.TryClaim(LargeModel.Images);

        var busy = await _app.CreateClient().GetFromJsonAsync<BusyInfo>("/api/busy", TestApp.Json);

        Assert.True(busy!.Busy);
        Assert.Equal(["images"], busy.Reasons);
    }
}
