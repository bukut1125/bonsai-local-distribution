using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BonsaiSetup.Agent;

internal sealed class McpServer(string installRoot)
{
    private const string ModernProtocolVersion = "2026-07-28";
    private static readonly string[] SupportedProtocolVersions = ["2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private static readonly JsonObject ServerInfo = new() { ["name"] = "bonsai-model-integration", ["version"] = "1.0.0" };
    private readonly ModelExtensionService _tools = new(installRoot);
    private bool _legacyInitialized;
    private bool _shutdown;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(Console.Error);

        while (!_shutdown && !cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? request;
            try { request = JsonNode.Parse(line); }
            catch (JsonException)
            {
                await WriteErrorAsync(output, null, -32700, "Parse error").ConfigureAwait(false);
                continue;
            }

            if (request is not JsonObject message || message["method"]?.GetValue<string>() is not { } method) continue;
            var id = message["id"]?.DeepClone();
            if (id is null) continue;
            var parameters = message["params"] as JsonObject;
            var protocolVersion = parameters?["_meta"]?["io.modelcontextprotocol/protocolVersion"]?.GetValue<string>();
            var modern = string.Equals(protocolVersion, ModernProtocolVersion, StringComparison.Ordinal);
            if (method == "server/discover")
            {
                if (!modern || parameters?["_meta"]?["io.modelcontextprotocol/clientCapabilities"] is not JsonObject)
                {
                    await WriteErrorAsync(output, id, -32602, "server/discover requires 2026-07-28 request metadata.").ConfigureAwait(false);
                    continue;
                }
                await WriteResultAsync(output, id, new JsonObject
                {
                    ["supportedVersions"] = SupportedVersionsJson(),
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["_meta"] = new JsonObject { ["io.modelcontextprotocol/serverInfo"] = ServerInfo.DeepClone() },
                    ["instructions"] = "This MCP server exposes local hardware/model profiles, public Hugging Face GGUF metadata search, and typed model download/registry tools. Model weight downloads use the installed user-chosen Bonsai directory.",
                    ["ttlMs"] = 0,
                    ["cacheScope"] = "private"
                }, modern: true).ConfigureAwait(false);
                continue;
            }

            if (modern && parameters?["_meta"]?["io.modelcontextprotocol/clientCapabilities"] is not JsonObject)
            {
                await WriteErrorAsync(output, id, -32602, "Modern MCP requests require clientCapabilities metadata.").ConfigureAwait(false);
                continue;
            }
            if (!modern && protocolVersion is not null && !SupportedProtocolVersions.Contains(protocolVersion, StringComparer.Ordinal))
            {
                await WriteUnsupportedVersionAsync(output, id).ConfigureAwait(false);
                continue;
            }
            if (!modern && method != "initialize" && !_legacyInitialized && protocolVersion is null)
            {
                await WriteErrorAsync(output, id, -32602, "Legacy MCP requests must follow initialize.").ConfigureAwait(false);
                continue;
            }
            try
            {
                switch (method)
                {
                    case "initialize":
                        var requestedVersion = parameters?["protocolVersion"]?.GetValue<string>() ?? "";
                        if (!SupportedProtocolVersions.Contains(requestedVersion, StringComparer.Ordinal) || requestedVersion == ModernProtocolVersion)
                        {
                            await WriteErrorAsync(output, id, -32602, "initialize requires a supported handshake-era protocol version.").ConfigureAwait(false);
                            break;
                        }
                        _legacyInitialized = true;
                        await WriteResultAsync(output, id, new JsonObject
                        {
                            ["protocolVersion"] = requestedVersion,
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                            ["serverInfo"] = ServerInfo.DeepClone()
                        }).ConfigureAwait(false);
                        break;
                    case "notifications/initialized":
                        break;
                    case "ping":
                        await WriteResultAsync(output, id, new JsonObject(), modern).ConfigureAwait(false);
                        break;
                    case "tools/list":
                        var toolList = new JsonObject { ["tools"] = _tools.CreateToolDefinitions() };
                        if (modern)
                        {
                            toolList["ttlMs"] = 0;
                            toolList["cacheScope"] = "private";
                        }
                        await WriteResultAsync(output, id, toolList, modern).ConfigureAwait(false);
                        break;
                    case "tools/call":
                        await HandleToolCallAsync(output, id, parameters, modern, cancellationToken).ConfigureAwait(false);
                        break;
                    case "shutdown":
                        _shutdown = true;
                        await WriteResultAsync(output, id, null).ConfigureAwait(false);
                        break;
                    default:
                        await WriteErrorAsync(output, id, -32601, $"Method not found: {method}").ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
            {
                await WriteErrorAsync(output, id, -32603, exception.Message).ConfigureAwait(false);
            }
        }

        _tools.Dispose();
        return 0;
    }

    private async Task HandleToolCallAsync(StreamWriter output, JsonNode? id, JsonObject? parameters, bool modern, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        if (string.IsNullOrWhiteSpace(name))
        {
            await WriteErrorAsync(output, id, -32602, "tools/call requires params.name.").ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await _tools.CallToolAsync(name, arguments, cancellationToken).ConfigureAwait(false);
            await WriteResultAsync(output, id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.ToJsonString() }),
                ["isError"] = false
            }, modern).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            await WriteResultAsync(output, id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = exception.Message }),
                ["isError"] = true
            }, modern).ConfigureAwait(false);
        }
    }

    private static async Task WriteUnsupportedVersionAsync(StreamWriter output, JsonNode? id)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = -32022,
                ["message"] = "Unsupported protocol version",
                ["data"] = new JsonObject { ["supported"] = SupportedVersionsJson() }
            }
        };
        await output.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
    }

    private static Task WriteResultAsync(StreamWriter output, JsonNode? id, JsonNode? result, bool modern = false)
    {
        var payload = result?.DeepClone() as JsonObject ?? new JsonObject();
        if (modern)
        {
            payload["resultType"] ??= "complete";
            payload["_meta"] ??= new JsonObject();
            payload["_meta"]!["io.modelcontextprotocol/serverInfo"] ??= ServerInfo.DeepClone();
        }
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = payload };
        return output.WriteLineAsync(response.ToJsonString());
    }

    private static JsonArray SupportedVersionsJson()
    {
        var result = new JsonArray();
        foreach (var version in SupportedProtocolVersions) result.Add(version);
        return result;
    }

    private static Task WriteErrorAsync(StreamWriter output, JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        return output.WriteLineAsync(response.ToJsonString());
    }
}
