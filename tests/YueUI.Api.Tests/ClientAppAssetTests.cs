using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace YueUI.Api.Tests;

public sealed class ClientAppAssetTests : IDisposable
{
    private static readonly byte[] Script = "console.log('plain')"u8.ToArray();
    private static readonly byte[] Brotli = [1, 2, 3];
    private static readonly byte[] Gzip = [4, 5, 6];

    private readonly TestApp _app = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ClientAppAssetTests()
    {
        var webRoot = Path.Combine(_app.Root, "wwwroot");
        var assets = Directory.CreateDirectory(Path.Combine(webRoot, "ui", "assets")).FullName;
        File.WriteAllBytes(Path.Combine(assets, "index-abc.js"), Script);
        File.WriteAllBytes(Path.Combine(assets, "index-abc.js.br"), Brotli);
        File.WriteAllBytes(Path.Combine(assets, "index-abc.js.gz"), Gzip);
        File.WriteAllBytes(Path.Combine(assets, "font-abc.woff2"), [7, 8]);
        File.WriteAllText(Path.Combine(webRoot, "ui", "version.json"), """{"build":"x"}""");
        _factory = _app.WithWebHostBuilder(builder => builder.UseWebRoot(webRoot));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _app.Dispose();
    }

    [Theory]
    [InlineData("gzip, deflate, br", "br")]
    [InlineData("gzip, deflate", "gzip")]
    [InlineData("br;q=0, gzip", "gzip")]
    public async Task A_script_comes_precompressed_in_the_best_encoding_the_browser_accepts(string accept, string expected)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/ui/assets/index-abc.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", accept);

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([expected], response.Content.Headers.ContentEncoding);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(expected == "br" ? Brotli : Gzip, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Without_an_accepted_encoding_or_a_compressed_copy_the_file_is_sent_as_it_is()
    {
        var client = _factory.CreateClient();

        var script = await client.GetAsync("/ui/assets/index-abc.js");
        var font = new HttpRequestMessage(HttpMethod.Get, "/ui/assets/font-abc.woff2");
        font.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");
        var fontResponse = await client.SendAsync(font);

        Assert.Empty(script.Content.Headers.ContentEncoding);
        Assert.Equal(Script, await script.Content.ReadAsByteArrayAsync());
        Assert.Empty(fontResponse.Content.Headers.ContentEncoding);
        Assert.Equal([7, 8], await fontResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", fontResponse.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Files_without_a_hash_in_their_name_are_not_cached_for_good()
    {
        var response = await _factory.CreateClient().GetAsync("/ui/version.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.CacheControl);
    }
}
