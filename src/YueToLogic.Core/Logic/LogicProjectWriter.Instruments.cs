using System.Buffers.Binary;
using System.Text;
using YueToLogic.Core.Diagnostics;

namespace YueToLogic.Core.Logic;

/// <summary>
/// Apple's External Instrument on the tracks that play hardware: the plug-in that sends a track's MIDI to one
/// output of the Mac's MIDI interfaces, on one channel.
/// </summary>
/// <remarks>
/// Learned from a project Logic Pro 12.3 saved with eight such tracks. A channel strip's plug-ins are <c>AuCU</c>
/// chunks (class 14) whose header names the strip at byte 14 and the slot at byte 18; the instrument sits in the
/// lowest slot. Which strip an environment object is, stands right behind its name, counted from one. The
/// External Instrument's state is 568 bytes: a 144-byte plug-in header, the destination's display name at 196,
/// its position in Logic's output list (counted from one, 0 = none) at 336, the MIDI channel at 344, the
/// output's CoreMIDI entry - unique id, port name, device name, the same 100 bytes as in the <c>CorM</c> list of
/// outputs - at 448, and an instance UUID at 548. A plug-in is not in the object registry, so swapping one
/// needs no entry there.
/// </remarks>
public sealed partial class LogicProjectWriter
{
    /// <summary>Plug-in instances and channel-strip objects: audio objects, class 14.</summary>
    private const ushort AudioObjectClass = 14;

    /// <summary>Header byte 10 of a plug-in instance; strip objects and CoreMIDI lists carry other values.</summary>
    private const uint PluginKind = 36;

    private const int PluginStripOffset = 14;
    private const int PluginSlotOffset = 18;

    /// <summary>Where the plug-in's name stands in its state ("External"), to recognize the instrument slot.</summary>
    private const int PluginNameOffset = 120;

    private const int DestinationNameOffset = 196;
    private const int DestinationNameLength = 128;
    private const int DestinationIndexOffset = 336;
    private const int DestinationChannelOffset = 344;
    private const int DestinationEntryOffset = 448;
    private const int InstanceUuidOffset = 548;

    /// <summary>A CoreMIDI list: a count at byte 2, then entries of an int32 unique id, a 64-byte port name and a 32-byte device name.</summary>
    private const int CoreMidiCountOffset = 2;
    private const int CoreMidiEntriesOffset = 6;
    private const int CoreMidiEntryLength = 100;
    private const int CoreMidiPortNameOffset = 4;
    private const int CoreMidiPortNameLength = 64;
    private const int CoreMidiDeviceNameOffset = 68;
    private const int CoreMidiDeviceNameLength = 32;

    private static readonly Lazy<byte[]> ExternalInstrumentState = new(LoadExternalInstrumentState);

    /// <summary>One of the MIDI outputs the template's Mac had, named as Web MIDI names it in the browser.</summary>
    private readonly record struct MidiOutput(int Index, string Name, byte[] Entry);

    /// <summary>
    /// Puts an External Instrument on every track whose instrument names a MIDI output the template knows,
    /// in place of the software instrument the template had there, sending to that output on the track's
    /// channel. A track whose output the template does not list keeps its instrument, with a warning: Logic
    /// finds an output by the unique id its Mac gave it, which only a project saved on that Mac can supply.
    /// </summary>
    private void WriteExternalInstruments(List<LogicChunk> chunks, IReadOnlyList<TemplateTrack> tracks, DiagnosticBag diagnostics)
    {
        var routed = tracks.Where(t => t.Port is not null).ToList();
        if (routed.Count == 0)
        {
            return;
        }

        var outputs = MidiOutputs(chunks);
        var strips = chunks
            .Where(c => c.Tag == "Envi" && c.Class == EnvironmentClass && c.Payload.Length > EnvironmentNameOffset + 2)
            .ToDictionary(c => c.Id);
        var timestamp = timeProvider.GetUtcNow().UtcTicks - LogicObjectRegistry.UuidEpochTicks;

        foreach (var track in routed)
        {
            var output = outputs.FirstOrDefault(o => string.Equals(o.Name, track.Port, StringComparison.OrdinalIgnoreCase));
            if (output.Entry is null)
            {
                diagnostics.Warning(
                    DiagnosticCodes.MidiPortUnknown,
                    $"Track '{track.Region}': the MIDI output '{track.Port}' of instrument '{track.Instrument}' is not among the outputs the Logic template knows ({string.Join(", ", outputs.Select(o => o.Name))}), so the track keeps the template's instrument. A template saved with that interface connected takes it as well.");
                continue;
            }

            if (!strips.TryGetValue(track.Environment, out var strip) || InstrumentSlot(chunks, StripNumber(strip)) is not { } slot)
            {
                diagnostics.Warning(
                    DiagnosticCodes.LogicTemplateLimitation,
                    $"Track '{track.Region}' has no instrument slot in the Logic template, so instrument '{track.Instrument}' could not be put on it.");
                continue;
            }

            slot.Payload = ExternalInstrument(output, track.Channel + 1, timestamp++);
        }
    }

    /// <summary>The MIDI outputs of the template's Mac, from the last CoreMIDI list, which Logic writes after the inputs.</summary>
    private static List<MidiOutput> MidiOutputs(List<LogicChunk> chunks)
    {
        var list = chunks.Where(c => c.Tag == "CorM" && c.Class == AudioObjectClass).OrderBy(c => c.Id).LastOrDefault();
        if (list is null)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(list.Payload.AsSpan(CoreMidiCountOffset, 2));
        var outputs = new List<MidiOutput>();
        for (var index = 0; index < count; index++)
        {
            var offset = CoreMidiEntriesOffset + (index * CoreMidiEntryLength);
            if (offset + CoreMidiEntryLength > list.Payload.Length)
            {
                break;
            }

            var entry = list.Payload.AsSpan(offset, CoreMidiEntryLength).ToArray();
            var port = FixedString(entry, CoreMidiPortNameOffset, CoreMidiPortNameLength);
            var device = FixedString(entry, CoreMidiDeviceNameOffset, CoreMidiDeviceNameLength);
            outputs.Add(new MidiOutput(index, DisplayName(device, port), entry));
        }

        return outputs;
    }

    /// <summary>
    /// How Web MIDI names a port in the browser: device and port, or the one name where they are the same,
    /// as CoreMIDI composes the display name.
    /// </summary>
    private static string DisplayName(string device, string port) =>
        device.Length == 0 || string.Equals(device, port, StringComparison.Ordinal) ? port : device + " " + port;

    /// <summary>The channel strip's number, which its plug-ins carry in their headers: stored behind the name, counted from one.</summary>
    private static uint StripNumber(LogicChunk strip)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(strip.Payload.AsSpan(EnvironmentNameOffset, 2));
        var offset = EnvironmentNameOffset + 2 + length + (length & 1);
        return offset + 2 <= strip.Payload.Length ? BinaryPrimitives.ReadUInt16LittleEndian(strip.Payload.AsSpan(offset, 2)) - 1u : uint.MaxValue;
    }

    /// <summary>The plug-in in the strip's instrument slot: the lowest slot among its plug-ins.</summary>
    private static LogicChunk? InstrumentSlot(List<LogicChunk> chunks, uint strip) =>
        chunks
            .Where(c => c.Tag == "AuCU" && c.Class == AudioObjectClass
                && ReadUInt32(c.Header, 10) == PluginKind && ReadUInt32(c.Header, PluginStripOffset) == strip)
            .OrderBy(c => ReadUInt32(c.Header, PluginSlotOffset))
            .FirstOrDefault();

    /// <summary>The External Instrument's state, sending to <paramref name="output"/> on <paramref name="channel"/> (1-16).</summary>
    private static byte[] ExternalInstrument(MidiOutput output, int channel, long timestamp)
    {
        var state = (byte[])ExternalInstrumentState.Value.Clone();
        state.AsSpan(DestinationNameOffset, DestinationNameLength).Clear();
        var name = Encoding.UTF8.GetBytes(output.Name);
        name.AsSpan(0, Math.Min(name.Length, DestinationNameLength - 1)).CopyTo(state.AsSpan(DestinationNameOffset));
        WriteUInt32(state, DestinationIndexOffset, (uint)output.Index + 1);
        WriteUInt32(state, DestinationChannelOffset, (uint)channel);
        output.Entry.CopyTo(state.AsSpan(DestinationEntryOffset, CoreMidiEntryLength));
        LogicObjectRegistry.NewTimeBasedUuid(timestamp, Random.Shared).Uuid.CopyTo(state.AsSpan(InstanceUuidOffset, 16));
        return state;
    }

    private static string FixedString(byte[] buffer, int offset, int length)
    {
        var field = buffer.AsSpan(offset, length);
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]).Trim();
    }

    private static byte[] LoadExternalInstrumentState()
    {
        using var stream = typeof(LogicProjectWriter).Assembly.GetManifestResourceStream("YueToLogic.ExternalInstrument")
            ?? throw new InvalidOperationException("The External Instrument state is missing from the library.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var state = buffer.ToArray();
        if (state.Length != InstanceUuidOffset + 16 + 4 || !state.AsSpan(PluginNameOffset).StartsWith("External\0"u8))
        {
            throw new InvalidOperationException("The External Instrument state in the library has an unexpected layout.");
        }

        return state;
    }
}
