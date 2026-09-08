using Celeste;
using Celeste.Mod;
using Microsoft.Xna.Framework;
using Monocle;

namespace CNGoldenLink;

public sealed class GoldenLinkSettings : EverestModuleSettings
{
    public bool ConnectionEnabled { get; set; }
    public string ServiceBaseUrl { get; set; } = "https://gist.diving-fish.com";
    public int UploadIntervalSeconds { get; set; } = 5;
    public bool DiagnosticsEnabled { get; set; }
    public bool OverlayEnabled { get; set; }
    public int OverlayPort { get; set; } = 32272;
}

public sealed class GoldenLinkSaveData : EverestModuleSaveData
{
    public string DatasetId { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, int> NoGoldenBestDeaths { get; set; } = new();
    public Dictionary<string, int> LastTotalDeaths { get; set; } = new();
}

public sealed class GoldenLinkModule : EverestModule
{
    public override Type SettingsType => typeof(GoldenLinkSettings);
    public override Type SaveDataType => typeof(GoldenLinkSaveData);
    private GoldenLinkSettings Settings => (GoldenLinkSettings)_Settings;
    private readonly NoGoldenAttempt noGolden = new();
    private RemoteUploader? uploader;
    private OverlayServer? overlay;
    private long nextOverlaySample;
    private string? overlayError;
    private DiagnosticWriter? writer;
    private TextMenu.SubHeader? statusLine;
    private Task? forgetting;
    private bool loaded, exiting, attempted;
    private string? currentUrl, fault;
    private int currentInterval;
    private long nextSample, diagnosticSequence;
    private GoldenLinkSaveData? queuedSave;
    private Queue<AreaStatistics> savedAreas = new();

    public override void Load() {
        if (loaded) return;
        exiting = false; loaded = true; attempted = false; nextSample = 0;
        On.Monocle.Engine.Update += Update;
        Everest.Events.Level.OnEnter += Enter;
        Everest.Events.Level.OnComplete += Complete;
        On.Celeste.Strawberry.OnPlayer += StrawberryPlayer;
        Everest.Events.Celeste.OnExiting += Exiting;
    }
    public override void Unload() {
        if (!loaded) return;
        On.Monocle.Engine.Update -= Update;
        Everest.Events.Level.OnEnter -= Enter;
        Everest.Events.Level.OnComplete -= Complete;
        On.Celeste.Strawberry.OnPlayer -= StrawberryPlayer;
        Everest.Events.Celeste.OnExiting -= Exiting;
        loaded = false; Exiting(); statusLine = null;
    }
    public override void CreateModMenuSection(TextMenu menu, bool inGame, FMOD.Studio.EventInstance snapshot) {
        menu.Add(new TextMenu.SubHeader("CN Golden Link"));
        menu.Add(new TextMenu.OnOff("OBS Overlay", Settings.OverlayEnabled).Change(value => {
            Settings.OverlayEnabled = value; overlayError = null;
        }));
        menu.Add(new TextMenu.Button("Open Overlay / 打开控制页").Pressed(() => {
            if (overlay != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://localhost:{overlay.Port}/apex") { UseShellExecute = true });
        }));
        menu.Add(new TextMenu.OnOff(Dialog.Clean("CNGOLDENLINK_CONNECT"), Settings.ConnectionEnabled).Change(value => {
            Settings.ConnectionEnabled = value; attempted = false; fault = null;
            if (!value) uploader?.Stop(); nextSample = 0;
        }));
        menu.Add(new TextMenu.Slider(Dialog.Clean("CNGOLDENLINK_INTERVAL"), n => n + "s", 1, 30,
            Math.Clamp(Settings.UploadIntervalSeconds, 1, 30)).Change(value => Settings.UploadIntervalSeconds = value));
        menu.Add(new TextMenu.Button(Dialog.Clean("CNGOLDENLINK_REAUTHORIZE")).Pressed(() => {
            Settings.ConnectionEnabled = false; attempted = false; uploader?.Stop();
            var previous = uploader; var url = Settings.ServiceBaseUrl;
            forgetting = Task.Run(async () => {
                if (previous != null) await previous.Completion;
                await RemoteUploader.Forget(url);
            });
        }));
        menu.Add(new TextMenu.OnOff(Dialog.Clean("CNGOLDENLINK_DIAGNOSTICS"), Settings.DiagnosticsEnabled)
            .Change(value => Settings.DiagnosticsEnabled = value));
        statusLine = new TextMenu.SubHeader(StatusText()); menu.Add(statusLine);
        menu.Add(new TextMenu.SubHeader(Dialog.Clean("CNGOLDENLINK_SERVER_HINT")));
    }
    private string StatusText() {
        string state = fault ?? (!Settings.ConnectionEnabled ? "off" : uploader?.Status ?? "connecting");
        string key = "CNGOLDENLINK_STATUS_" + state.ToUpperInvariant();
        return Dialog.Has(key) ? Dialog.Clean(key) : Dialog.Clean("CNGOLDENLINK_STATUS_ERROR") + " " + state;
    }
    private void Update(On.Monocle.Engine.orig_Update orig, Engine self, GameTime time) {
        orig(self, time);
        if (exiting) return;
        try {
            if (overlay != null && (!Settings.OverlayEnabled || overlay.Port != Math.Clamp(Settings.OverlayPort, 1024, 65535))) {
                overlay.Dispose(); overlay = null;
            }
            if (Settings.OverlayEnabled && overlay == null && overlayError == null) {
                try { overlay = new OverlayServer(Settings.OverlayPort, Settings.ServiceBaseUrl, Path.Combine(Everest.PathGame, "CNGoldenLinkData")); }
                catch (Exception ex) { overlayError = ex.GetType().Name; Logger.Log(LogLevel.Warn, "CNGoldenLink", "Overlay: " + overlayError); }
            }
            if (forgetting is { IsCompleted: true }) {
                fault = forgetting.IsFaulted ? "credential_delete_failed" : null; forgetting = null;
            }
            if (currentUrl != Settings.ServiceBaseUrl || currentInterval != Settings.UploadIntervalSeconds) {
                uploader?.Stop(); attempted = false;
                currentUrl = Settings.ServiceBaseUrl; currentInterval = Settings.UploadIntervalSeconds;
            }
            if (!Settings.ConnectionEnabled) { uploader?.Stop(); attempted = false; }
            else if (!attempted && forgetting == null && (uploader == null || uploader.Completion.IsCompleted)) {
                attempted = true; fault = null;
                uploader = new RemoteUploader(Settings.ServiceBaseUrl, Settings.UploadIntervalSeconds); nextSample = 0;
                queuedSave = null;
            }
            if (Settings.DiagnosticsEnabled && writer == null)
                writer = new DiagnosticWriter(Path.Combine(Everest.PathGame, "CNGoldenLinkData", "logs"));
            if (!Settings.DiagnosticsEnabled && writer != null) { writer.Stop(); writer = null; }
            var level = Engine.Scene as Level;
            if (level != null) noGolden.Observe(level.Session, level.Session.Deaths,
                level.Session.GrabbedGolden || HoldingGolden(level) == true);
            bool uploadDue = Environment.TickCount64 >= nextSample;
            if (uploadDue || (overlay != null && Environment.TickCount64 >= nextOverlaySample)) {
                Sample(level, uploadDue);
                nextOverlaySample = Environment.TickCount64 + 500;
                if (uploadDue) nextSample = Environment.TickCount64 + Math.Clamp(Settings.UploadIntervalSeconds, 1, 30) * 1000;
            }
            if (Settings.ConnectionEnabled && uploader != null && _SaveData is GoldenLinkSaveData save) {
                if (!ReferenceEquals(queuedSave, save)) {
                    queuedSave = save;
                    savedAreas = new Queue<AreaStatistics>(SavedAreaStatistics.Read(save.DatasetId, save.NoGoldenBestDeaths, save.LastTotalDeaths));
                }
                if (savedAreas.TryPeek(out var saved) && uploader.QueueSavedArea(saved)) savedAreas.Dequeue();
            }
        } catch (Exception ex) { fault = "sampling_error:" + ex.GetType().Name; }
        if (statusLine != null) statusLine.Title = StatusText();
    }
    private static bool? HoldingGolden(Level level) {
        var player = level.Tracker.GetEntity<Player>();
        return player == null ? null : !player.Dead && player.Leader.Followers.Any(f => f.Entity is Strawberry { Golden: true });
    }
    private void Sample(Level? level, bool publishRemote = true) {
        var sid = level?.Session.Area.SID; var side = level?.Session.Area.Mode.ToString();
        var live = new LiveObservation(sid, side, level?.Session.Level, level?.Paused,
            level?.Transitioning, level == null ? null : HoldingGolden(level), CctAdapter.Available, CctAdapter.TrackingPaused);
        CctCapture? cct = null; AreaStatistics? area = null; string? error = null;
        if (level != null && _SaveData is GoldenLinkSaveData save) {
            var key = sid + "|" + side;
            var total = Celeste.SaveData.Instance?.GetAreaStatsFor(level.Session.Area)?.Modes[(int)level.Session.Area.Mode].Deaths;
            if (total != null) {
                // A restored/reset save must not try to lower the server's cumulative high-water mark.
                if (save.LastTotalDeaths.TryGetValue(key, out int last) && total < last) {
                    save.DatasetId = Guid.NewGuid().ToString(); save.NoGoldenBestDeaths.Clear(); save.LastTotalDeaths.Clear();
                    noGolden.Invalidate();
                }
                save.LastTotalDeaths[key] = total.Value;
            }
            area = new(save.DatasetId, sid!, side!, save.NoGoldenBestDeaths.TryGetValue(key, out int best) ? best : null, TotalDeaths: total);
            try { if (Settings.ConnectionEnabled || overlay != null) cct = CctAdapter.Capture(save.DatasetId, sid!, side!); }
            catch (Exception ex) { error = ex is InvalidOperationException ? ex.Message : "cct_sampling_error"; }
        }
        var captured = new SyncSnapshot(live, cct, area, Environment.TickCount64, error);
        overlay?.Publish(captured, Settings.ServiceBaseUrl);
        if (Settings.ConnectionEnabled && publishRemote) uploader?.Publish(captured);
        if (writer != null && publishRemote) {
            var player = level?.Tracker.GetEntity<Player>();
            var observation = new Observation(sid, side, live.Room, Engine.Scene?.GetType().Name ?? "none",
                live.Paused, live.Transitioning, level?.Completed, level == null ? null : player != null,
                player?.Dead, live.HoldingGolden, Engine.Instance.IsActive);
            writer.Enqueue(new(++diagnosticSequence, DateTimeOffset.UtcNow, Environment.TickCount64,
                "snapshot", observation, writer.Dropped, error, area));
        }
    }
    private void Enter(Session session, bool fromSaveData) {
        if (exiting) return;
        noGolden.Start(session, session.StartedFromBeginning, fromSaveData || session.RestartedFromGolden,
            session.Deaths, session.GrabbedGolden); nextSample = 0;
    }
    private void Complete(Level level) {
        if (exiting) return;
        try {
            var deaths = noGolden.Complete(level.Session, level.Session.Deaths, level.Session.StartedFromBeginning,
                level.Session.GrabbedGolden || HoldingGolden(level) == true);
            if (deaths != null && _SaveData is GoldenLinkSaveData save) {
                var key = level.Session.Area.SID + "|" + level.Session.Area.Mode;
                if (!save.NoGoldenBestDeaths.TryGetValue(key, out var old) || deaths < old) save.NoGoldenBestDeaths[key] = deaths.Value;
            }
            Sample(level);
        } catch (Exception ex) { fault = "completion_error:" + ex.GetType().Name; }
    }
    private void StrawberryPlayer(On.Celeste.Strawberry.orig_OnPlayer orig, Strawberry self, Player player) {
        orig(self, player);
        if (self.Golden && self.Follower.Leader?.Entity == player) noGolden.Invalidate();
    }
    private void Exiting() {
        exiting = true; noGolden.Invalidate(); uploader?.Stop(); writer?.Stop(); writer = null;
        overlay?.Dispose(); overlay = null;
    }
}
