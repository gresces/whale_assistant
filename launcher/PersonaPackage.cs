using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhaleLauncher;

// 人设包只有纯数据。显式白名单防止分享账号、密钥、权限或执行配置。
internal static class PersonaPackage
{
    // 导出包最多 192 KiB 正文 + 几 KB 清单；给到 1 MiB 足够，再大基本是误选了别的包或压缩炸弹。
    private const int PackageLimit = 1024 * 1024;
    private const int PersonaLimit = 192 * 1024;
    private const int ManifestLimit = 8 * 1024;
    private const int EntryLimit = 32;
    // 内核读取自建预设的上限（src/persona/presets.js MAX_USER_BYTES），超了会被静默忽略。
    private const int UserPresetLimit = 256 * 1024;

    static readonly Dictionary<string, int> Strings = new()
    {
        ["personaName"] = 40, ["job"] = 80, ["appearance"] = 400,
        ["personality"] = 500, ["background"] = 500, ["customPrompt"] = 24000, ["ownerName"] = 24
    };
    static readonly Dictionary<string, (double Min, double Max)> Numbers = new()
    {
        ["age"] = (1,120), ["privateMaxChars"] = (40,800), ["groupMaxChars"] = (20,300),
        ["historyTurns"] = (4,400), ["groupHistoryTurns"] = (2,300), ["maxBubbles"] = (1,8),
        ["discussionMaxChars"] = (20,4000), ["emojiChance"] = (0,1)
    };

    static string ReadText(JsonElement item, string key)
        => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    internal static Dictionary<string, object> Read(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("人设必须是 JSON 对象");
        if (data.TryGetProperty("schemaVersion", out var declared)
            && (declared.ValueKind != JsonValueKind.Number || !declared.TryGetInt32(out var declaredVersion) || declaredVersion != 1))
            throw new InvalidDataException("不支持的人设数据版本");
        var result = new Dictionary<string, object>();
        foreach (var (key, max) in Strings)
        {
            if (!data.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String || value.GetString().Length > max)
                throw new InvalidDataException("人设字段无效：" + key);
            result[key] = value.GetString().Trim();
        }
        foreach (var (key, range) in Numbers)
        {
            if (!data.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var n)
                || !double.IsFinite(n) || n < range.Min || n > range.Max || (key != "emojiChance" && n != Math.Truncate(n)))
                throw new InvalidDataException("人设数值无效：" + key);
            result[key] = n;
        }
        foreach (var key in new[] { "masterRoast", "proactive", "opinions" })
        {
            if (!data.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("人设开关无效：" + key);
            result[key] = value.GetBoolean();
        }
        if (data.TryGetProperty("emoji", out var emoji))
        {
            if (emoji.ValueKind != JsonValueKind.String || !new[] { "关", "少", "正常", "多" }.Contains(emoji.GetString()))
                throw new InvalidDataException("颜文字档位无效");
            result["emoji"] = emoji.GetString();
        }
        if (data.TryGetProperty("aliases", out var aliases))
        {
            if (aliases.ValueKind != JsonValueKind.Array || aliases.GetArrayLength() > 30
                || aliases.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString().Length > 40))
                throw new InvalidDataException("人设别名无效");
            result["aliases"] = aliases.EnumerateArray().Select(v => v.GetString().Trim()).Where(v => v.Length > 0).ToArray();
        }
        // 自定义段落：坏条目丢弃而不是整包失败（与内核归一化一致）。
        // 显式空数组必须保留，否则应用一份空段落预设时清不掉旧段落。
        if (data.TryGetProperty("customBlocks", out var customBlocks))
        {
            if (customBlocks.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("自定义段落格式无效");
            var blocks = new List<Dictionary<string, string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in customBlocks.EnumerateArray())
            {
                if (blocks.Count >= 10) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = ReadText(item, "id");
                if (!Regex.IsMatch(id, "^custom-[0-9a-f]{8}$") || !seen.Add(id)) continue;
                var text = ReadText(item, "text");
                if (text.Length == 0) continue;
                if (text.Length > 2000) text = text.Substring(0, 2000);
                var title = ReadText(item, "title");
                if (title.Length == 0) title = "自定义段落";
                if (title.Length > 40) title = title.Substring(0, 40);
                blocks.Add(new Dictionary<string, string> { ["id"] = id, ["title"] = title, ["text"] = text });
            }
            result["customBlocks"] = blocks;
        }
        var modern = data.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var schemaVersion) && schemaVersion == 1;
        var hasPrompt = result.TryGetValue("customPrompt", out var prompt) && ((string)prompt).Length > 0;
        if (!hasPrompt)
        {
            if (!modern || (!result.ContainsKey("personality") && !result.ContainsKey("personaName")))
                throw new InvalidDataException("人设需要正文或基础身份与性格");
            result["customPrompt"] = "";
        }
        result["schemaVersion"] = 1;
        return result;
    }

    internal sealed record ImportedPersona(string Name, string Id, string File);

    /// <summary>
    /// 从本地 ZIP 导入人设：写进 data/personas/user-*.json，内核下次列预设时就能看到。
    /// 旧版市场包带 plugin.json，只用来取标题；新版只认 persona.json，没有清单也能导入。
    /// </summary>
    internal static ImportedPersona Import(string file, string dataDir)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            throw new InvalidDataException("找不到人设包，请重新选择文件");
        if (new FileInfo(file).Length > PackageLimit)
            throw new InvalidDataException("人设包超过 1 MiB，请确认选择的是「打包分享」导出的 .zip 人设包");

        using var zip = ZipFile.OpenRead(file);
        if (zip.Entries.Count > EntryLimit)
            throw new InvalidDataException("人设包里的文件太多，可能不是人设包，请用「打包分享」重新导出");
        if (zip.Entries.Any(IsSymlink))
            throw new InvalidDataException("人设包不得包含符号链接，请用「打包分享」重新导出");

        var personaEntry = zip.GetEntry("persona.json")
            ?? throw new InvalidDataException("人设包里没有 persona.json，请选择本启动器「打包分享」导出的 .zip 文件");
        if (personaEntry.Length > PersonaLimit)
            throw new InvalidDataException("人设内容超过 192 KiB，请精简人设正文后再导入");

        string title = null;
        string version = "1";
        if (zip.GetEntry("plugin.json") is { } manifestEntry)
        {
            // 清单只做兼容：读不出来就当没有，不能让一份坏清单挡住有效的 persona.json。
            try
            {
                using var stream = manifestEntry.Open();
                using var manifest = JsonDocument.Parse(ReadLimited(stream, ManifestLimit));
                var root = manifest.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var kind = Str(root, "kind");
                    if (!string.IsNullOrEmpty(kind) && !string.Equals(kind, "persona", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("这个包里是" + KindLabel(kind) + "，不是人设包");
                    title = Str(root, "name");
                    var declared = Str(root, "version");
                    if (!string.IsNullOrEmpty(declared)
                        && Regex.IsMatch(declared, @"^\d+\.\d+\.\d+(\.\d+)?$") && Version.TryParse(declared, out _))
                        version = declared;
                }
            }
            catch (JsonException) { /* 旧清单坏了不影响导入 */ }
        }

        Dictionary<string, object> persona;
        try
        {
            using var stream = personaEntry.Open();
            using var document = JsonDocument.Parse(ReadLimited(stream, PersonaLimit));
            persona = Read(document.RootElement);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("人设包里的 persona.json 不是有效 JSON，请重新导出后再导入");
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(ex.Message + "，请重新导出后再导入");
        }

        var display = (title ?? (persona.TryGetValue("personaName", out var personaName) ? personaName as string : null) ?? "").Trim();
        if (display.Length == 0) display = "导入的人设";
        if (display.Length > 40) display = display.Substring(0, 40);

        var presetId = "user-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        persona["id"] = presetId;
        // 列表标题和角色名分开：name 是预设标题，personaName 才是她的名字。
        persona["name"] = display;
        persona["version"] = version;
        persona["source"] = "user";
        persona["order"] = 9000;
        persona["createdAt"] = now;
        persona["updatedAt"] = now;

        var payload = JsonSerializer.SerializeToUtf8Bytes(persona,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (payload.Length > UserPresetLimit)
            throw new InvalidDataException("人设内容超过 256 KiB，内核无法读取，请精简后再导入");

        var targetDir = Path.Combine(dataDir, "personas");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, presetId + ".json");
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, payload);
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new ImportedPersona(display, presetId, target);
    }

    internal static void Export(string destination, JsonElement data, string title, string author, string id, string version, bool promptOnly = false)
    {
        if (!Regex.IsMatch(id ?? "", "^[a-z][a-z0-9-]{0,47}$")
            || !Regex.IsMatch(version ?? "", @"^\d+\.\d+\.\d+(\.\d+)?$") || !Version.TryParse(version, out _)
            || string.IsNullOrWhiteSpace(title) || title.Length > 80 || string.IsNullOrWhiteSpace(author) || author.Length > 80)
            throw new InvalidDataException("包 ID、名称、作者或版本无效");
        Dictionary<string, object> persona;
        if (promptOnly)
        {
            if (!data.TryGetProperty("customPrompt", out var value) || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString().Trim().Length > 24000)
                throw new InvalidDataException("仅分享提示词需要非空的自定义提示词，最多 24000 字符");
            persona = new Dictionary<string, object> { ["customPrompt"] = value.GetString().Trim() };
        }
        else persona = Read(data);
        // 输出是独立 JSON 文件而非 HTML，直接 UTF-8 避免中文转义放大包体。
        var payload = JsonSerializer.SerializeToUtf8Bytes(persona, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (payload.Length > (promptOnly ? 64 : 192) * 1024)
            throw new InvalidDataException(promptOnly ? "提示词文件超过 64 KiB 上限，请精简后再分享" : "人设文件超过 192 KiB");
        var legacyMin = promptOnly ? "4.1.0" : "4.1.5";
        // 旧版启动器（≤4.1.x）只认 plugin.json + persona.json 的两文件包；
        // 保留清单是为了导出的包还能被旧版安装，新版导入只读 persona.json，不再依赖它。
        var manifest = new Dictionary<string, object>
        {
            ["id"] = id,
            ["name"] = title.Trim(),
            ["author"] = author.Trim(),
            ["version"] = version,
            ["minAppVersion"] = legacyMin,
            ["description"] = promptOnly ? "纯提示词人设" : "可编辑的人设与表达设置",
            ["api"] = 1,
            ["kind"] = "persona",
            ["appEpoch"] = 2,
            ["appCompatibility"] = new object[]
            {
                new Dictionary<string, object> { ["appEpoch"] = 1, ["minAppVersion"] = legacyMin },
                new Dictionary<string, object> { ["appEpoch"] = 2, ["minAppVersion"] = "1.0.0" },
            },
        };
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                using (var body = zip.CreateEntry("plugin.json").Open())
                    JsonSerializer.Serialize(body, manifest, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                using (var body = zip.CreateEntry("persona.json").Open()) body.Write(payload);
            }
            // 回读一遍：导出的包必须能被自己的导入流程读懂，否则宁可在本地失败。
            using (var check = ZipFile.OpenRead(temp))
            using (var stream = check.GetEntry("persona.json").Open())
            using (var document = JsonDocument.Parse(ReadLimited(stream, PersonaLimit)))
                _ = Read(document.RootElement);
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool IsSymlink(ZipArchiveEntry entry)
        => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static byte[] ReadLimited(Stream stream, int limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + count > limit)
                throw new InvalidDataException("人设包里的文件超过 " + (limit / 1024) + " KiB 上限");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static string Str(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString().Trim() : null;

    private static string KindLabel(string kind) => kind?.ToLowerInvariant() switch
    {
        "message" => "消息类扩展包",
        "tool" => "工具类扩展包",
        _ => "其他类型的扩展包",
    };
}
