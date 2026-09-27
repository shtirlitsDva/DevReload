using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Acad.Rpc.Core;

/// <summary>
/// Builds the JSON Schemas of a tool descriptor: <c>inputSchema</c> from the
/// method's parameters and <c>outputSchema</c> from its return type.
/// </summary>
/// <remarks>
/// Both schemas describe what <see cref="McpProtocol.JsonOptions"/> actually
/// reads and writes, so the mapping follows its rules: camelCase names, null
/// members omitted, enums as their member names.
/// System.Text.Json's own JsonSchemaExporter is .NET 9+; AutoCAD 2025 runs on
/// .NET 8 and its framework System.Text.Json wins in the default ALC, so the
/// exporter is not available in-process.
/// </remarks>
internal static class JsonSchemaBuilder
{
    public static JsonObject BuildInput(MethodInfo method)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var nullability = new NullabilityInfoContext();

        foreach (var p in method.GetParameters())
        {
            // Don't expose framework-injected parameters to the agent.
            if (p.ParameterType == typeof(CancellationToken)) continue;

            var paramSchema = TypeSchema(p.ParameterType, new HashSet<Type>(), nullability);
            var desc = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrEmpty(desc)) paramSchema["description"] = desc;

            properties[p.Name ?? "_"] = paramSchema;

            if (!p.HasDefaultValue) required.Add(p.Name ?? "_");
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    /// <summary>The schema of the tool's <c>structuredContent</c>, or null when
    /// the tool has none that is known before it runs: text and primitive
    /// returns carry no structuredContent, and a <see cref="ToolResult"/>'s
    /// structured payload is typed <c>object</c>.</summary>
    public static JsonObject? BuildOutput(MethodInfo method)
    {
        Type t = UnwrapTask(method.ReturnType);
        if (t == typeof(void) || t == typeof(object) || t == typeof(ToolResult) ||
            t == typeof(ToolImage) || typeof(IEnumerable<ToolImage>).IsAssignableFrom(t))
            return null;

        var schema = TypeSchema(t, new HashSet<Type>(), new NullabilityInfoContext());
        return schema["type"]?.GetValue<string>() switch
        {
            "object" => schema,
            // structuredContent must be an object; lists travel wrapped.
            "array" => new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { [McpProtocol.ListItemsProperty] = schema },
                ["required"] = new JsonArray(McpProtocol.ListItemsProperty),
            },
            _ => null,
        };
    }

    private static Type UnwrapTask(Type t)
    {
        if (t == typeof(Task) || t == typeof(ValueTask)) return typeof(void);
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(Task<>) || def == typeof(ValueTask<>)) return t.GetGenericArguments()[0];
        }
        return t;
    }

    private static JsonObject TypeSchema(Type t, HashSet<Type> inProgress, NullabilityInfoContext nullability)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;

        if (t == typeof(string) || t == typeof(char) || t == typeof(TimeSpan) || t == typeof(Uri))
            return new JsonObject { ["type"] = "string" };
        if (t == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
            t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte))
            return new JsonObject { ["type"] = "integer" };
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            return new JsonObject { ["type"] = "number" };
        if (t.IsEnum) return EnumSchema(t);
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset))
            return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        if (t == typeof(Guid)) return new JsonObject { ["type"] = "string", ["format"] = "uuid" };

        // Free-form JSON: any value.
        if (t == typeof(object) || typeof(JsonNode).IsAssignableFrom(t) || t == typeof(JsonElement))
            return new JsonObject();

        if (DictionaryValueType(t) is Type valueType)
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = TypeSchema(valueType, inProgress, nullability),
            };

        if (typeof(IEnumerable).IsAssignableFrom(t))
        {
            Type? element = t.IsArray ? t.GetElementType() : EnumerableElementType(t);
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = element == null ? new JsonObject() : TypeSchema(element, inProgress, nullability),
            };
        }

        // A type that contains itself: stop at an opaque object.
        if (!inProgress.Add(t)) return new JsonObject { ["type"] = "object" };
        try { return ObjectSchema(t, inProgress, nullability); }
        finally { inProgress.Remove(t); }
    }

    private static JsonObject ObjectSchema(Type t, HashSet<Type> inProgress, NullabilityInfoContext nullability)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || prop.GetMethod?.IsPublic != true) continue;
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;

            string name = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? McpProtocol.JsonOptions.PropertyNamingPolicy?.ConvertName(prop.Name)
                ?? prop.Name;

            properties[name] = TypeSchema(prop.PropertyType, inProgress, nullability);

            // JsonOptions omits null members, so only a member that can never be
            // null is always present. Unknown nullability (a nullable-oblivious
            // assembly) is not a promise, so it is not required either.
            bool alwaysPresent = prop.PropertyType.IsValueType
                ? Nullable.GetUnderlyingType(prop.PropertyType) == null
                : nullability.Create(prop).ReadState == NullabilityState.NotNull;
            if (alwaysPresent) required.Add(name);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    /// <summary>The member names, as JsonOptions' string-enum converter writes
    /// them. A [Flags] value is written as a comma list of names ("A, B"), which
    /// no fixed list describes, so it is only typed as a string.</summary>
    private static JsonObject EnumSchema(Type t)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (t.GetCustomAttribute<FlagsAttribute>() == null)
            schema["enum"] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode)n).ToArray());
        return schema;
    }

    private static Type? DictionaryValueType(Type t)
    {
        foreach (var i in t.GetInterfaces().Append(t))
        {
            if (!i.IsGenericType) continue;
            var def = i.GetGenericTypeDefinition();
            if ((def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>)) &&
                i.GetGenericArguments()[0] == typeof(string))
                return i.GetGenericArguments()[1];
        }
        return null;
    }

    private static Type? EnumerableElementType(Type t) =>
        t.GetInterfaces().Append(t)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
}
