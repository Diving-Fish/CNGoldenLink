using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CNGoldenLink;

internal sealed class RemoteUploader
{
    private readonly Uri origin;
    private readonly int interval;
    private readonly CancellationTokenSource stop = new();
    private readonly HttpClient http;
    private SyncSnapshot? latest;
    private string status = "connecting";
    private string? token, connectionId, activeScope;
    private long sequence;
    private JsonElement? cursor, acknowledgedState;
    private readonly object pendingGate = new();
    private readonly Dictionary<string, SyncSnapshot> pending = new();
    private string? lastArea;
    private long droppedScopes;
    private readonly Func<Uri, string?> loadCredential;
    private readonly Action<Uri, string> saveCredential;
    private readonly Action<string> openBrowser;
    public string Status => Volatile.Read(ref status);
    public Task Completion { get; }
    public RemoteUploader(string baseUrl, int intervalSeconds, HttpMessageHandler? handler = null,
        Func<Uri, string?>? loadCredential = null, Action<Uri, string>? saveCredential = null, Action<string>? openBrowser = null) {
        origin = ValidateOrigin(baseUrl); interval = Math.Clamp(intervalSeconds, 1, 30);
        this.loadCredential = loadCredential ?? CredentialStore.Load;
        this.saveCredential = saveCredential ?? CredentialStore.Save;
        this.openBrowser = openBrowser ?? (url => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); });
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(10) };
        Completion = Task.Run(Run);
    }
    public static Uri ValidateOrigin(string value) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("invalid_server_url");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    public void Publish(SyncSnapshot snapshot) {
        Volatile.Write(ref latest, snapshot);
        if (snapshot.Area == null && snapshot.Cct == null) return;
        var key = snapshot.Area is { } area ? area.DatasetId + "|" + area.Sid + "|" + area.Side : "";
        key += "|" + snapshot.Cct?.SegmentIndex;
        lock (pendingGate) {
            if (!pending.ContainsKey(key) && pending.Count >= 64) {
                pending.Remove(pending.Keys.First()); Interlocked.Increment(ref droppedScopes);
            }
            pending[key] = snapshot;
        }
    }
    public void Stop() => stop.Cancel();
    public bool QueueSavedArea(AreaStatistics area) {
        var key = area.DatasetId + "|" + area.Sid + "|" + area.Side + "|saved";
        lock (pendingGate) {
            if (pending.Count >= 60) return false;
            pending[key] = new(new(null, null, null, null, null, null, false, null), null, area, 0);
            return true;
        }
    }
    public static Task Forget(string baseUrl) => Task.Run(() => CredentialStore.Forget(ValidateOrigin(baseUrl)));
    private void SetStatus(string value) => Volatile.Write(ref status, value);
    private async Task Run() {
        try {
            token = loadCredential(origin);
            if (token == null) {
                SetStatus("awaiting_browser");
                token = await Authorize(stop.Token);
                saveCredential(origin, token);
            }
            int failures = 0;
            while (!stop.IsCancellationRequested) {
                try {
                    if (connectionId == null) {
                        await Send("api/tracker/config", null, stop.Token);
                        var opened = await Send("api/tracker/presence", new { action = "start" }, stop.Token);
                        connectionId = opened.GetProperty("connectionId").GetString(); sequence = 0;
                    }
                    var snapshot = Volatile.Read(ref latest);
                    // Never replay a stale scene as live presence after a stall or reconnect.
                    if (snapshot != null && Environment.TickCount64 - snapshot.CapturedAt <= Math.Max(10000, interval * 2000)) {
                        await Send("api/tracker/presence", new { action = "snapshot", connectionId,
                            sequence = ++sequence, observation = snapshot.Live }, stop.Token);
                        string? dataError = snapshot.SamplingError;
                        KeyValuePair<string, SyncSnapshot>[] batch;
                        lock (pendingGate) batch = pending.Take(4).ToArray();
                        foreach (var item in batch) {
                            try {
                                if (item.Value.Area is { } a) {
                                    var body = new { a.DatasetId, a.Sid, a.Side, a.NoGoldenBestDeaths, a.TotalDeaths, a.Source, a.PracticeDetection };
                                    string encoded = SyncJson.Serialize(body);
                                    if (encoded != lastArea) { await Send("api/tracker/area-stats", body, stop.Token); lastArea = encoded; }
                                }
                                if (item.Value.Cct != null) await SyncCct(item.Value.Cct, stop.Token);
                                lock (pendingGate) {
                                    if (pending.TryGetValue(item.Key, out var current) && ReferenceEquals(current, item.Value)) pending.Remove(item.Key);
                                }
                            } catch (RemoteError ex) when (ex.Status is not (401 or 403 or 429) && ex.Status < 500) {
                                cursor = acknowledgedState = null; dataError = ex.Code;
                            }
                        }
                        if (Interlocked.Read(ref droppedScopes) > 0) dataError ??= "pending_scope_limit";
                        SetStatus(dataError == null ? "connected" : "connected_data_error:" + dataError);
                    } else SetStatus("connected_waiting_snapshot");
                    failures = 0;
                    await Task.Delay(TimeSpan.FromSeconds(interval), stop.Token);
                } catch (RemoteError ex) when (ex.Status is 401 or 403) {
                    SetStatus("authorization_required"); return;
                } catch (RemoteError ex) when (ex.Status == 409 && ex.Code == "presence_conflict") {
                    SetStatus("connection_replaced"); return;
                } catch (Exception ex) when (!stop.IsCancellationRequested &&
                    (ex is HttpRequestException or OperationCanceledException || ex is RemoteError { Status: >= 500 or 429 })) {
                    cursor = acknowledgedState = null; SetStatus("reconnecting");
                    int delay = Math.Min(60, 1 << Math.Min(++failures, 6));
                    if (ex is RemoteError remote) delay = Math.Max(delay, remote.RetryAfterSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(delay + Random.Shared.NextDouble()), stop.Token);
                }
            }
        } catch (OperationCanceledException) { SetStatus(stop.IsCancellationRequested ? "off" : "authorization_timeout"); }
        catch (Exception ex) { SetStatus(ex is RemoteError remote ? "server_error:" + remote.Code : "connection_error:" + ex.GetType().Name); }
        finally {
            if (connectionId != null && token != null) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await Send("api/tracker/presence", new { action = "stop", connectionId, sequence = ++sequence }, timeout.Token); }
                catch { /* TTL handles unreachable servers without blocking the game. */ }
            }
            http.Dispose();
        }
    }
    private async Task SyncCct(CctCapture capture, CancellationToken cancellation) {
        var scope = SyncJson.Scope(capture); var key = SyncJson.Serialize(scope);
        if (activeScope != key) { activeScope = key; cursor = acknowledgedState = null; }
        var state = SyncJson.Element(capture.State); var hash = SyncJson.Hash(state);
        if (Encoding.UTF8.GetByteCount(state.GetRawText()) > 8 * 1024 * 1024) throw new RemoteError(413, "scope_too_large");
        if (cursor == null) {
            var read = await Send("api/tracker/cct/state", new { scope }, cancellation);
            var current = read.GetProperty("current");
            if (current.ValueKind != JsonValueKind.Null) { cursor = Cursor(current); acknowledgedState = current.GetProperty("state").Clone(); }
        }
        if (cursor?.GetProperty("stateHash").GetString() == hash) return;
        JsonElement result;
        if (cursor == null || acknowledgedState == null) {
            result = await Send("api/tracker/cct/baseline", new { scope, mutationId = Guid.NewGuid().ToString(),
                streamEpoch = Guid.NewGuid().ToString(), revision = 0, expected = cursor, state }, cancellation);
        } else {
            var oldRooms = acknowledgedState.Value.GetProperty("rooms").EnumerateArray()
                .ToDictionary(r => r.GetProperty("roomKey").GetString()!, r => r, StringComparer.Ordinal);
            var rooms = state.GetProperty("rooms").EnumerateArray().ToArray();
            var roomKeys = rooms.Select(r => r.GetProperty("roomKey").GetString()!).ToHashSet(StringComparer.Ordinal);
            var patch = new {
                replaceRooms = rooms.Where(r => !oldRooms.TryGetValue(r.GetProperty("roomKey").GetString()!, out var old)
                    || SyncJson.Canonical(old) != SyncJson.Canonical(r)).ToArray(),
                removeRooms = oldRooms.Keys.Where(k => !roomKeys.Contains(k)).ToArray(), metadata = state.GetProperty("metadata")
            };
            result = await Send("api/tracker/cct/change", new { scope, mutationId = Guid.NewGuid().ToString(),
                expected = cursor, revision = cursor.Value.GetProperty("revision").GetInt64() + 1, patch, afterStateHash = hash }, cancellation);
        }
        cursor = Cursor(result); acknowledgedState = state;
    }
    private static JsonElement Cursor(JsonElement current) => SyncJson.Element(new {
        streamEpoch = current.GetProperty("streamEpoch").GetString(), revision = current.GetProperty("revision").GetInt64(),
        stateHash = current.GetProperty("stateHash").GetString()
    });
    private async Task<JsonElement> Send(string path, object? body, CancellationToken cancellation, bool authenticated = true) {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, path);
        if (body != null) request.Content = new StringContent(SyncJson.Serialize(body), Encoding.UTF8, "application/json");
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        using var stream = await response.Content.ReadAsStreamAsync(bounded.Token);
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, bounded.Token)) > 0) {
            if (bytes.Length + count > 10 * 1024 * 1024) throw new RemoteError(413, "response_too_large");
            bytes.Write(buffer, 0, count);
        }
        JsonElement json;
        try { using var doc = JsonDocument.Parse(bytes.ToArray()); json = doc.RootElement.Clone(); }
        catch (JsonException) { throw new RemoteError((int)response.StatusCode, "invalid_server_response"); }
        if (!response.IsSuccessStatusCode || !json.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) {
            var code = json.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (code == null || code.Length > 80 || code.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_')) code = "http_" + (int)response.StatusCode;
            int retry = (int)Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ??
                (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 0, 0, 300);
            throw new RemoteError((int)response.StatusCode, code, retry);
        }
        return json;
    }
    private async Task<string> Authorize(CancellationToken cancellation) {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var listener = new LoopbackAuthorization(); string redirect = listener.RedirectUri;
        var url = new Uri(origin, "tracker/activate").AbsoluteUri +
            $"?redirect_uri={Uri.EscapeDataString(redirect)}&code_challenge={challenge}&code_challenge_method=S256&state={state}&client_name=CNGoldenLink&device_name=Celeste";
        openBrowser(url);
        var code = await listener.Receive(state, timeout.Token);
        var result = await Send("api/tracker/devices/token", new { code, codeVerifier = verifier }, timeout.Token, false);
        var accessToken = result.GetProperty("accessToken").GetString();
        if (string.IsNullOrEmpty(accessToken) || accessToken.Length > 1024) throw new FormatException("invalid_device_token");
        return accessToken;
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed class RemoteError(int status, string code, int retryAfterSeconds = 0) : Exception(code) {
        public int Status { get; } = status;
        public string Code { get; } = code;
        public int RetryAfterSeconds { get; } = retryAfterSeconds;
    }
}
