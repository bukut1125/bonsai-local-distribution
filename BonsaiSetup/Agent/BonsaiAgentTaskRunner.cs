using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BonsaiSetup.Agent;

internal static class BonsaiAgentTaskRunner
{
    public static async Task<int> RunAsync(string installRoot, string task, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("--agent-task 需要提供任務描述。");
        if (task.Length > 6000) throw new ArgumentException("模型接入任務最多 6000 個字元。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var (endpointV1, modelAlias, modelName) = ReadActiveModel(root);
        using var toolService = new ModelExtensionService(root);
        var agentPolicy = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "model-extension.json")))?["agent"]?.AsObject()
                         ?? throw new InvalidDataException("model-extension.agent 設定無法讀取。");
        var maxRounds = agentPolicy["max_rounds"]?.GetValue<int>() ?? 8;
        var maxToolCalls = agentPolicy["max_tool_calls"]?.GetValue<int>() ?? 16;
        var maxCompletionTokens = agentPolicy["max_completion_tokens"]?.GetValue<int>() ?? 4096;
        var systemPrompt = agentPolicy["system_prompt"]?.GetValue<string>()
                           ?? throw new InvalidDataException("model-extension.agent.system_prompt 缺少值。");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var health = await http.GetAsync(endpointV1[..^3] + "/health", cancellationToken).ConfigureAwait(false);
        health.EnsureSuccessStatusCode();

        var tools = ToOpenAiTools(toolService.CreateToolDefinitions());
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = systemPrompt
            },
            new JsonObject { ["role"] = "user", ["content"] = task }
        };

        var totalCalls = 0;
        for (var round = 1; round <= maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new JsonObject
            {
                ["model"] = modelAlias,
                ["messages"] = messages.DeepClone(),
                ["tools"] = tools.DeepClone(),
                ["tool_choice"] = "auto",
                ["temperature"] = 0,
                ["max_tokens"] = maxCompletionTokens,
                ["stream"] = false
            };
            Console.WriteLine($"代理回合 {round}/{maxRounds} · model={modelName}");
            using var response = await http.PostAsJsonAsync(endpointV1 + "/chat/completions", request, cancellationToken).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"本機 OpenAI-compatible API 回傳 HTTP {(int)response.StatusCode}: {raw}");
            using var document = JsonDocument.Parse(raw);
            var choice = document.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0)
            {
                var text = ReadAssistantText(message);
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("本機模型沒有回傳文字或 tool_calls。此模型/runtime 可能不支援本次工具呼叫契約。");
                Console.WriteLine("代理完成：");
                Console.WriteLine(text);
                return 0;
            }

            totalCalls += calls.GetArrayLength();
            if (totalCalls > maxToolCalls) throw new InvalidOperationException($"代理超過本次工作上限 {maxToolCalls} 次工具呼叫。");
            var assistantMessage = JsonNode.Parse(message.GetRawText()) as JsonObject
                                   ?? throw new InvalidDataException("模型 tool_calls message 無法解析。");
            assistantMessage["role"] = "assistant";
            messages.Add(assistantMessage);

            foreach (var call in calls.EnumerateArray())
            {
                var callId = call.GetProperty("id").GetString() ?? throw new InvalidDataException("tool_call 缺少 id。");
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? throw new InvalidDataException("tool_call 缺少 function.name。");
                var argumentText = function.GetProperty("arguments").GetString() ?? "{}";
                var arguments = JsonNode.Parse(argumentText) as JsonObject ?? throw new InvalidDataException($"tool_call {name} arguments 必須是 JSON object。");
                Console.WriteLine("代理呼叫工具：" + name);
                JsonNode result;
                try
                {
                    result = await toolService.CallToolAsync(name, arguments, cancellationToken, Console.WriteLine).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or HttpRequestException or JsonException or InvalidOperationException)
                {
                    result = new JsonObject { ["error"] = exception.Message };
                }
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = callId,
                    ["content"] = result.ToJsonString()
                });
            }
        }

        throw new InvalidOperationException($"代理已達 {maxRounds} 回合上限；請縮小接入任務後重試。");
    }

    private static (string EndpointV1, string Alias, string Name) ReadActiveModel(string root)
    {
        var configPath = Path.Combine(root, "config", "model-registry.json");
        var activePath = Path.Combine(root, "runtime", "llama-local", "active-local-runtime.json");
        if (!File.Exists(activePath)) throw new InvalidOperationException("目前沒有本 Bonsai launcher 管理且狀態為 Ready 的本機模型。");
        var registry = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? throw new InvalidDataException("model-registry.json 無法解析。");
        var endpoint = registry["endpoint"] as JsonObject ?? throw new InvalidDataException("model-registry.endpoint 缺少設定。");
        var host = endpoint["host"]?.GetValue<string>() ?? "";
        var port = endpoint["port"]?.GetValue<int>() ?? 0;
        if (host != "127.0.0.1" || port != 18080) throw new InvalidDataException("代理 endpoint 必須固定為 127.0.0.1:18080。");
        var endpointV1 = $"http://{host}:{port}/v1";
        var active = JsonNode.Parse(File.ReadAllText(activePath))?.AsObject() ?? throw new InvalidDataException("active-local-runtime.json 無法解析。");
        if (!string.Equals(active["status"]?.GetValue<string>(), "ready", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目前 Bonsai runtime 未處於 Ready；未傳送代理任務。");
        var modelId = active["model_id"]?.GetValue<string>() ?? "";
        var profileId = active["profile_id"]?.GetValue<string>() ?? "";
        var profile = registry["profiles"]?.AsArray().OfType<JsonObject>()
            .SingleOrDefault(item => item["id"]?.GetValue<string>() == profileId && item["model_id"]?.GetValue<string>() == modelId)
            ?? throw new InvalidDataException("active runtime model/profile 不在 model-registry.json。");
        var alias = profile["model_alias"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(alias)) throw new InvalidDataException("active model profile 缺少 model_alias。");
        return (endpointV1, alias, modelId);
    }

    private static JsonArray ToOpenAiTools(JsonArray mcpTools)
    {
        var result = new JsonArray();
        foreach (var tool in mcpTools.OfType<JsonObject>())
        {
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool["name"]?.DeepClone(),
                    ["description"] = tool["description"]?.DeepClone(),
                    ["parameters"] = tool["inputSchema"]?.DeepClone()
                }
            });
        }
        return result;
    }

    private static string ReadAssistantText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Join(Environment.NewLine, content.EnumerateArray()
            .Where(item => item.TryGetProperty("text", out _))
            .Select(item => item.GetProperty("text").GetString() ?? ""));
    }
}
