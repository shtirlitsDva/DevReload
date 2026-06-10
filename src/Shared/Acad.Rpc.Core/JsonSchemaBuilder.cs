using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;

namespace Acad.Rpc.Core;

/// <summary>
/// Builds a JSON-Schema-shaped JsonObject from a method's parameters.
/// The schema goes into the tool descriptor's inputSchema property and
/// is what the agent sees when introspecting tools/list.
/// </summary>
internal static class JsonSchemaBuilder
{
    public static JsonObject Build(MethodInfo method)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in method.GetParameters())
        {
            // Don't expose framework-injected parameters to the agent.
            if (p.ParameterType == typeof(CancellationToken)) continue;

            var paramSchema = MapType(p.ParameterType);
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

    private static JsonObject MapType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;

        if (t == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (t == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (t == typeof(int) || t == typeof(long) || t == typeof(short))
            return new JsonObject { ["type"] = "integer" };
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            return new JsonObject { ["type"] = "number" };
        if (t.IsEnum) return new JsonObject { ["type"] = "string" };

        // Fallback — anything else is opaque. The agent passes a JSON
        // object and we attempt JSON deserialization at call time.
        return new JsonObject { ["type"] = "object" };
    }
}
