using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;

namespace YueUI.Api.Loras;

/// <summary>A LoRA for YuE2's acoustic path, as the form offers it.</summary>
/// <param name="Name">The file name without <c>.safetensors</c>; what a <see cref="GenerateRequest"/> names.</param>
/// <param name="TriggerWord">The word the trainer put in every caption; it belongs in the style of songs made with it.</param>
/// <param name="Steps">Training steps; a checkpoint file (<c>name-step500</c>) carries its own.</param>
/// <param name="Songs">Songs it was trained on, and their minutes, when this app's trainer made it.</param>
public sealed record LoraInfo(
    string Name,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    string? TriggerWord,
    int? Steps,
    int? Songs,
    double? Minutes);

/// <summary>Where LoRAs are looked for, and the ones there.</summary>
public sealed record LoraList(string Folder, IReadOnlyList<LoraInfo> Loras);

/// <summary>
/// The LoRA files in <c>loras/</c> next to the database: what <c>deploy/train-lora.sh</c> writes there, or a file put
/// there by hand or uploaded (one from Hugging Face in ComfyUI's layout works too, see <c>Worker/yueui_lora.py</c>).
/// The worker gets a file's absolute path; it reads the weights itself.
/// </summary>
public sealed partial class LoraLibrary(IOptions<DataOptions> data)
{
    public const string Extension = ".safetensors";

    /// <summary>A rank 32 LoRA on every NAR projection is about 140 MB; leave room for larger ranks.</summary>
    public const long MaxUploadBytes = 1L << 30;

    public string Folder => Path.Combine(Path.GetDirectoryName(data.Value.ResolvedPath)!, "loras");

    public IReadOnlyList<LoraInfo> List()
    {
        var folder = new DirectoryInfo(Folder);
        if (!folder.Exists)
        {
            return [];
        }
        return
        [
            .. folder.EnumerateFiles("*" + Extension)
                .Where(f => ValidName(Path.GetFileNameWithoutExtension(f.Name)))
                .Select(Describe)
                .OfType<LoraInfo>()
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The file of the LoRA called <paramref name="name"/>, or null if there is none (or the name is not one).</summary>
    public string? PathFor(string name)
    {
        if (!ValidName(name))
        {
            return null;
        }
        var path = Path.Combine(Folder, name + Extension);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Letters, digits, spaces, dots, dashes and underscores; also keeps names inside the folder.</summary>
    public static bool ValidName(string name) => NamePattern().IsMatch(name) && !name.Contains("..");

    /// <summary>Stores an upload under <paramref name="name"/>, replacing a LoRA of that name.</summary>
    /// <returns>The stored LoRA, or null if the content is not a safetensors file.</returns>
    public async Task<LoraInfo?> SaveAsync(string name, Stream content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name + Extension);
        // The worker might read the old file while the new one arrives: it only ever sees a complete file.
        var temporary = path + ".upload";
        try
        {
            await using (var file = File.Create(temporary))
            {
                await content.CopyToAsync(file, cancellationToken);
            }
            if (ReadMetadata(temporary) is null)
            {
                return null;
            }
            File.Move(temporary, path, overwrite: true);
            return Describe(new FileInfo(path));
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public bool Delete(string name)
    {
        if (PathFor(name) is not { } path)
        {
            return false;
        }
        File.Delete(path);
        return true;
    }

    private static LoraInfo? Describe(FileInfo file)
    {
        if (ReadMetadata(file.FullName) is not { } metadata)
        {
            return null;                    // half written by the trainer, or not a safetensors file
        }
        string? Text(string key) => metadata.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
        int? Whole(string key) => int.TryParse(Text(key), out var value) ? value : null;
        double? Number(string key) =>
            double.TryParse(Text(key), System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
        return new LoraInfo(
            Path.GetFileNameWithoutExtension(file.Name), file.Length, file.LastWriteTimeUtc,
            Text("trigger_word"), Whole("steps"), Whole("songs"), Number("minutes"));
    }

    /// <summary>
    /// The <c>__metadata__</c> of a safetensors file (empty when it has none), or null when the file is not one: eight
    /// bytes of header length (little endian), then the header as a JSON object.
    /// </summary>
    public static Dictionary<string, string>? ReadMetadata(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> prefix = stackalloc byte[8];
            if (file.ReadAtLeast(prefix, 8, throwOnEndOfStream: false) < 8)
            {
                return null;
            }
            var length = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
            if (length is < 2 or > 100_000_000 || (long)length > file.Length - 8)
            {
                return null;
            }
            var header = new byte[length];
            file.ReadExactly(header);
            using var json = JsonDocument.Parse(header);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var metadata = new Dictionary<string, string>();
            if (json.RootElement.TryGetProperty("__metadata__", out var values) && values.ValueKind == JsonValueKind.Object)
            {
                foreach (var value in values.EnumerateObject())
                {
                    metadata[value.Name] = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : value.Value.GetRawText();
                }
            }
            return metadata;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,99}$")]
    private static partial Regex NamePattern();
}
