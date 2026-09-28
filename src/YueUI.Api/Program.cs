using System.Text.Json.Serialization;
using YueUI.Api;
using YueUI.Api.Data;
using YueUI.Api.Export;
using YueUI.Api.Library;
using YueUI.Api.Logic;
using YueUI.Api.Lyrics;
using YueUI.Api.Push;
using YueUI.Api.Queue;
using YueUI.Api.Share;
using YueUI.Api.Speech;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = PublishedContentRoot(),
});

// YuE Studio's installation and song folder; see YuePaths for the defaults.
builder.Services.AddSingleton(YuePaths.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<SongLibrary>();
builder.Services.AddSingleton<TranscriptionLibrary>();
builder.Services.AddSingleton<IWorkerLauncher, PythonWorkerLauncher>();
builder.Services.AddSingleton<IStudioDetector, StudioDetector>();
builder.Services.AddSingleton(TimeProvider.System);
// One worker for the whole server; as a hosted service it is asked to quit (and free its memory) on shutdown.
builder.Services.AddSingleton<WorkerHost>();
builder.Services.AddHostedService(services => services.GetRequiredService<WorkerHost>());
// Lyrics drafts from LM Studio; loading a 15 GB model and writing take far longer than HttpClient's 100 s default allows for.
builder.Services.Configure<LyricsOptions>(builder.Configuration.GetSection(LyricsOptions.Section));
builder.Services.AddSingleton<ILmStudioStarter, LmsCli>();
builder.Services.AddSingleton<LyricsWriter>();
builder.Services.AddHttpClient(LyricsWriter.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10));
// Logic Pro projects and MIDI read back into scores, by YueToLogic.Core in this process.
builder.Services.Configure<LogicOptions>(builder.Configuration.GetSection(LogicOptions.Section));
builder.Services.AddYueToLogic();

// Sharing a song from the phone as a small AAC instead of the FLAC.
builder.Services.AddSingleton<IAudioEncoder, AacEncoder>();
// Exporting it as MP3, M4A or FLAC with title, lyrics and cover.
builder.Services.AddSingleton<IAudioTagger, TagLibTagger>();

// Web Push: notifies subscribed browsers (the app on a phone's home screen) when something finishes.
builder.Services.Configure<PushOptions>(builder.Configuration.GetSection(PushOptions.Section));
builder.Services.AddSingleton<PushStore>();
builder.Services.AddSingleton<IPushSender, WebPushSender>();
builder.Services.AddHttpClient(WebPushSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHostedService<PushNotifier>();

// The app's own state (playlists, titles, ratings, …) in one SQLite file, shared by every browser.
builder.Services.Configure<DataOptions>(builder.Configuration.GetSection(DataOptions.Section));
builder.Services.AddSingleton<SqliteDatabase>();
builder.Services.AddSingleton<SqlitePlaylistStore>();
builder.Services.AddSingleton<SqliteRunTitleStore>();
builder.Services.AddSingleton<SqliteSongRatingStore>();
builder.Services.AddSingleton<SqliteVersionStore>();
builder.Services.AddSingleton<SqliteStemStore>();
builder.Services.AddSingleton<SqliteCoverStore>();
builder.Services.AddSingleton<SqliteInstrumentStore>();
builder.Services.AddSingleton<SqliteLogicPresetStore>();
builder.Services.AddSingleton<SqliteSynthStore>();

// Songs sung with a reference voice: StemMyWav separates, ChangeMyVoice converts, ffmpeg mixes. A slow separation
// model takes three times as long as the song and answers only when it is done.
builder.Services.Configure<VoiceOptions>(builder.Configuration.GetSection(VoiceOptions.Section));
builder.Services.AddSingleton<VoiceClient>();
builder.Services.AddSingleton<StemClient>();
builder.Services.AddSingleton<IAudioMixer, FfmpegMixer>();
builder.Services.AddHttpClient(VoiceClient.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient(StemClient.HttpClientName, client => client.Timeout = TimeSpan.FromHours(2));
builder.Services.AddSingleton<VoiceConverter>();
builder.Services.AddHostedService(services => services.GetRequiredService<VoiceConverter>());
builder.Services.AddHostedService<AutoVersions>();

// The speech lab: text spoken by local models (mlx-audio in its own Python environment), one take at a time.
builder.Services.Configure<SpeechOptions>(builder.Configuration.GetSection(SpeechOptions.Section));
builder.Services.AddSingleton<SpeechActivity>();
builder.Services.AddSingleton<SqliteSpeechStore>();
builder.Services.AddSingleton<ISpeechEngine, MlxAudioEngine>();
builder.Services.AddSingleton<SpeechLab>();
builder.Services.AddHostedService(services => services.GetRequiredService<SpeechLab>());

// Songs, renders and lyrics drafts wait here for the memory instead of being refused; kept in the database.
builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.Section));
builder.Services.AddSingleton<SqliteJobStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService(services => services.GetRequiredService<JobQueue>());

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
}

app.UseStaticFiles();

app.MapOpenApi("/api/openapi");

var api = app.MapGroup("/api");
// HEAD as well, since uptime monitors often probe with it.
api.MapMethods("/health", ClientAppEndpoints.GetAndHead, () => Results.Text("ok"));
api.MapWorkerEndpoints();
api.MapLibraryEndpoints();
api.MapExportEndpoints();
api.MapCoverEndpoints();
api.MapTranscriptionEndpoints();
api.MapLyricsEndpoints();
api.MapLogicEndpoints();
api.MapInstrumentEndpoints();
api.MapSynthEndpoints();
api.MapPushEndpoints();
api.MapPlaylistEndpoints();
api.MapVoiceEndpoints();
api.MapStemEndpoints();
api.MapQueueEndpoints();
api.MapSpeechEndpoints();

app.MapClientApp();

app.Run();

// ASP.NET Core looks for appsettings.json and wwwroot in the working directory. A published app started from
// elsewhere (e.g. by launchd) would then miss its web frontend, so use the app's own directory instead.
static string? PublishedContentRoot()
{
    const string settings = "appsettings.json";
    var appDirectory = AppContext.BaseDirectory;
    return !File.Exists(Path.Combine(Directory.GetCurrentDirectory(), settings)) && File.Exists(Path.Combine(appDirectory, settings))
        ? appDirectory
        : null;
}

/// <summary>Entry point; public so that integration tests can start the app with WebApplicationFactory.</summary>
public partial class Program;
