using System.Globalization;
using System.Text.Json;

namespace APet.Framework.Plugin;

/// <summary>设置值的类型转换与校验逻辑(数字大小限制、文本长度限制、列表元素数与元素校验)。</summary>
internal static class PluginSettingBinder
{
    public static object? ConvertValue(string settingName, Type targetType, object? value, PluginSettingAttribute attribute)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
            return ConvertList(settingName, underlying.GetGenericArguments()[0], value, attribute);

        if (value is null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
                throw new PluginSettingException($"Setting '{settingName}' does not accept null.");
            return null;
        }

        return ConvertScalar(settingName, underlying, value, attribute);
    }

    private static object? ConvertScalar(string settingName, Type targetType, object? value, PluginSettingAttribute attribute)
    {
        if (value is null)
            return null;

        if (targetType == typeof(string))
        {
            var text = value as string ?? value.ToString() ?? "";
            if (text.Length < attribute.MinLength)
                throw new PluginSettingException($"Setting '{settingName}' requires at least {attribute.MinLength} characters, got {text.Length}.");
            if (text.Length > attribute.MaxLength)
                throw new PluginSettingException($"Setting '{settingName}' allows at most {attribute.MaxLength} characters, got {text.Length}.");
            if (attribute.Options is { Length: > 0 } && !attribute.Options.Contains(text, StringComparer.Ordinal))
                throw new PluginSettingException($"Setting '{settingName}' must be one of: {string.Join(", ", attribute.Options)}.");
            return text;
        }

        if (targetType.IsEnum)
        {
            if (value is string enumName)
            {
                try
                {
                    return Enum.Parse(targetType, enumName, ignoreCase: true);
                }
                catch (ArgumentException)
                {
                    throw new PluginSettingException($"Setting '{settingName}' is not a valid {DescribeType(targetType)} value: '{enumName}'.");
                }
            }
            var underlyingValue = Convert.ChangeType(value, Enum.GetUnderlyingType(targetType), CultureInfo.InvariantCulture);
            return Enum.ToObject(targetType, underlyingValue);
        }

        if (targetType == typeof(bool))
        {
            if (value is bool b)
                return b;
            var text = value.ToString()!.Trim();
            if (bool.TryParse(text, out var parsed))
                return parsed;
            if (text is "1" or "0")
                return text == "1";
            throw new PluginSettingException($"Setting '{settingName}' expects a boolean, got '{value}'.");
        }

        object converted;
        try
        {
            converted = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new PluginSettingException($"Setting '{settingName}' expects {DescribeType(targetType)}, got '{value}'.");
        }

        var number = Convert.ToDouble(converted, CultureInfo.InvariantCulture);
        if (number < attribute.Min)
            throw new PluginSettingException($"Setting '{settingName}' must be >= {attribute.Min}, got {number}.");
        if (number > attribute.Max)
            throw new PluginSettingException($"Setting '{settingName}' must be <= {attribute.Max}, got {number}.");
        return converted;
    }

    private static object ConvertList(string settingName, Type elementType, object? value, PluginSettingAttribute attribute)
    {
        var items = new List<object?>();
        if (value is null)
        {
            // null 重置为空列表
        }
        else if (value is string json)
        {
            try
            {
                if (JsonSerializer.Deserialize(json, typeof(List<>).MakeGenericType(elementType)) is System.Collections.IEnumerable parsed)
                    foreach (var item in parsed)
                        items.Add(item);
            }
            catch (JsonException ex)
            {
                throw new PluginSettingException($"Setting '{settingName}' expects a JSON array of {DescribeType(elementType)}: {ex.Message}");
            }
        }
        else if (value is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq)
                items.Add(item);
        }
        else
        {
            throw new PluginSettingException($"Setting '{settingName}' expects a list or a JSON array.");
        }

        if (items.Count < attribute.MinLength)
            throw new PluginSettingException($"Setting '{settingName}' requires at least {attribute.MinLength} items, got {items.Count}.");
        if (items.Count > attribute.MaxLength)
            throw new PluginSettingException($"Setting '{settingName}' allows at most {attribute.MaxLength} items, got {items.Count}.");

        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var item in items)
        {
            if (item is null && elementType.IsValueType && Nullable.GetUnderlyingType(elementType) is null)
                throw new PluginSettingException($"Setting '{settingName}' does not accept null items.");
            list.Add(item is null ? null : ConvertScalar(settingName, elementType, item, attribute));
        }
        return list;
    }

    public static string DescribeType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return $"List<{DescribeType(type.GetGenericArguments()[0])}>";
        if (Nullable.GetUnderlyingType(type) is { } inner)
            return DescribeType(inner) + "?";
        return type.Name switch
        {
            "Int32" => "int", "Int64" => "long", "Int16" => "short",
            "UInt32" => "uint", "UInt64" => "ulong", "UInt16" => "ushort",
            "Boolean" => "bool", "Single" => "float", "String" => "string",
            "Double" => "double", "Decimal" => "decimal", "Byte" => "byte", "SByte" => "sbyte",
            _ => type.Name,
        };
    }
}
