using System.Text.Json;
using Airwave.Core;

namespace Airwave;

internal sealed class AirwaveUserException(string message) : Exception(message);

internal sealed record ConnectionDraft(string RelayUrl, string ListenKey, string PublishKey)
{
    public static ConnectionDraft Validate(string relay, string listen, string publish,
        bool requireListener = false, bool requirePublisher = false, params string[] savedBroadcastKeys)
    {
        var draft = new ConnectionDraft(relay.Trim(), listen.Trim(), publish.Trim());
        if ((requireListener || requirePublisher) && draft.RelayUrl.Length == 0)
            throw new AirwaveUserException("Paste a listener invite in Listen, or enter the relay address in Connection.");
        try { if (draft.RelayUrl.Length > 0) _ = ConnectionPolicy.Endpoint(draft.RelayUrl, requirePublisher); }
        catch (ArgumentException)
        { throw new AirwaveUserException("Check the relay address in Connection. Use its wss:// base address without a path, sign-in details, or extra parameters."); }
        ValidateKey(draft.ListenKey, requireListener, "listener");
        ValidateKey(draft.PublishKey, requirePublisher, "broadcast");
        try { ListenerInvite.ValidateKeySeparation(draft.ListenKey, [draft.PublishKey, .. savedBroadcastKeys]); }
        catch (ArgumentException)
        { throw new AirwaveUserException("The listener key must differ from the broadcast key. Get the separate listener key before saving or sharing."); }
        return draft;
    }

    public static ConnectionDraft FromInvite(string invite, params string[] knownBroadcastKeys)
    {
        (string RelayUrl, string ListenKey) parsed;
        try { parsed = ListenerInvite.Parse(invite); }
        catch (FormatException)
        { throw new AirwaveUserException("That listener invite is incomplete or invalid. Copy the full Airwave invite from the broadcaster and try again."); }
        return Validate(parsed.RelayUrl, parsed.ListenKey, "", requireListener: true, savedBroadcastKeys: knownBroadcastKeys);
    }

    private static void ValidateKey(string key, bool required, string role)
    {
        if (key.Length == 0 && !required) return;
        try { _ = ConnectionPolicy.ValidateToken(key); }
        catch (ArgumentException)
        { throw new AirwaveUserException($"The {role} key is missing or invalid. Copy the full {role} key from the relay operator into Connection."); }
    }
}

internal static class UiState
{
    public static string Session(bool publishing, bool running, string stage, JsonElement? status)
    {
        if (stage == "Error") return "Needs attention";
        if (!running) return publishing ? "Off air" : "Not listening";
        return stage switch
        {
            "OnAir" => "On air",
            "Listening" => "Playing live audio",
            "Buffering" => Number(status, "FirstAudioMilliseconds") is not null ? "Buffering audio…" : "Preparing playback…",
            "Capturing" => "Waiting for rekordbox audio…",
            "Connecting" => "Connecting to relay…",
            _ => "Starting audio…",
        };
    }

    public static string? Problem(JsonElement? status, bool publishing)
    {
        if (status is not { } value || value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("Error", out var error) || error.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(error.GetString())) return null;
        var message = error.GetString();
        if (Enum.GetValues<RelayConnectionFailure>().Any(failure => message == RelayConnectionException.MessageFor(failure))) return message;
        return message switch
        {
            "Rekordbox capture stopped or could not start." => "Rekordbox capture stopped. Keep rekordbox open and enable PC MASTER OUT for an ASIO controller, then start again.",
            "The playback device could not be opened or stopped unexpectedly." => "Playback stopped. Check the Windows default output device and its volume, then join again.",
            "The relay connection failed or ended." => publishing
                ? "The relay connection ended. Check the relay address, broadcast key and network, then go on air again."
                : "The relay connection ended. Check that the broadcaster is on air and the invite is current, then join again.",
            "The audio connection timed out." => "The connection timed out. Check the relay and your network, then try again.",
            "The audio process could not access a required device or process." => "Audio access failed. Check that rekordbox and the Windows playback device are available, then try again.",
            "Invalid audio, connection settings, or control data." => "Audio settings or data were rejected. Check Connection or get a new invite, then try again.",
            "The audio process stopped responding. Audio has been stopped." or "The status output stopped responding." => "The audio engine stopped responding. Stop all audio, then try again.",
            _ => "The audio session stopped. Check the connection and audio device, then try again.",
        };
    }

    public static double? Number(JsonElement? status, string property) =>
        status is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var number) &&
        number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var result) && double.IsFinite(result) ? result : null;

    public static bool AudioLoss(JsonElement? status) => Number(status, "NativeCaptureDroppedFrames") > 0 ||
        Number(status, "DroppedFrames") > 0 || Number(status, "Underruns") > 0;

    public static bool PublisherReadyForLocalListener(bool running, string stage) => running && stage == "OnAir";

    public static string? CaptureReadiness(bool supportedWindows, bool audioAvailable, bool captureAvailable) =>
        !supportedWindows ? "Broadcasting and the local sound test need Windows 11 or newer; listening is still available."
        : !audioAvailable || !captureAvailable ? "Reinstall the complete Airwave plugin to restore its audio components." : null;
}
