using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CNGoldenLink;

/// <summary>A bounded loopback-only HTTP callback. TcpListener avoids Windows HTTP.sys URL ACL requirements.</summary>
internal sealed class LoopbackAuthorization : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public string RedirectUri { get; }
    public LoopbackAuthorization() {
        listener.Start(4);
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/cngoldenlink/";
    }
    public async Task<string> Receive(string expectedState, CancellationToken cancellation) {
        while (true) {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            try {
                using var stream = client.GetStream();
                var buffer = new byte[8192]; int length = 0;
                while (length < buffer.Length) {
                    int read = await stream.ReadAsync(buffer.AsMemory(length), limit.Token);
                    if (read == 0) break;
                    length += read;
                    if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                string request = Encoding.ASCII.GetString(buffer, 0, length);
                string? code = ParseRequest(request, expectedState);
                string body = code == null ? "Invalid authorization callback." : "Authorization received. You can return to Celeste.";
                string response = $"HTTP/1.1 {(code == null ? "400 Bad Request" : "200 OK")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), limit.Token);
                if (code != null) return code;
            } catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is IOException or OperationCanceledException or SocketException) {
                // An unrelated tab or disconnected caller does not consume the authorization transaction.
            }
        }
    }
    internal static string? ParseRequest(string request, string expectedState) {
        if (!request.Contains("\r\n\r\n", StringComparison.Ordinal)) return null;
        var line = request.Split("\r\n", 2)[0].Split(' ');
        if (line.Length != 3 || line[0] != "GET" || !line[1].StartsWith("/cngoldenlink/?", StringComparison.Ordinal)
            || line[2] is not ("HTTP/1.1" or "HTTP/1.0")) return null;
        try {
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in line[1].Split('?', 2)[1].Split('&')) {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2 || !query.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]))) return null;
            }
            return query.TryGetValue("state", out var state) && state == expectedState
                && query.TryGetValue("code", out var code) && code is { Length: > 0 and <= 256 }
                && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? code : null;
        } catch (UriFormatException) { return null; }
    }
    public void Dispose() => listener.Stop();
}
