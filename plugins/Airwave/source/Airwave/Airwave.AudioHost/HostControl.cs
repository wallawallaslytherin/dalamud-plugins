using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airwave.AudioHost;

public sealed record HostConfiguration(string RelayUrl, string Token, float Volume = 1);
public sealed record VolumeCommand(float Volume);

public static class HostControl
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };

    public static async Task<string?> ReadBoundedLineAsync(TextReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var single = new char[1];
        while (await reader.ReadAsync(single.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
            char value = single[0];
            if (value == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length == maximumCharacters) throw new InvalidDataException("The control message is too large.");
            line.Append(value);
        }
        return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
    }

    public static HostConfiguration ParseConfiguration(string line)
    {
        var result = JsonSerializer.Deserialize<HostConfiguration>(line, JsonOptions)
            ?? throw new InvalidDataException("Missing connection configuration.");
        if (string.IsNullOrWhiteSpace(result.RelayUrl) || result.RelayUrl.Length > 2048
            || string.IsNullOrWhiteSpace(result.Token) || result.Token.Length > 256)
            throw new InvalidDataException("Invalid connection configuration.");
        ValidateVolume(result.Volume);
        return result;
    }

    public static async Task ConsumeCommandsAsync(TextReader reader, Action<float> volume, CancellationTokenSource session)
    {
        try
        {
            while (!session.IsCancellationRequested)
            {
                string? line = await ReadBoundedLineAsync(reader, 256, session.Token).ConfigureAwait(false);
                if (line is null || line == "stop") { session.Cancel(); return; }
                var command = JsonSerializer.Deserialize<VolumeCommand>(line, JsonOptions)
                    ?? throw new InvalidDataException("Invalid audio control command.");
                ValidateVolume(command.Volume);
                volume(command.Volume);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
    }

    private static void ValidateVolume(float volume)
    {
        if (!float.IsFinite(volume) || volume is < 0 or > 1) throw new InvalidDataException("Invalid listening volume.");
    }
}
