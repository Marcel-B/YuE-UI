using System.Text.Json.Serialization;
using YueUI.Api;
using YueUI.Api.Library;
using YueUI.Api.Logic;
using YueUI.Api.Lyrics;
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
// Logic Pro projects from yue-to-logic-pro; uploading a long FLAC to it and building the project take minutes.
builder.Services.Configure<LogicOptions>(builder.Configuration.GetSection(LogicOptions.Section));
builder.Services.AddHttpClient(LogicEndpoints.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10));

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
api.MapTranscriptionEndpoints();
api.MapLyricsEndpoints();
api.MapLogicEndpoints();

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
