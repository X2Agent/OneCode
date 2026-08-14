namespace OneCode.Core.Keybindings;

/// <summary>
/// 验证用户配置文件，检查语法错误、重复绑定、保留快捷键、无效上下文/动作等。
/// </summary>
public static class KeybindingValidator
{
    /// <summary>
    /// 验证单个按键字符串并返回解析错误。
    /// </summary>
    public static KeybindingWarning? ValidateKeystroke(string keystroke, string? contextName = null)
    {
        var parts = keystroke.ToLowerInvariant().Split('+');

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return new KeybindingWarning(
                    KeybindingWarningType.ParseError,
                    KeybindingSeverity.Error,
                    $"Empty key part in \"{keystroke}\"",
                    Key: keystroke,
                    Context: contextName,
                    Suggestion: "Remove extra \"+\" characters");
            }
        }

        var parsed = KeybindingParser.ParseKeystroke(keystroke);
        if (string.IsNullOrEmpty(parsed.Key) &&
            !parsed.Ctrl && !parsed.Alt && !parsed.Shift && !parsed.Meta && !parsed.Super)
        {
            return new KeybindingWarning(
                KeybindingWarningType.ParseError,
                KeybindingSeverity.Error,
                $"Could not parse keystroke \"{keystroke}\"",
                Key: keystroke,
                Context: contextName);
        }

        return null;
    }

    /// <summary>
    /// 验证一个绑定块。
    /// </summary>
    public static List<KeybindingWarning> ValidateBlock(KeybindingBlock block)
    {
        var warnings = new List<KeybindingWarning>();
        var contextName = block.Context;

        if (!KeybindingDefaults.IsValidContext(contextName))
        {
            warnings.Add(new KeybindingWarning(
                KeybindingWarningType.InvalidContext,
                KeybindingSeverity.Error,
                $"Unknown context \"{contextName}\"",
                Context: contextName,
                Suggestion: $"Valid contexts: {string.Join(", ", KeybindingDefaults.AllContexts)}"));
        }

        foreach (var (key, action) in block.Bindings)
        {
            var keyError = ValidateKeystroke(key, contextName);
            if (keyError is not null)
            {
                warnings.Add(keyError);
            }

            if (action is not null)
            {
                if (!KeybindingDefaults.IsValidAction(action))
                {
                    if (action.StartsWith("command:", StringComparison.Ordinal))
                    {
                        if (!IsValidCommandBinding(action))
                        {
                            warnings.Add(new KeybindingWarning(
                                KeybindingWarningType.InvalidAction,
                                KeybindingSeverity.Warning,
                                $"Invalid command binding \"{action}\" for \"{key}\": command name may only contain alphanumeric characters, colons, hyphens, and underscores",
                                Key: key,
                                Context: contextName,
                                Action: action));
                        }

                        if (contextName != KeybindingDefaults.ContextChat)
                        {
                            warnings.Add(new KeybindingWarning(
                                KeybindingWarningType.InvalidAction,
                                KeybindingSeverity.Warning,
                                $"Command binding \"{action}\" must be in \"Chat\" context, not \"{contextName}\"",
                                Key: key,
                                Context: contextName,
                                Action: action,
                                Suggestion: "Move this binding to a block with \"context\": \"Chat\""));
                        }
                    }
                    else
                    {
                        warnings.Add(new KeybindingWarning(
                            KeybindingWarningType.InvalidAction,
                            KeybindingSeverity.Warning,
                            $"Unknown action \"{action}\" for \"{key}\"",
                            Key: key,
                            Context: contextName,
                            Action: action,
                            Suggestion: $"Valid actions: {string.Join(", ", KeybindingDefaults.AllActions.Take(10))}..."));
                    }
                }
            }
        }

        return warnings;
    }

    /// <summary>
    /// 检查同一上下文中的重复绑定。
    /// 仅检查用户绑定（非默认+用户合并后的）。
    /// </summary>
    public static List<KeybindingWarning> CheckDuplicates(IEnumerable<KeybindingBlock> blocks)
    {
        var warnings = new List<KeybindingWarning>();
        var seenByContext = new Dictionary<string, Dictionary<string, string?>>();

        foreach (var block in blocks)
        {
            if (!seenByContext.TryGetValue(block.Context, out var contextMap))
            {
                contextMap = new Dictionary<string, string?>();
                seenByContext[block.Context] = contextMap;
            }

            foreach (var (key, action) in block.Bindings)
            {
                var normalizedKey = KeybindingParser.NormalizeKeyForComparison(key);

                if (contextMap.TryGetValue(normalizedKey, out var existingAction))
                {
                    var existingDisplay = existingAction ?? "null (unbind)";
                    var newDisplay = action ?? "null (unbind)";

                    if (existingAction != action)
                    {
                        warnings.Add(new KeybindingWarning(
                            KeybindingWarningType.Duplicate,
                            KeybindingSeverity.Warning,
                            $"Duplicate binding \"{key}\" in {block.Context} context",
                            Key: key,
                            Context: block.Context,
                            Action: newDisplay,
                            Suggestion: $"Previously bound to \"{existingDisplay}\". Only the last binding will be used."));
                    }
                }

                contextMap[normalizedKey] = action;
            }
        }

        return warnings;
    }

    /// <summary>
    /// 检查 JSON 字符串中同一绑定块内的重复键。
    /// System.Text.Json 会静默使用最后一个值，需要检查原始字符串来警告用户。
    ///
    /// <para>实现说明：使用 <see cref="Utf8JsonReader"/> 逐 token 遍历 JSON，正确处理转义、嵌套，
    /// 且无 ReDoS 风险。跟踪深度定位 <c>bindings</c> 数组中的每个对象的 <c>bindings</c> 子对象，
    /// 在该子对象内检测重复的属性名。</para>
    /// </summary>
    public static List<KeybindingWarning> CheckDuplicateKeysInJson(string jsonString)
    {
        var warnings = new List<KeybindingWarning>();
        if (string.IsNullOrWhiteSpace(jsonString))
            return warnings;

        try
        {
            var reader = new Utf8JsonReader(
                System.Text.Encoding.UTF8.GetBytes(jsonString),
                new JsonReaderOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

            // 状态机：跟踪深度和位置以定位 "bindings" 数组内每个块对象的 "bindings" 子对象
            //
            // JSON 结构与 CurrentDepth 值（参考微软文档：StartObject/StartArray 返回进入后的深度，
            // EndObject/EndArray 返回退出后的深度，PropertyName/String 返回当前容器的深度）：
            //
            // {                              StartObject  CurrentDepth=0 (根对象)
            //   "bindings": [                PropertyName CurrentDepth=0
            //                                StartArray   CurrentDepth=1
            //     {                          StartObject  CurrentDepth=1 (block 对象)
            //       "context": "Chat"        PropertyName CurrentDepth=1 ← 检测 context
            //                                String       CurrentDepth=1
            //       "bindings": {            PropertyName CurrentDepth=1 ← 检测 block_bindings
            //                                StartObject  CurrentDepth=2 (bindings 子对象) ← 开始跟踪重复键
            //         "ctrl+k": "action"     PropertyName CurrentDepth=2 ← 记录 key
            //         "ctrl+k": "action2"    PropertyName CurrentDepth=2 ← 重复！
            //       }                         EndObject    CurrentDepth=1 (退出后) ← 清除跟踪
            //     }                           EndObject    CurrentDepth=0 (退出后)
            //   ]                             EndArray     CurrentDepth=0 (退出后)
            // }                              EndObject    CurrentDepth=0 (退出后)
            //
            // 深度语义：
            // - block 对象层级的属性（"context"/"bindings"）CurrentDepth=1
            // - bindings 子对象的 StartObject CurrentDepth=2，存入 blockBindingsDepth
            // - bindings 子对象内的属性 CurrentDepth=2 == blockBindingsDepth
            // - 退出 bindings 子对象时 EndObject CurrentDepth=1 == blockBindingsDepth-1

            string? currentContext = null;
            string? pendingPropertyName = null;  // 等待值的属性名（用于读取 context 值）
            var seenKeysInCurrentBindings = new Dictionary<string, int>(StringComparer.Ordinal);

            // 跟踪我们是否在某个 block 对象的 "bindings" 子对象内
            // blockBindingsDepth 记录该子对象 StartObject 的 CurrentDepth 值
            int? blockBindingsDepth = null;

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        // 进入新对象：如果是 block 的 bindings 子对象，开始跟踪重复键
                        if (pendingPropertyName == "block_bindings")
                        {
                            blockBindingsDepth = reader.CurrentDepth;  // 此 StartObject token 的深度
                            seenKeysInCurrentBindings.Clear();
                        }
                        pendingPropertyName = null;
                        break;

                    case JsonTokenType.EndObject:
                        // 退出对象：如果正在跟踪 block bindings 子对象且退出的是它，清空状态
                        // EndObject 的 CurrentDepth 是退出后的值，所以 == blockBindingsDepth - 1
                        if (blockBindingsDepth.HasValue && reader.CurrentDepth == blockBindingsDepth.Value - 1)
                        {
                            blockBindingsDepth = null;
                            seenKeysInCurrentBindings.Clear();
                        }
                        pendingPropertyName = null;
                        break;

                    case JsonTokenType.StartArray:
                        pendingPropertyName = null;
                        break;

                    case JsonTokenType.EndArray:
                        // 退出顶层数组：重置 context
                        currentContext = null;
                        pendingPropertyName = null;
                        break;

                    case JsonTokenType.PropertyName:
                        var propName = reader.GetString();
                        var propDepth = reader.CurrentDepth;  // 属性名所在容器的深度

                        // 在 block 对象层级（CurrentDepth==1，即 root > array > block object）检测 "context" 和 "bindings"
                        if (propDepth == 1 && propName == "context")
                        {
                            pendingPropertyName = "context";
                        }
                        else if (propDepth == 1 && propName == "bindings")
                        {
                            // 下一个 token 应该是 StartObject（内层 bindings 子对象）
                            // 标记：等待进入该对象后开始跟踪
                            pendingPropertyName = "block_bindings";
                        }
                        else if (blockBindingsDepth.HasValue && propDepth == blockBindingsDepth.Value)
                        {
                            // 在 block 的 bindings 子对象内（属性 CurrentDepth == blockBindingsDepth）：检测重复键
                            // propName 可能是 null（畸形 JSON），Utf8JsonReader 会抛异常，这里安全处理
                            if (propName is not null)
                            {
                                seenKeysInCurrentBindings.TryGetValue(propName, out var count);
                                count++;
                                seenKeysInCurrentBindings[propName] = count;

                                if (count == 2)
                                {
                                    warnings.Add(new KeybindingWarning(
                                        KeybindingWarningType.Duplicate,
                                        KeybindingSeverity.Warning,
                                        $"Duplicate key \"{propName}\" in {currentContext ?? "unknown"} bindings",
                                        Key: propName,
                                        Context: currentContext,
                                        Suggestion: "This key appears multiple times in the same context. JSON uses the last value, earlier values are ignored."));
                                }
                            }
                        }
                        else
                        {
                            pendingPropertyName = null;
                        }
                        break;

                    case JsonTokenType.String:
                        if (pendingPropertyName == "context")
                        {
                            currentContext = reader.GetString();
                        }
                        pendingPropertyName = null;
                        break;

                    default:
                        pendingPropertyName = null;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // JSON 语法错误——忽略，其他验证器会报语法问题。
            // 此方法只负责重复键检测，畸形 JSON 由 JsonSerializer.Deserialize 在其他路径报告。
        }

        return warnings;
    }

    /// <summary>
    /// 验证 command:* 绑定的格式：以 "command:" 开头，后跟字母、数字、冒号、连字符或下划线。
    /// 等价于正则 ^command:[a-zA-Z0-9:\-_]+$，但用字符遍历实现以彻底消除该文件对正则的依赖。
    /// </summary>
    private static bool IsValidCommandBinding(string action)
    {
        const string Prefix = "command:";
        if (!action.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var rest = action.AsSpan(Prefix.Length);
        if (rest.Length == 0)
            return false;

        foreach (var ch in rest)
        {
            var isAllowed =
                (ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9') ||
                ch == ':' || ch == '-' || ch == '_';
            if (!isAllowed)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 检查保留快捷键。
    /// </summary>
    public static List<KeybindingWarning> CheckReservedShortcuts(IEnumerable<KeybindingEntry> bindings)
    {
        var warnings = new List<KeybindingWarning>();
        var reserved = KeybindingDefaults.GetReservedShortcuts();

        foreach (var binding in bindings)
        {
            var keyDisplay = KeybindingParser.ChordToString(binding.Chord);
            var normalizedKey = KeybindingParser.NormalizeKeyForComparison(keyDisplay);

            foreach (var res in reserved)
            {
                if (KeybindingParser.NormalizeKeyForComparison(res.Key) == normalizedKey)
                {
                    warnings.Add(new KeybindingWarning(
                        KeybindingWarningType.Reserved,
                        res.Severity,
                        $"\"{keyDisplay}\" may not work: {res.Reason}",
                        Key: keyDisplay,
                        Context: binding.Context,
                        Action: binding.Action));
                }
            }
        }

        return warnings;
    }

    /// <summary>
    /// 验证用户配置块结构。
    /// </summary>
    public static List<KeybindingWarning> ValidateUserBlocks(IEnumerable<KeybindingBlock> userBlocks)
    {
        var warnings = new List<KeybindingWarning>();
        var blockList = userBlocks.ToList();

        for (var i = 0; i < blockList.Count; i++)
        {
            warnings.AddRange(ValidateBlock(blockList[i]));
        }

        return warnings;
    }

    /// <summary>
    /// 运行所有验证并返回合并的警告列表。
    /// </summary>
    public static List<KeybindingWarning> ValidateBindings(
        IEnumerable<KeybindingBlock> userBlocks)
    {
        var warnings = new List<KeybindingWarning>();
        var userBlockList = userBlocks.ToList();

        // 验证用户配置结构
        warnings.AddRange(ValidateUserBlocks(userBlockList));

        warnings.AddRange(CheckDuplicates(userBlockList));

        var userBindings = KeybindingParser.ParseBindings(userBlockList);
        warnings.AddRange(CheckReservedShortcuts(userBindings));

        // 去重
        var seen = new HashSet<string>();
        return warnings.Where(w =>
        {
            var key = $"{w.Type}:{w.Key}:{w.Context}";
            if (seen.Contains(key)) return false;
            seen.Add(key);
            return true;
        }).ToList();
    }

    /// <summary>
    /// 格式化单个警告用于显示。
    /// </summary>
    public static string FormatWarning(KeybindingWarning warning)
    {
        var icon = warning.Severity == KeybindingSeverity.Error ? "\u2717" : "\u26A0";
        var msg = $"{icon} Keybinding {warning.Severity.ToString().ToLowerInvariant()}: {warning.Message}";

        if (warning.Suggestion is not null)
        {
            msg += $"\n  {warning.Suggestion}";
        }

        return msg;
    }

    /// <summary>
    /// 格式化多个警告用于显示。
    /// </summary>
    public static string FormatWarnings(IEnumerable<KeybindingWarning> warnings)
    {
        var warningList = warnings.ToList();
        if (warningList.Count == 0) return string.Empty;

        var errors = warningList.Where(w => w.Severity == KeybindingSeverity.Error).ToList();
        var warns = warningList.Where(w => w.Severity == KeybindingSeverity.Warning).ToList();

        var lines = new List<string>();

        if (errors.Count > 0)
        {
            lines.Add($"Found {errors.Count} keybinding {(errors.Count == 1 ? "error" : "errors")}:");
            lines.AddRange(errors.Select(FormatWarning));
        }

        if (warns.Count > 0)
        {
            if (lines.Count > 0) lines.Add(string.Empty);
            lines.Add($"Found {warns.Count} keybinding {(warns.Count == 1 ? "warning" : "warnings")}:");
            lines.AddRange(warns.Select(FormatWarning));
        }

        return string.Join("\n", lines);
    }
}
