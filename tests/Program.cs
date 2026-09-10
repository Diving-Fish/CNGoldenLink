using System.Text.Json;
using CNGoldenLink;

var root = Path.Combine(Path.GetTempPath(), "CNGoldenLinkTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    const string oldRoute = """{"chapterSID":"Example/Map","checkpoints":[{"name":"Start","abbreviation":"ST","rooms":[{"debugRoomName":"d-03","customRoomName":"Room A"}]}]}""";
    var midRoute = oldRoute.Replace("\"chapterSID\"", "\"ignoredRooms\":[],\"gameplayRoomCount\":999,\"chapterSID\"");
    var wrappedRoute = "{\"selectedIndex\":0,\"segments\":[{\"path\":" + midRoute + "}]}";
    var imported = new[] { oldRoute, midRoute, wrappedRoute }.Select(raw => CctRouteReader.Read(raw, (_, _) => new("stable-segment", new[] { "stable-cp" }))).ToArray();
    Check(imported.All(r => r.GameplayRoomCount == 1 && r.Route.GetRawText() == imported[0].Route.GetRawText()), "Three route generations normalize identically");
    Check(imported[0].Route.GetProperty("nodes")[0].GetProperty("customRoomName").GetString() == "Room A", "Room display name retained");
    Check(imported[0].Route.GetProperty("checkpoints")[0].GetProperty("abbreviation").GetString() == "ST", "Checkpoint abbreviation retained");
    bool badIndex = false;
    try { CctRouteReader.Read(wrappedRoute.Replace("\"selectedIndex\":0", "\"selectedIndex\":9"), (_, _) => new("x", new[] { "cp" })); }
    catch (FormatException) { badIndex = true; }
    Check(badIndex, "Invalid selected segment rejected");
    var attempt = new NoGoldenAttempt();
    var identity = new object();
    var savedBest = new Dictionary<string, int> { ["Test/Map|Normal"] = 8 };
    var savedTotal = new Dictionary<string, int> { ["Test/Map|Normal"] = 100 };
    var restored = SavedAreaStatistics.Read("dataset", savedBest, savedTotal).Single();
    var completionOnly = SavedAreaStatistics.Read("dataset", new Dictionary<string, int>(), new Dictionary<string, int>(),
        new Dictionary<string, bool> { ["Cleared/Map|Normal"] = true, ["New/Map|Normal"] = false }).ToArray();
    Check(completionOnly.Length == 2 && completionOnly[0].Completed == true && completionOnly[1].Completed == false
        && completionOnly.All(a => a.NoGoldenBestDeaths == null), "Completion-only saves replay true and false without fabricating PB");
    Check(restored.Sid == "Test/Map" && restored.NoGoldenBestDeaths == 8 && restored.TotalDeaths == 100,
        "Offline records can be queued without revisiting their map");
    attempt.Start(identity, true, false, 0, false);
    var outgoing = new object();
    attempt.Observe(outgoing, 200, false);
    Check(attempt.Complete(outgoing, 200, true, false) is null, "Outgoing level cannot complete incoming attempt");
    attempt.Observe(identity, 0, false);
    Check(attempt.Complete(identity, 4, true, false) == 4, "Outgoing level final frame does not invalidate incoming attempt");
    attempt.Start(identity, true, false, 0, false);
    attempt.Observe(identity, 0, false);
    attempt.Observe(new object(), 0, false);
    Check(attempt.Complete(identity, 4, true, false) is null, "Session change after activation remains invalid");
    attempt.Start(identity, true, false, 0, false);
    Check(attempt.Complete(identity, 4, true, false) == 4, "Full observed no-golden clear");
    Check(attempt.Complete(identity, 4, true, false) is null, "Completion deduplicated");
    attempt.Start(identity, true, false, 0, false);
    attempt.Observe(identity, 2, true);
    Check(attempt.Complete(identity, 3, true, false) is null, "Earlier golden excludes clear");
    attempt.Start(identity, true, true, 0, false);
    Check(attempt.Complete(identity, 0, true, false) is null, "Resumed session excluded");
    attempt.Start(identity, false, false, 0, false);
    Check(attempt.Complete(identity, 0, false, false) is null, "Checkpoint start excluded");
    attempt.Start(identity, true, false, 0, false);
    attempt.Observe(identity, 5, false);
    Check(attempt.Complete(identity, 1, true, false) is null, "Death rollback excluded");
    attempt.Start(identity, true, false, 0, false);
    Check(attempt.Complete(new object(), 0, true, false) is null, "Unknown session excluded");
    var observation = new Observation("Example/Map", "Normal", "room-a", "Level",
        false, false, false, true, false, true, true);
    DiagnosticEntry Entry(int sequence) => new(sequence, DateTimeOffset.UtcNow, sequence,
        "snapshot", observation, 0);

    var normal = new DiagnosticWriter(Path.Combine(root, "normal"));
    for (var i = 1; i <= 10; i++) normal.Enqueue(Entry(i));
    normal.Stop();
    await normal.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    var lines = File.ReadAllLines(Directory.GetFiles(Path.Combine(root, "normal"))[0]);
    Check(lines.Length == 10, "Stop drains queued entries");
    for (var i = 0; i < lines.Length; i++)
    {
        using var json = JsonDocument.Parse(lines[i]);
        Check(json.RootElement.GetProperty("sequence").GetInt32() == i + 1, "FIFO ordering");
        Check(json.RootElement.GetProperty("attemptId").ValueKind == JsonValueKind.Null, "Unknown attempt remains null");
        Check(!json.RootElement.GetProperty("capabilities").GetProperty("attemptEvents").GetBoolean(), "No reliable attempt claim");
    }

    var limited = new DiagnosticWriter(Path.Combine(root, "limited"), maxBytes: 700);
    for (var i = 1; i <= 10000; i++) limited.Enqueue(Entry(i));
    limited.Stop();
    await limited.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    Check(limited.Status == "size_limit_reached", "File limit is visible");
    Check(new FileInfo(Directory.GetFiles(Path.Combine(root, "limited"))[0]).Length <= 700, "File stays bounded");
    Check(limited.Dropped > 0, "Overflow is reported");

    var badPath = Path.Combine(root, "file-instead-of-directory");
    File.WriteAllText(badPath, "test");
    var failing = new DiagnosticWriter(badPath);
    await failing.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    Check(failing.Status.StartsWith("storage_error:"), "Storage failure contained");
    Check(!failing.Status.Contains(root), "Status omits local path");
    failing.Enqueue(Entry(1));
    Check(failing.Dropped == 1, "Failed worker rejects new entries");
    failing.Stop();

    var retention = Path.Combine(root, "retention");
    Directory.CreateDirectory(retention);
    for (var i = 0; i < 8; i++) File.WriteAllText(Path.Combine(retention, $"diagnostic-old-{i}.jsonl"), "");
    File.WriteAllText(Path.Combine(retention, "unrelated.txt"), "keep");
    var retaining = new DiagnosticWriter(retention);
    retaining.Enqueue(Entry(1)); retaining.Stop();
    await retaining.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    Check(Directory.GetFiles(retention, "diagnostic-*.jsonl").Length == 4, "Retains at most four logs");
    Check(File.Exists(Path.Combine(retention, "unrelated.txt")), "Retention preserves unrelated files");
    var exitWriter = new DiagnosticWriter(Path.Combine(root, "exit"));
    exitWriter.Enqueue(Entry(1));
    exitWriter.Enqueue(new DiagnosticEntry(2, DateTimeOffset.UtcNow, 2,
        "diagnostics_stopped", null, 0, "game_exiting"));
    exitWriter.Stop();
    exitWriter.Stop();
    await exitWriter.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    exitWriter.Stop();
    var exitLines = File.ReadAllLines(Directory.GetFiles(Path.Combine(root, "exit"))[0]);
    Check(exitLines.Length == 2, "Repeated stop preserves a single terminal entry");
    using var terminal = JsonDocument.Parse(exitLines[^1]);
    Check(terminal.RootElement.GetProperty("detail").GetString() == "game_exiting", "Exit reason drained");
    exitWriter.Enqueue(Entry(3));
    Check(exitWriter.Dropped == 1, "Stopped writer rejects late snapshots");
    Console.WriteLine("PASS: drain/order, unknown capabilities, queue/file bounds, storage failure, retention.");
}
finally
{
    // Only delete the unique directory created by this test run.
    Directory.Delete(root, recursive: true);
}

static void Check(bool result, string label)
{
    if (!result) throw new Exception("FAIL: " + label);
}
