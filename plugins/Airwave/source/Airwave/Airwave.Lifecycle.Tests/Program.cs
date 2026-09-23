using System.Diagnostics;
using System.Text.Json;
using Airwave;
using Airwave.Core;

var exe = Path.Combine(AppContext.BaseDirectory, "Airwave.Lifecycle.Tests.exe");
if (args.FirstOrDefault() == "fixture-child") { await Task.Delay(30000); return 0; }
if (args.FirstOrDefault() == "fixture-exit") { Console.WriteLine("{\"Stage\":\"OnAir\"}"); return 0; }
if (args.FirstOrDefault() == "fixture-stalled")
{ Console.WriteLine("{\"Stage\":\"Ready\"}"); await Task.Delay(30000); return 0; }
if (args.FirstOrDefault() == "fixture-parent")
{
    using var child = Process.Start(new ProcessStartInfo(exe, "fixture-child") { UseShellExecute = false, CreateNoWindow = true })!;
    Console.WriteLine(new string('x', 40000));
    Console.WriteLine(JsonSerializer.Serialize(new { Stage = "Running", ChildId = child.Id }));
    await Task.Delay(30000); return 0;
}

var results = new Dictionary<string, bool>();
void Check(string name, bool pass) { results.Add(name, pass); if (!pass) throw new InvalidOperationException("Check failed: " + name); }
void Reject(string name, Action action)
{ try { action(); } catch (Exception ex) when (ex is FormatException or ArgumentException or AirwaveUserException) { Check(name, true); return; } Check(name, false); }
try
{
    var originalRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
    var childStart = new ProcessStartInfo();
    childStart.Environment["DOTNET_ROOT"] = "embedded-core-runtime";
    childStart.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
    childStart.Environment["AIRWAVE_UNRELATED_CHECK"] = "preserve";
    ChildRuntimeEnvironment.Apply(childStart);
    Check("HelperResolvesInstalledFrameworks", !childStart.Environment.ContainsKey("DOTNET_ROOT") && !childStart.Environment.ContainsKey("DOTNET_MULTILEVEL_LOOKUP"));
    Check("ParentAndUnrelatedEnvironmentPreserved", originalRoot == Environment.GetEnvironmentVariable("DOTNET_ROOT") && childStart.Environment["AIRWAVE_UNRELATED_CHECK"] == "preserve");
    var publishKey = ConnectionPolicy.NewToken(); var listenKey = ConnectionPolicy.NewToken();
    var invite = ListenerInvite.Create("wss://radio.example.org", listenKey, publishKey);
    var parsed = ListenerInvite.Parse(invite);
    Check("InviteRoundTrip", parsed.RelayUrl == "wss://radio.example.org" && parsed.ListenKey == listenKey);
    var encoded = invite[8..].Replace('-', '+').Replace('_', '/');
    var contents = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
    Check("InviteDoesNotContainPublisherKey", !contents.Contains(publishKey));
    Reject("UnsavedBroadcastKeyCannotBecomeListenerInvite", () => ListenerInvite.Create("wss://radio.example.org", publishKey, publishKey));
    var savedBroadcastKey = Configuration.Unprotect(Configuration.Protect(publishKey));
    Reject("SavedBroadcastKeyCannotBecomeListenerInvite", () => ListenerInvite.Create("wss://radio.example.org", publishKey, ConnectionPolicy.NewToken(), savedBroadcastKey));
    Reject("ClearedBroadcastFieldCannotExportSavedBroadcastKey", () => ListenerInvite.Create("wss://radio.example.org", publishKey, "", savedBroadcastKey));
    Reject("WhitespaceCannotBypassInviteKeySeparation", () => ListenerInvite.Create("wss://radio.example.org", " " + publishKey + " ", "\t" + publishKey + "\n"));
    Reject("WhitespaceCannotBypassSavedInviteKeySeparation", () => ListenerInvite.Create("wss://radio.example.org", " " + publishKey + " ", ConnectionPolicy.NewToken(), savedBroadcastKey));
    Reject("WhitespaceCannotBypassSavedConnectionKeySeparation", () => ListenerInvite.ValidateKeySeparation(" " + publishKey, publishKey + " "));
    ListenerInvite.ValidateKeySeparation("", "");
    Check("EmptyConnectionKeysRemainSaveable", true);
    var normalizedInvite = ListenerInvite.Parse(ListenerInvite.Create(" wss://radio.example.org ", " " + listenKey + " ", publishKey, savedBroadcastKey));
    Check("DistinctListenerInviteNormalizesWhitespace", normalizedInvite.RelayUrl == "wss://radio.example.org" && normalizedInvite.ListenKey == listenKey);
    try { ListenerInvite.Create("wss://radio.example.org", publishKey, publishKey); }
    catch (ArgumentException error) { Check("InviteKeyErrorDoesNotEchoCredentials", !error.Message.Contains(publishKey)); }
    Reject("InvalidInviteEncoding", () => ListenerInvite.Parse("airwave:not-a-valid-key"));
    Reject("InviteSizeLimit", () => ListenerInvite.Parse("airwave:" + new string('A', 4096)));
    Reject("UnencryptedRemoteInviteRejected", () => ListenerInvite.Create("ws://radio.example.org", listenKey));
    Reject("CredentialsInInviteUrlRejected", () => ListenerInvite.Create("wss://user:secret@example.org", listenKey));
    Reject("ShortInviteKeyRejected", () => ListenerInvite.Create("wss://radio.example.org", "bad"));
    var protectedKey = Configuration.Protect(publishKey);
    Check("AccessKeyEncryptedAtRest", protectedKey != publishKey && !protectedKey.Contains(publishKey));
    Check("AccessKeyRoundTrip", Configuration.Unprotect(protectedKey) == publishKey);
    Check("EmptyAccessKey", Configuration.Protect("") == "" && Configuration.Unprotect("") == "");
    var join = ConnectionDraft.FromInvite("  " + invite + "  ", publishKey, savedBroadcastKey);
    Check("JoinInvitePreparesListenerOnlyConnection", join.RelayUrl == parsed.RelayUrl && join.ListenKey == listenKey && join.PublishKey == "");
    Reject("JoinCannotImportCurrentBroadcastKeyAsListener", () => ConnectionDraft.FromInvite(ListenerInvite.Create("wss://radio.example.org", publishKey), publishKey));
    Reject("JoinCannotImportSavedBroadcastKeyAsListener", () => ConnectionDraft.FromInvite(ListenerInvite.Create("wss://radio.example.org", publishKey), "", savedBroadcastKey));
    Reject("SaveCannotLaunderSavedBroadcastKeyAfterClearingField", () => ConnectionDraft.Validate("wss://radio.example.org", publishKey, "", savedBroadcastKeys: [savedBroadcastKey]));
    Reject("JoinWithoutInviteOrConnectionHasActionableError", () => ConnectionDraft.Validate("", "", "", requireListener: true));
    Check("BroadcastDoesNotRequireListenerKeyUntilSharing", ConnectionDraft.Validate("wss://radio.example.org", "", publishKey, requirePublisher: true).PublishKey == publishKey);
    Check("ListenerDoesNotRequireBroadcastKey", ConnectionDraft.Validate("wss://radio.example.org", listenKey, "", requireListener: true).ListenKey == listenKey);
    var safeValidationError = false;
    try { ConnectionDraft.Validate("wss://user:" + publishKey + "@radio.example.org", listenKey, publishKey, requireListener: true); }
    catch (AirwaveUserException error) { safeValidationError = !error.Message.Contains(publishKey) && !error.Message.Contains(listenKey); }
    Check("ConnectionValidationNeverEchoesSuppliedCredentials", safeValidationError);
    var buffering = JsonSerializer.SerializeToElement(new { Stage = "Buffering", FirstAudioMilliseconds = (double?)null });
    var playing = JsonSerializer.SerializeToElement(new { Stage = "Listening", FirstAudioMilliseconds = 165.0 });
    Check("ConnectedButBufferingIsNotReportedPlaying", UiState.Session(false, true, "Buffering", buffering) == "Preparing playback…");
    var rebuffering = JsonSerializer.SerializeToElement(new { Stage = "Buffering", FirstAudioMilliseconds = 165.0, Underruns = 1 });
    Check("UnderrunRebufferingDoesNotReusePastPlaybackSuccess", UiState.Session(false, true, "Buffering", rebuffering) == "Buffering audio…");
    Check("ActualListenerStageIsReportedPlaying", UiState.Session(false, true, "Listening", playing) == "Playing live audio");
    Check("ExitedListenerCannotBeReportedPlaying", UiState.Session(false, false, "Listening", playing) == "Not listening");
    Check("ExitedPublisherCannotBeReportedOnAir", UiState.Session(true, false, "OnAir", null) == "Off air");
    Check("LocalListenerWaitsForPublisherAcceptance", !UiState.PublisherReadyForLocalListener(true, "Connecting") && !UiState.PublisherReadyForLocalListener(true, "Capturing") && !UiState.PublisherReadyForLocalListener(false, "OnAir") && UiState.PublisherReadyForLocalListener(true, "OnAir"));
    Check("MissingCaptureSupportLeavesListeningAvailable", UiState.CaptureReadiness(false, true, true)?.Contains("listening is still available") == true);
    Check("SupportedCaptureNeedsCompletePayload", UiState.CaptureReadiness(true, true, false) is not null && UiState.CaptureReadiness(true, true, true) is null);
    Check("UnknownNativeDropTelemetryIsNotReportedLoss", !UiState.AudioLoss(JsonSerializer.SerializeToElement(new { NativeCaptureDroppedFrames = (long?)null, DroppedFrames = 0, Underruns = 0 })));
    Check("NativeCaptureLossWarnsEvenWithoutPacketLoss", UiState.AudioLoss(JsonSerializer.SerializeToElement(new { NativeCaptureDroppedFrames = 480, DroppedFrames = 0 })));
    Check("PlaybackInterruptionWarnsEvenWithoutPacketLoss", UiState.AudioLoss(JsonSerializer.SerializeToElement(new { Underruns = 1, DroppedFrames = 0 })));
    var privateError = JsonSerializer.SerializeToElement(new { Stage = "Error", Error = "Unexpected endpoint " + publishKey });
    Check("UnrecognizedHelperErrorCannotExposeCredentials", UiState.Problem(privateError, false) is { } safeProblem && !safeProblem.Contains(publishKey));
    foreach (var failure in Enum.GetValues<RelayConnectionFailure>())
    {
        var expected = RelayConnectionException.MessageFor(failure);
        Check("KnownRelayErrorRemainsActionable_" + failure, UiState.Problem(JsonSerializer.SerializeToElement(new { Error = expected }), false) == expected);
    }
    var childId = 0; int parentId;
    using (var owner = new OwnedProcess(exe, ["fixture-parent"]))
    {
        parentId = owner.Id;
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 5000 && owner.Status is null) await Task.Delay(20);
        Check("OversizedStatusDiscardedAndValidStatusParsed", owner.Status is { } state && state.TryGetProperty("ChildId", out _));
        childId = owner.Status!.Value.GetProperty("ChildId").GetInt32();
        Check("OwnedChildRunning", IsRunning(childId));
    }
    var stopped = Stopwatch.StartNew();
    while (stopped.ElapsedMilliseconds < 2000 && (IsRunning(parentId) || IsRunning(childId))) await Task.Delay(20);
    Check("StopKillsHostAndDescendant", !IsRunning(parentId) && !IsRunning(childId));
    var stalled = new OwnedProcess(exe, ["fixture-stalled"]);
    var stalledId = stalled.Id;
    var sendTime = Stopwatch.StartNew();
    for (int i = 0; i < 3000; ++i) stalled.Send(new { Volume = new string('x', 2048) });
    Check("StalledControlsDoNotBlockCaller", sendTime.ElapsedMilliseconds < 1000);
    var disposeTime = Stopwatch.StartNew(); stalled.Dispose();
    Check("StalledControlStopDoesNotBlockCaller", disposeTime.ElapsedMilliseconds < 250);
    await Task.Delay(200);
    Check("StalledControlHostStopped", !IsRunning(stalledId));
    using (var exited = new OwnedProcess(exe, ["fixture-exit"]))
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 3000 && (exited.Running || exited.Status is null)) await Task.Delay(20);
        Check("ExitedHostCannotRemainOnAir", !exited.Running && exited.Stage == "Stopped");
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = results.Count, Results = results }));
    return 0;
}
catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { Passed = results.Count(r => r.Value), Results = results, Error = ex.Message })); return 1; }

static bool IsRunning(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
