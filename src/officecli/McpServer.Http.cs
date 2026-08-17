// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OfficeCli;

public static partial class McpServer
{
    public sealed class HttpOptions
    {
        public string Host { get; init; } = "127.0.0.1";
        public int Port { get; init; } = 8765;
        public string? Token { get; init; }
        public string WorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "officecli-mcp");
    }

    internal static bool LooksLikeHttp(string[] args) =>
        args.Length > 0 && (args[0] is "http" or "--http" || args.Contains("--http"));

    internal static bool TryParseHttpOptions(string[] args, out HttpOptions options, out string error)
    {
        options = null!;
        error = "";
        string host = "127.0.0.1";
        int port = 8765;
        string? token = Environment.GetEnvironmentVariable("OFFICECLI_MCP_TOKEN");
        string workDir = Path.Combine(Path.GetTempPath(), "officecli-mcp");

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "http" or "--http") continue;
            if ((a == "--port" || a == "-p") && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out port) || port is < 1 or > 65535)
                {
                    error = "Invalid --port (1-65535).";
                    return false;
                }
                continue;
            }
            if (a == "--host" && i + 1 < args.Length) { host = args[++i]; continue; }
            if (a == "--token" && i + 1 < args.Length) { token = args[++i]; continue; }
            if (a is "--workdir" or "--work-dir" && i + 1 < args.Length) { workDir = args[++i]; continue; }
            error = $"Unknown mcp http option: {a}";
            return false;
        }

        if (!IsLoopbackHost(host) && string.IsNullOrEmpty(token))
        {
            error = "Binding a non-loopback host requires --token or OFFICECLI_MCP_TOKEN.";
            return false;
        }

        options = new HttpOptions { Host = host, Port = port, Token = token, WorkDir = workDir };
        return true;
    }

    public static async Task RunHttpAsync(HttpOptions options)
    {
        ConfigureMcpProcess();
        Directory.CreateDirectory(options.WorkDir);

        var prefix = ListenerPrefix(options.Host, options.Port);
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode is 5 or 32)
        {
            Console.Error.WriteLine($"Failed to bind {prefix}: {ex.Message}");
            Console.Error.WriteLine("On Windows, non-localhost prefixes need URL ACL reservation or elevation.");
            throw;
        }

        using var upgradeCts = new CancellationTokenSource();
        var upgradeTask = RunPeriodicUpgradeCheckAsync(upgradeCts.Token);
        var sessions = new ConcurrentDictionary<string, HttpSession>(StringComparer.Ordinal);
        Console.Error.WriteLine($"officecli MCP HTTP listening on {PublicUrl(options)}");

        try
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                _ = Task.Run(() => HandleHttp(ctx, options, sessions));
            }
        }
        finally
        {
            upgradeCts.Cancel();
            try { await upgradeTask; } catch { }
            foreach (var s in sessions.Values)
                s.Dispose();
        }
    }

    private static async Task HandleHttp(HttpListenerContext ctx, HttpOptions options, ConcurrentDictionary<string, HttpSession> sessions)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!ValidateHostAndOrigin(req, options.Host))
            {
                await WriteHttpError(res, 403, "Forbidden: invalid Host or Origin");
                return;
            }
            if (!Authorize(req, options.Token))
            {
                await WriteHttpError(res, 401, "Unauthorized");
                return;
            }

            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path is "" or "/" or "/mcp") { /* mcp endpoint */ }
            else if (path == "/health" && req.HttpMethod == "GET")
            {
                res.StatusCode = 200;
                await WriteBytes(res, "ok"u8.ToArray(), "text/plain");
                return;
            }
            else
            {
                await WriteHttpError(res, 404, "Not Found");
                return;
            }

            if (req.HttpMethod == "OPTIONS")
            {
                res.AddHeader("Allow", "GET, POST, DELETE, OPTIONS");
                res.StatusCode = 204;
                res.Close();
                return;
            }

            var protoHeader = req.Headers["MCP-Protocol-Version"];
            if (!string.IsNullOrEmpty(protoHeader) && !SupportedProtocolVersions.Contains(protoHeader))
            {
                await WriteHttpError(res, 400, $"Unsupported MCP-Protocol-Version: {protoHeader}");
                return;
            }

            switch (req.HttpMethod)
            {
                case "GET":
                    res.AddHeader("Allow", "POST, DELETE, OPTIONS");
                    await WriteHttpError(res, 405, "Method Not Allowed");
                    return;
                case "DELETE":
                    HandleDelete(req, res, sessions);
                    return;
                case "POST":
                    await HandlePost(req, res, options, sessions);
                    return;
                default:
                    res.AddHeader("Allow", "GET, POST, DELETE, OPTIONS");
                    await WriteHttpError(res, 405, "Method Not Allowed");
                    return;
            }
        }
        catch (Exception ex)
        {
            try { await WriteHttpError(res, 500, ex.Message); } catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    private static async Task HandlePost(HttpListenerRequest req, HttpListenerResponse res, HttpOptions options, ConcurrentDictionary<string, HttpSession> sessions)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();

        var method = PeekMethod(body);
        var sessionHeader = req.Headers["MCP-Session-Id"] ?? req.Headers["Mcp-Session-Id"];

        if (method == "initialize")
        {
            var session = HttpSession.Create(options.WorkDir);
            sessions[session.Id] = session;
            SessionWorkDir.Value = session.WorkDir;
            try
            {
                var response = DispatchJsonRpc(body) ?? ErrorJson(null, -32603, "initialize produced no result");
                res.AddHeader("MCP-Session-Id", session.Id);
                res.AddHeader("MCP-Protocol-Version", "2025-11-25");
                res.StatusCode = 200;
                await WriteBytes(res, System.Text.Encoding.UTF8.GetBytes(response), "application/json");
            }
            finally { SessionWorkDir.Value = null; }
            return;
        }

        if (string.IsNullOrEmpty(sessionHeader))
        {
            await WriteHttpError(res, 400, "Missing MCP-Session-Id");
            return;
        }
        if (!sessions.TryGetValue(sessionHeader, out var existing))
        {
            await WriteHttpError(res, 404, "session not found");
            return;
        }

        existing.Touch();
        SessionWorkDir.Value = existing.WorkDir;
        try
        {
            var response = DispatchJsonRpc(body);
            if (response == null)
            {
                res.StatusCode = 202;
                res.Close();
                return;
            }
            res.AddHeader("MCP-Session-Id", existing.Id);
            res.StatusCode = 200;
            await WriteBytes(res, System.Text.Encoding.UTF8.GetBytes(response), "application/json");
        }
        finally { SessionWorkDir.Value = null; }
    }

    private static void HandleDelete(HttpListenerRequest req, HttpListenerResponse res, ConcurrentDictionary<string, HttpSession> sessions)
    {
        var sessionHeader = req.Headers["MCP-Session-Id"] ?? req.Headers["Mcp-Session-Id"];
        if (string.IsNullOrEmpty(sessionHeader))
        {
            res.StatusCode = 400;
            return;
        }
        if (sessions.TryRemove(sessionHeader, out var session))
            session.Dispose();
        res.StatusCode = 204;
    }

    private static string? PeekMethod(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
        }
        catch { return null; }
    }

    private static bool Authorize(HttpListenerRequest req, string? token)
    {
        if (string.IsNullOrEmpty(token)) return true;
        var header = req.Headers["Authorization"];
        if (header != null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && header["Bearer ".Length..] == token)
            return true;
        return req.QueryString["token"] == token;
    }

    private static bool ValidateHostAndOrigin(HttpListenerRequest req, string bindHost)
    {
        if (IsLoopbackHost(bindHost) && !IsLoopbackHost(req.UserHostName.Split(':')[0]))
            return false;
        var origin = req.Headers["Origin"];
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false;
        if (IsLoopbackHost(bindHost) && !IsLoopbackHost(originUri.Host))
            return false;
        return true;
    }

    private static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "::1" or "localhost" or "[::1]";

    private static string ListenerPrefix(string host, int port) =>
        host is "0.0.0.0" or "*" or "+"
            ? $"http://+:{port}/"
            : $"http://{host}:{port}/";

    private static string PublicUrl(HttpOptions options)
    {
        var host = options.Host is "0.0.0.0" or "*" or "+" ? "127.0.0.1" : options.Host;
        return $"http://{host}:{options.Port}/mcp";
    }

    private static async Task WriteHttpError(HttpListenerResponse res, int status, string message)
    {
        res.StatusCode = status;
        await WriteBytes(res, System.Text.Encoding.UTF8.GetBytes(message), "text/plain");
    }

    private static async Task WriteBytes(HttpListenerResponse res, byte[] bytes, string contentType)
    {
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private sealed class HttpSession : IDisposable
    {
        public required string Id { get; init; }
        public required string WorkDir { get; init; }
        public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

        public void Touch() => LastUsedUtc = DateTime.UtcNow;

        public static HttpSession Create(string root)
        {
            var id = Guid.NewGuid().ToString("N");
            var dir = Path.Combine(root, id);
            Directory.CreateDirectory(dir);
            return new HttpSession { Id = id, WorkDir = dir };
        }

        public void Dispose()
        {
            try { Directory.Delete(WorkDir, recursive: true); } catch { }
        }
    }
}
