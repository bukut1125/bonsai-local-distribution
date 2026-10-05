using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BonsaiSetup.Distribution;

namespace BonsaiSetup.Agent;

internal sealed class ModelExtensionService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex ModelIdRegex = new("^[a-z0-9][a-z0-9-]{1,62}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _root;
    private readonly string _modelsRoot;
    private readonly JsonObject _policy;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };
    private readonly ResumableDownloader _downloader = new();

    public ModelExtensionService(string installRoot)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        _modelsRoot = Path.Combine(_root, "models");
        if (!File.Exists(Path.Combine(_root, "config", "model-registry.json")))
            throw new FileNotFoundException("Bonsai 安裝目錄沒有 model-registry.json。", Path.Combine(_root, "config", "model-registry.json"));
        var extensionPath = Path.Combine(_root, "config", "model-extension.json");
        _policy = JsonNode.Parse(File.ReadAllText(extensionPath))?.AsObject()
                  ?? throw new FileNotFoundException("Bonsai 安裝目錄沒有有效的 model-extension.json。", extensionPath);
        if (_policy["schema_version"]?.GetValue<int>() != 1) throw new InvalidDataException("不支援的 model-extension.json schema。");
    }

    public JsonArray CreateToolDefinitions()
    {
        var profiles = _policy["profiles"]?.AsObject() ?? throw new InvalidDataException("model-extension profiles policy is missing.");
        var minimumContext = profiles["minimum_context_size"]?.GetValue<int>() ?? 2048;
        var maximumContext = profiles["maximum_context_size"]?.GetValue<int>() ?? 131072;
        return new JsonArray
        {
            Tool("bonsai_get_hardware", "讀取安裝時偵測的 OS、GPU VRAM、RAM、CPU 與磁碟資訊。", new JsonObject
            {
                ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false
            }),
            Tool("bonsai_list_models", "列出目前 launcher 的模型、runtime backend 與 profiles。", new JsonObject
            {
                ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false
            }),
            Tool("bonsai_search_huggingface_gguf", "搜尋 Hugging Face 公開 GGUF repositories。只讀取公開 metadata，不下載權重。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "模型名稱或用途關鍵字" } },
                ["required"] = new JsonArray("query"), ["additionalProperties"] = false
            }),
            Tool("bonsai_inspect_huggingface_gguf", "讀取指定 Hugging Face repository 的 revision、GGUF 檔案大小與 SHA-256。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["repo_id"] = new JsonObject { ["type"] = "string", ["description"] = "owner/repository" } },
                ["required"] = new JsonArray("repo_id"), ["additionalProperties"] = false
            }),
            Tool("bonsai_install_huggingface_gguf", "下載並驗證一個公開 GGUF，建立本機 user model registry entry 與保守 runtime profiles。只接受目前已安裝 backend 支援的 GGUF。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repo_id"] = new JsonObject { ["type"] = "string" },
                    ["file_name"] = new JsonObject { ["type"] = "string", ["description"] = "必須是 inspect 工具列出的精確 GGUF 檔名" },
                    ["display_name"] = new JsonObject { ["type"] = "string" },
                    ["backend_id"] = new JsonObject { ["type"] = "string", ["description"] = "必須取自 bonsai_list_models 的既有 backend" },
                    ["context_size"] = new JsonObject { ["type"] = "integer", ["minimum"] = minimumContext, ["maximum"] = maximumContext },
                    ["gpu_layers"] = new JsonObject { ["type"] = "string", ["description"] = "all 或 0 到 99 的數字字串" },
                    ["quantization"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("repo_id", "file_name", "display_name", "backend_id"),
                ["additionalProperties"] = false
            })
        };
    }

    public async Task<JsonNode> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken, Action<string>? progress = null)
    {
        return name switch
        {
            "bonsai_get_hardware" => await ReadHardwareAsync(cancellationToken).ConfigureAwait(false),
            "bonsai_list_models" => await ListModelsAsync(cancellationToken).ConfigureAwait(false),
            "bonsai_search_huggingface_gguf" => await SearchHuggingFaceAsync(ReadRequired(arguments, "query"), cancellationToken).ConfigureAwait(false),
            "bonsai_inspect_huggingface_gguf" => await InspectHuggingFaceAsync(ReadRequired(arguments, "repo_id"), cancellationToken).ConfigureAwait(false),
            "bonsai_install_huggingface_gguf" => await InstallHuggingFaceModelAsync(arguments, cancellationToken, progress).ConfigureAwait(false),
            _ => throw new ArgumentException($"未知 Bonsai model integration tool: {name}")
        };
    }

    private async Task<JsonNode> ReadHardwareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(_root, "config", "hardware.json");
        if (!File.Exists(path)) throw new FileNotFoundException("尚未建立 config/hardware.json。", path);
        return JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
               ?? throw new InvalidDataException("hardware.json 是空檔。 ");
    }

    private async Task<JsonNode> ListModelsAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "config", "model-registry.json");
        var registry = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))?.AsObject()
                       ?? throw new InvalidDataException("model-registry.json 無法解析。 ");
        return new JsonObject
        {
            ["default_model_id"] = registry["default_model_id"]?.DeepClone(),
            ["default_profile_id"] = registry["default_profile_id"]?.DeepClone(),
            ["models"] = SelectArray(registry, "models", "id", "display_name", "source_repository", "weight_format", "quantization"),
            ["backends"] = SelectArray(registry, "backends", "id", "display_name", "capabilities"),
            ["profiles"] = SelectArray(registry, "profiles", "id", "display_name", "model_id", "backend_id", "context_size", "gpu_layers")
        };
    }

    private async Task<JsonNode> SearchHuggingFaceAsync(string query, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length is < 2 or > 120) throw new ArgumentException("模型搜尋字串長度須介於 2 到 120 個字元。 ");
        var uri = new Uri("https://huggingface.co/api/models?search=" + Uri.EscapeDataString(query) + "&filter=gguf&sort=downloads&direction=-1&limit=10");
        using var response = await SendHttpsAsync(uri, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Hugging Face model search returned a non-array response.");
        var results = new JsonArray();
        var resultLimit = Math.Clamp(_policy["search_result_limit"]?.GetValue<int>() ?? 10, 1, 20);
        foreach (var item in document.RootElement.EnumerateArray().Take(resultLimit))
        {
            var id = GetString(item, "id") ?? GetString(item, "modelId");
            if (string.IsNullOrWhiteSpace(id)) continue;
            results.Add(new JsonObject
            {
                ["repo_id"] = id,
                ["revision"] = GetString(item, "sha") ?? "",
                ["downloads"] = GetInt64(item, "downloads"),
                ["likes"] = GetInt64(item, "likes"),
                ["last_modified"] = GetString(item, "lastModified") ?? "",
                ["pipeline_tag"] = GetString(item, "pipeline_tag") ?? "",
                ["gated"] = GetBoolean(item, "gated"),
                ["private"] = GetBoolean(item, "private")
            });
        }
        return new JsonObject { ["source"] = "Hugging Face Hub public model API", ["query"] = query, ["results"] = results };
    }

    private async Task<JsonNode> InspectHuggingFaceAsync(string repoId, CancellationToken cancellationToken)
    {
        ValidateRepoId(repoId);
        using var response = await SendHttpsAsync(new Uri($"https://huggingface.co/api/models/{EscapeRepo(repoId)}?blobs=true"), cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var revision = GetString(root, "sha") ?? throw new InvalidDataException("Hugging Face repository response omitted revision SHA.");
        var files = new JsonArray();
        if (root.TryGetProperty("siblings", out var siblings) && siblings.ValueKind == JsonValueKind.Array)
        {
            foreach (var sibling in siblings.EnumerateArray())
            {
                var fileName = GetString(sibling, "rfilename");
                if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                var size = GetInt64(sibling, "size") ?? GetInt64(sibling, "lfs", "size");
                var sha = NormalizeLfsSha(GetString(sibling, "lfs", "sha256") ?? GetString(sibling, "lfs", "oid"));
                files.Add(new JsonObject
                {
                    ["file_name"] = fileName,
                    ["size_bytes"] = size,
                    ["sha256"] = sha,
                    ["sha256_available"] = sha.Length == 64
                });
            }
        }
        return new JsonObject
        {
            ["repo_id"] = GetString(root, "id") ?? repoId,
            ["revision"] = revision,
            ["license"] = GetString(root, "cardData", "license") ?? "",
            ["gated"] = GetBoolean(root, "gated"),
            ["private"] = GetBoolean(root, "private"),
            ["files"] = files
        };
    }

    private async Task<JsonNode> InstallHuggingFaceModelAsync(JsonObject arguments, CancellationToken cancellationToken, Action<string>? progress)
    {
        var repoId = ReadRequired(arguments, "repo_id");
        ValidateRepoId(repoId);
        var requestedFile = ReadRequired(arguments, "file_name");
        var displayName = ReadRequired(arguments, "display_name").Trim();
        var backendId = ReadRequired(arguments, "backend_id");
        var quantization = ReadString(arguments, "quantization", InferQuantization(requestedFile));
        if (displayName.Length is < 2 or > 100) throw new ArgumentException("display_name 長度須介於 2 到 100 個字元。 ");

        var inspection = await InspectHuggingFaceAsync(repoId, cancellationToken).ConfigureAwait(false);
        if (inspection["private"]?.GetValue<bool>() == true) throw new InvalidDataException("Private Hugging Face repositories are not supported by the public model integration tool.");
        if (inspection["gated"]?.GetValue<bool>() == true) throw new InvalidDataException("This Hugging Face repository is gated and requires separate access approval; no download was attempted.");
        var revision = inspection["revision"]!.GetValue<string>();
        var file = inspection["files"]!.AsArray().OfType<JsonObject>()
            .SingleOrDefault(item => string.Equals(item["file_name"]?.GetValue<string>(), requestedFile, StringComparison.Ordinal))
            ?? throw new InvalidDataException("指定 GGUF 不在該 repository 的公開檔案清單。先呼叫 inspect 並使用清單中的精確 file_name。 ");
        var sizeBytes = file["size_bytes"]?.GetValue<long>() ?? 0;
        var sha256 = file["sha256"]?.GetValue<string>() ?? "";
        if (sizeBytes <= 0 || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("此 GGUF 沒有可用的精確大小與 LFS SHA-256，拒絕登錄。 ");

        var fileName = ValidateRelativeModelFileName(requestedFile);
        var modelId = MakeModelId(repoId, fileName, revision);
        var registryPath = Path.Combine(_root, "config", "model-registry.json");
        var registry = JsonNode.Parse(await File.ReadAllTextAsync(registryPath, cancellationToken).ConfigureAwait(false))?.AsObject()
                       ?? throw new InvalidDataException("model-registry.json 無法解析。 ");
        var backend = registry["backends"]!.AsArray().OfType<JsonObject>()
            .SingleOrDefault(item => string.Equals(item["id"]?.GetValue<string>(), backendId, StringComparison.Ordinal))
            ?? throw new ArgumentException("backend_id 必須取自 bonsai_list_models。 ");
        var backendCapabilities = backend["capabilities"]?.AsArray() ?? new JsonArray();
        if (!backendCapabilities.Any(item => string.Equals(item?.GetValue<string>(), "gguf", StringComparison.Ordinal)))
            throw new InvalidDataException("所選 backend 沒有 gguf capability。 ");
        var isGpuBackend = backendCapabilities.Any(item => string.Equals(item?.GetValue<string>(), "cuda", StringComparison.Ordinal));
        var profilePolicy = _policy["profiles"]?.AsObject() ?? throw new InvalidDataException("model-extension profiles policy is missing.");
        var profileDefaults = (profilePolicy[isGpuBackend ? "gpu" : "cpu"] as JsonObject)?.DeepClone().AsObject()
                              ?? throw new InvalidDataException("model-extension runtime profile defaults are missing.");
        var minimumContext = profilePolicy["minimum_context_size"]?.GetValue<int>() ?? 2048;
        var maximumContext = profilePolicy["maximum_context_size"]?.GetValue<int>() ?? 131072;
        var contextSize = ReadInt(arguments, "context_size", profileDefaults["default_context_size"]?.GetValue<int>() ?? 8192);
        var gpuLayers = ReadString(arguments, "gpu_layers", profileDefaults["gpu_layers"]?.GetValue<string>() ?? "all");
        if (contextSize < minimumContext || contextSize > maximumContext) throw new ArgumentException($"context_size 必須介於 {minimumContext} 與 {maximumContext}。 ");
        if (gpuLayers != "all" && (!int.TryParse(gpuLayers, out var layers) || layers is < 0 or > 99))
            throw new ArgumentException("gpu_layers 必須是 all 或 0 到 99。 ");
        if (!isGpuBackend && gpuLayers != "0")
            throw new ArgumentException("CPU backend 的 gpu_layers 必須是 0。 ");

        var modelDirectory = Path.Combine(_modelsRoot, "user", modelId);
        var modelPath = Path.GetFullPath(Path.Combine(modelDirectory, fileName));
        EnsurePathWithin(_modelsRoot, modelPath);
        EnsureDiskSpace(Path.GetPathRoot(_root)!, sizeBytes, _policy["download_disk_reserve_bytes"]?.GetValue<long>() ?? 0);

        var asset = new DownloadAsset
        {
            Id = modelId,
            Kind = "model",
            Url = BuildResolveUrl(repoId, revision, fileName),
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            TargetRelativePath = Path.GetRelativePath(_root, modelPath),
            ArchiveFormat = "none",
            Revision = revision,
            License = inspection["license"]?.GetValue<string>() ?? ""
        };
        await _downloader.DownloadAsync(asset, modelPath, cancellationToken, progress: (transferred, total) =>
        {
            if (total > 0) progress?.Invoke($"正在下載 {displayName}：{ResumableDownloader.FormatBytes(transferred)} / {ResumableDownloader.FormatBytes(total)} · {Math.Min(100d, transferred * 100d / total):0}%");
        }).ConfigureAwait(false);

        var profileId = modelId + "-stable";
        var modelNode = new JsonObject
        {
            ["id"] = modelId,
            ["display_name"] = displayName,
            ["storage_directory"] = Path.Combine("user", modelId).Replace('\\', '/'),
            ["model_file"] = fileName.Replace('\\', '/'),
            ["expected_bytes"] = sizeBytes,
            ["expected_sha256"] = sha256,
            ["source_repository"] = repoId,
            ["source_revision"] = revision,
            ["weight_format"] = "GGUF",
            ["quantization"] = quantization,
            ["chat_template"] = "GGUF embedded chat template",
            ["projector_required"] = false,
            ["required_capabilities"] = new JsonArray("gguf")
        };
        var profileNode = CreateProfile(profileId, displayName, modelId, backendId, contextSize, gpuLayers, profileDefaults, isGpuBackend);
        var cpuDefaults = (profilePolicy["cpu"] as JsonObject)?.DeepClone().AsObject()
                          ?? throw new InvalidDataException("model-extension CPU profile defaults are missing.");
        var cpuProfileNode = CreateCpuFallbackProfile(modelId, displayName, registry, cpuDefaults);
        var catalogPath = Path.Combine(_root, "config", "user-model-registry.json");
        var catalog = File.Exists(catalogPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(catalogPath, cancellationToken).ConfigureAwait(false))?.AsObject()
              ?? throw new InvalidDataException("user-model-registry.json 無法解析。 ")
            : new JsonObject { ["schema_version"] = 1, ["models"] = new JsonArray(), ["profiles"] = new JsonArray() };
        var catalogModels = catalog["models"] as JsonArray ?? throw new InvalidDataException("user-model-registry.models 必須是陣列。 ");
        var catalogProfiles = catalog["profiles"] as JsonArray ?? throw new InvalidDataException("user-model-registry.profiles 必須是陣列。 ");
        if (catalogModels.OfType<JsonObject>().Any(item => item["id"]?.GetValue<string>() == modelId))
            throw new InvalidOperationException($"模型 {modelId} 已在 user registry；不會覆寫現有 entry。 ");
        catalogModels.Add(modelNode.DeepClone());
        catalogProfiles.Add(profileNode.DeepClone());
        if (cpuProfileNode is not null) catalogProfiles.Add(cpuProfileNode.DeepClone());
        await WriteJsonAtomicAsync(catalogPath, catalog, cancellationToken).ConfigureAwait(false);

        DistributionInstaller.MergeUserModelCatalog(_root, registry);
        await WriteJsonAtomicAsync(registryPath, registry, cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["status"] = "registered",
            ["model_id"] = modelId,
            ["profile_id"] = profileId,
            ["cpu_fallback_profile_id"] = cpuProfileNode?["id"]?.DeepClone(),
            ["installed_path"] = modelPath,
            ["size_bytes"] = sizeBytes,
            ["sha256"] = sha256,
            ["source_repository"] = repoId,
            ["source_revision"] = revision,
            ["selected"] = false,
            ["started"] = false
        };
    }

    private static JsonObject CreateProfile(string profileId, string displayName, string modelId, string backendId, int contextSize, string gpuLayers, JsonObject defaults, bool gpu)
    {
        return new JsonObject
        {
            ["id"] = profileId,
            ["display_name"] = displayName + " · " + contextSize.ToString("N0") + " context",
            ["model_id"] = modelId,
            ["backend_id"] = backendId,
            ["context_size"] = contextSize,
            ["gpu_layers"] = gpuLayers,
            ["cache_type_k"] = defaults["cache_type_k"]?.DeepClone(),
            ["cache_type_v"] = defaults["cache_type_v"]?.DeepClone(),
            ["kv_offload"] = defaults["kv_offload"]?.DeepClone(),
            ["batch_size"] = defaults["batch_size"]?.DeepClone(),
            ["ubatch_size"] = defaults["ubatch_size"]?.DeepClone(),
            ["flash_attention"] = defaults["flash_attention"]?.DeepClone(),
            ["model_alias"] = modelId,
            ["hardware_requirements"] = BuildHardwareRequirements(defaults, gpu),
            ["adapters"] = new JsonArray()
        };
    }

    private static JsonObject? CreateCpuFallbackProfile(string modelId, string displayName, JsonObject registry, JsonObject defaults)
    {
        var cpuBackend = registry["backends"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(item => item["id"]?.GetValue<string>()?.Contains("cpu", StringComparison.OrdinalIgnoreCase) == true);
        if (cpuBackend is null) return null;
        var contextSize = defaults["default_context_size"]?.GetValue<int>() ?? 4096;
        return new JsonObject
        {
            ["id"] = modelId + "-cpu",
            ["display_name"] = displayName + " · CPU fallback",
            ["model_id"] = modelId,
            ["backend_id"] = cpuBackend["id"]?.DeepClone(),
            ["context_size"] = contextSize,
            ["gpu_layers"] = defaults["gpu_layers"]?.DeepClone(),
            ["cache_type_k"] = defaults["cache_type_k"]?.DeepClone(),
            ["cache_type_v"] = defaults["cache_type_v"]?.DeepClone(),
            ["kv_offload"] = defaults["kv_offload"]?.DeepClone(),
            ["batch_size"] = defaults["batch_size"]?.DeepClone(),
            ["ubatch_size"] = defaults["ubatch_size"]?.DeepClone(),
            ["flash_attention"] = defaults["flash_attention"]?.DeepClone(),
            ["model_alias"] = modelId + "-cpu",
            ["hardware_requirements"] = BuildHardwareRequirements(defaults, false),
            ["adapters"] = new JsonArray()
        };
    }

    private static JsonObject BuildHardwareRequirements(JsonObject defaults, bool gpu)
    {
        return new JsonObject
        {
            ["gpu_required"] = gpu,
            ["gpu_vendor"] = gpu ? "NVIDIA" : "ANY",
            ["min_vram_mib"] = defaults["min_vram_mib"]?.DeepClone(),
            ["min_free_vram_mib"] = defaults["min_free_vram_mib"]?.DeepClone(),
            ["min_free_ram_mib"] = defaults["min_free_ram_mib"]?.DeepClone(),
            ["backend_device_pattern"] = defaults["backend_device_pattern"]?.DeepClone()
        };
    }

    private async Task<HttpResponseMessage> SendHttpsAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("Hugging Face URL 必須是無帳密的 HTTPS URL。 ");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("BonsaiLocalDistribution/1.0");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            response.Dispose();
            throw new HttpRequestException("Hugging Face request redirected to non-HTTPS.");
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new HttpRequestException("Hugging Face repository not found or not public.");
        }
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task WriteJsonAtomicAsync(string path, JsonNode node, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        await File.WriteAllTextAsync(temporary, node.ToJsonString(JsonOptions) + Environment.NewLine, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static void EnsureDiskSpace(string driveRoot, long modelSizeBytes, long reserveBytes)
    {
        var drive = new DriveInfo(driveRoot);
        var required = checked(modelSizeBytes + reserveBytes);
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"磁碟空間不足：需要 {required:N0} bytes，可用 {drive.AvailableFreeSpace:N0} bytes。模型尚未登錄。 ");
    }

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema
    };

    private static JsonArray SelectArray(JsonObject source, string key, params string[] fields)
    {
        var result = new JsonArray();
        foreach (var item in source[key]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var projection = new JsonObject();
            foreach (var field in fields)
                if (item.TryGetPropertyValue(field, out var value)) projection[field] = value?.DeepClone();
            result.Add(projection);
        }
        return result;
    }

    private static string ReadRequired(JsonObject value, string property)
    {
        var text = value[property]?.GetValue<string>()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? throw new ArgumentException($"缺少 {property}。 ") : text;
    }

    private static string ReadString(JsonObject value, string property, string fallback) => value[property]?.GetValue<string>()?.Trim() is { Length: > 0 } text ? text : fallback;
    private static int ReadInt(JsonObject value, string property, int fallback) => value[property]?.GetValue<int>() ?? fallback;

    private static long? GetInt64(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var item in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(item, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt64(out var number) ? number : null;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var item in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(item, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static bool GetBoolean(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string NormalizeLfsSha(string? oid)
    {
        if (string.IsNullOrWhiteSpace(oid)) return "";
        var value = oid.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? oid[7..] : oid;
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : "";
    }

    private static string ValidateRepoId(string repoId)
    {
        repoId = repoId.Trim();
        if (repoId.Length is < 3 or > 160 || repoId.Contains('\\') || repoId.StartsWith('/') || repoId.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("repo_id 必須是 owner/repository。 ");
        var parts = repoId.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new ArgumentException("repo_id 必須是合法的 owner/repository。 ");
        return repoId;
    }

    private static string EscapeRepo(string repoId) => string.Join('/', ValidateRepoId(repoId).Split('/').Select(Uri.EscapeDataString));

    private static string BuildResolveUrl(string repoId, string revision, string fileName)
    {
        var encodedFileName = string.Join('/', ValidateRelativeModelFileName(fileName).Split('/').Select(Uri.EscapeDataString));
        return $"https://huggingface.co/{EscapeRepo(repoId)}/resolve/{Uri.EscapeDataString(revision)}/{encodedFileName}?download=true";
    }

    private static string ValidateRelativeModelFileName(string fileName)
    {
        fileName = fileName.Replace('\\', '/').Trim();
        if (fileName.Length is < 1 or > 240 || fileName.StartsWith('/') || fileName.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("GGUF filename must be a relative repository path without dot segments. ");
        if (!fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("目前只支援 GGUF 權重。 ");
        return fileName;
    }

    private static void EnsurePathWithin(string root, string candidate)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型輸出路徑超出 models 目錄。 ");
    }

    private static string MakeModelId(string repoId, string fileName, string revision)
    {
        var label = Regex.Replace(repoId + "-" + Path.GetFileNameWithoutExtension(fileName), "[^a-zA-Z0-9]+", "-").Trim('-').ToLowerInvariant();
        if (label.Length > 38) label = label[..38].TrimEnd('-');
        var identity = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(repoId + "\n" + fileName + "\n" + revision));
        var suffix = Convert.ToHexString(identity.AsSpan(0, 4)).ToLowerInvariant();
        var id = "user-" + label + "-" + suffix;
        if (!ModelIdRegex.IsMatch(id)) throw new ArgumentException("無法將 repository id 轉為合法 model id。 ");
        return id;
    }

    private static string InferQuantization(string fileName)
    {
        var match = Regex.Match(fileName, "(?i)(IQ[1-8]_[A-Z0-9_]+|Q[1-8]_[A-Z0-9_]+|F16|BF16|F32)");
        return match.Success ? match.Value.ToUpperInvariant() : "GGUF quantization not specified";
    }

    public void Dispose()
    {
        _downloader.Dispose();
        _http.Dispose();
    }
}
