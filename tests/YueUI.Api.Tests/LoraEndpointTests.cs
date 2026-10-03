using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using YueUI.Api.Loras;

namespace YueUI.Api.Tests;

public sealed class LoraEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public LoraEndpointTests() => _client = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    /// <summary>A safetensors file as the trainer writes it: header length, header with metadata, (here no) tensor bytes.</summary>
    public static byte[] Safetensors(string metadata = """{"format":"yue2-lora-v1","trigger_word":"dubstep","steps":"1500","songs":"12","minutes":"48.5"}""")
    {
        var header = Encoding.UTF8.GetBytes($$"""{"__metadata__":{{metadata}}}""");
        return [.. BitConverter.GetBytes((ulong)header.Length), .. header];
    }

    public static string WriteLora(TestApp app, string name)
    {
        var folder = Path.Combine(app.Root, "loras");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".safetensors");
        File.WriteAllBytes(path, Safetensors());
        return path;
    }

    [Fact]
    public async Task The_folders_loras_are_listed_with_what_the_trainer_noted()
    {
        WriteLora(_app, "dubstep");
        File.WriteAllBytes(Path.Combine(_app.Root, "loras", "Wobble v2.safetensors"), Safetensors("{}"));
        File.WriteAllText(Path.Combine(_app.Root, "loras", "broken.safetensors"), "not a safetensors file");
        File.WriteAllBytes(Path.Combine(_app.Root, "loras", "half.safetensors.part"), Safetensors());

        var list = (await _client.GetFromJsonAsync<LoraList>("/api/loras", TestApp.Json))!;

        Assert.Equal(Path.Combine(_app.Root, "loras"), list.Folder);
        Assert.Equal(["dubstep", "Wobble v2"], list.Loras.Select(l => l.Name));
        var dubstep = list.Loras[0];
        Assert.Equal("dubstep", dubstep.TriggerWord);
        Assert.Equal(1500, dubstep.Steps);
        Assert.Equal(12, dubstep.Songs);
        Assert.Equal(48.5, dubstep.Minutes);
        Assert.Null(list.Loras[1].TriggerWord);
    }

    [Fact]
    public async Task No_folder_means_no_loras()
    {
        var list = (await _client.GetFromJsonAsync<LoraList>("/api/loras", TestApp.Json))!;

        Assert.Empty(list.Loras);
    }

    [Fact]
    public async Task An_uploaded_lora_can_be_used_and_deleted()
    {
        var response = await _client.PostAsync("/api/loras", Upload(Safetensors(), "industrial-rock.safetensors"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("industrial-rock", (await response.Content.ReadFromJsonAsync<LoraInfo>(TestApp.Json))!.Name);
        Assert.True(File.Exists(Path.Combine(_app.Root, "loras", "industrial-rock.safetensors")));

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/loras/industrial-rock")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/loras/industrial-rock")).StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(_app.Root, "loras")));
    }

    [Fact]
    public async Task An_upload_may_be_renamed_but_not_out_of_the_folder()
    {
        var renamed = await _client.PostAsync("/api/loras", Upload(Safetensors(), "x.safetensors", "Dubstep 2"));
        var escaped = await _client.PostAsync("/api/loras", Upload(Safetensors(), "x.safetensors", "../yueui"));

        Assert.Equal(HttpStatusCode.Created, renamed.StatusCode);
        Assert.True(File.Exists(Path.Combine(_app.Root, "loras", "Dubstep 2.safetensors")));
        Assert.Equal(HttpStatusCode.BadRequest, escaped.StatusCode);
    }

    [Fact]
    public async Task Something_else_is_not_stored()
    {
        var response = await _client.PostAsync("/api/loras", Upload(Encoding.UTF8.GetBytes("<html>"), "page.safetensors"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(_app.Root, "loras")));
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string fileName, string? name = null)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent { { file, "file", fileName } };
        if (name is not null)
        {
            form.Add(new StringContent(name), "name");
        }
        return form;
    }
}
