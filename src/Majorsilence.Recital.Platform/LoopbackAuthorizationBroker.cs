using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Majorsilence.Recital.Storage.OAuth;

namespace Majorsilence.Recital.Platform
{
    /// <summary>
    /// Desktop OAuth sign-in (plan 2.5): the consent page opens in the system browser and the server redirects
    /// to <c>http://127.0.0.1:&lt;port&gt;/</c>, where a one-shot listener on this machine reads the code. A raw
    /// socket rather than HttpListener, which on Windows needs a URL reservation (admin) to listen.
    /// </summary>
    public sealed class LoopbackAuthorizationBroker : IAuthorizationBroker, IDisposable
    {
        private const string Done = """
            <!doctype html><html><head><meta charset="utf-8"><title>Recital</title></head>
            <body style="font-family:sans-serif;text-align:center;margin-top:20vh">
            <h1>Signed in</h1><p>You can close this tab and go back to Recital.</p></body></html>
            """;

        private readonly Action<Uri> open_browser;
        private readonly string host;
        private readonly TimeSpan timeout;
        private readonly int port;
        private TcpListener? listener;

        /// <param name="openBrowser">Opens a URL; the default asks the OS (the default browser).</param>
        /// <param name="host">The redirect's host as registered with the provider: <c>127.0.0.1</c> (Google) or <c>localhost</c>.</param>
        /// <param name="port">A fixed port for a provider that wants the exact redirect registered; 0 picks a free one.</param>
        public LoopbackAuthorizationBroker (Action<Uri>? openBrowser = null, string host = "127.0.0.1", int port = 0, TimeSpan? timeout = null)
        {
            open_browser = openBrowser ?? OpenWithShell;
            this.host = host;
            this.port = port;
            this.timeout = timeout ?? TimeSpan.FromMinutes (5);
        }

        public Task<Uri> StartAsync (CancellationToken ct)
        {
            listener?.Stop ();
            listener = new TcpListener (IPAddress.Loopback, port);
            listener.Start ();
            var bound = ((IPEndPoint)listener.LocalEndpoint).Port;
            return Task.FromResult (new Uri ($"http://{host}:{bound}/"));
        }

        public async Task<Uri> AuthorizeAsync (Uri authorizeUri, CancellationToken ct)
        {
            var server = listener ?? throw new InvalidOperationException ("StartAsync first.");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource (ct);
            timeoutSource.CancelAfter (timeout);
            open_browser (authorizeUri);
            try {
                // Browsers also ask for /favicon.ico; the answer is the request carrying code or error.
                while (true) {
                    using var client = await server.AcceptTcpClientAsync (timeoutSource.Token).ConfigureAwait (false);
                    var stream = client.GetStream ();
                    var target = await ReadRequestTargetAsync (stream, timeoutSource.Token).ConfigureAwait (false);
                    var answer = target is not null && (target.Contains ("code=", StringComparison.Ordinal) || target.Contains ("error=", StringComparison.Ordinal));
                    var body = Encoding.UTF8.GetBytes (answer ? Done : "");
                    var head = Encoding.ASCII.GetBytes ($"HTTP/1.1 {(answer ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync (head, timeoutSource.Token).ConfigureAwait (false);
                    await stream.WriteAsync (body, timeoutSource.Token).ConfigureAwait (false);
                    if (answer)
                        return new Uri (new Uri ($"http://{host}/"), target);
                }
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                throw new OAuthException ("Sign-in timed out: no answer from the browser.");
            } finally {
                server.Stop ();
                listener = null;
            }
        }

        public void Dispose () => listener?.Stop ();

        /// <summary>The target of the request line (<c>GET /?code=... HTTP/1.1</c>), or null.</summary>
        private static async Task<string?> ReadRequestTargetAsync (NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var total = 0;
            while (total < buffer.Length) {
                var read = await stream.ReadAsync (buffer.AsMemory (total), ct).ConfigureAwait (false);
                if (read == 0)
                    break;
                total += read;
                if (Encoding.ASCII.GetString (buffer, 0, total).Contains ("\r\n\r\n", StringComparison.Ordinal))
                    break;
            }
            var line = Encoding.ASCII.GetString (buffer, 0, total).Split ("\r\n")[0].Split (' ');
            return line.Length >= 2 && line[0] == "GET" ? line[1] : null;
        }

        private static void OpenWithShell (Uri uri)
        {
            // UseShellExecute hands the URL to the OS: the default browser on Windows and macOS, xdg-open on Linux.
            if (OperatingSystem.IsLinux ())
                Process.Start (new ProcessStartInfo ("xdg-open", uri.AbsoluteUri) { UseShellExecute = false })?.Dispose ();
            else
                Process.Start (new ProcessStartInfo (uri.AbsoluteUri) { UseShellExecute = true })?.Dispose ();
        }
    }
}
