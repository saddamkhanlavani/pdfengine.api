using System;
using System.Collections.Generic;
using System.Text.Json;
using Scriban;
using Scriban.Runtime;

namespace PdfEngine.Application.Common;

/// <summary>
/// Renders an HTML template (Scriban syntax: {{ name }}, {{ for x in items }}...{{ end }},
/// {{ if cond }}...{{ end }}) against a JSON data object — the same shape of feature
/// APITemplate.io built its whole product around, so a caller can keep one reusable
/// invoice/report template and supply just the data per request.
/// </summary>
public static class HtmlTemplateRenderer
{
    public static (bool Success, string Result, string? Error) Render(string htmlTemplate, JsonElement? data)
    {
        var template = Template.Parse(htmlTemplate);
        if (template.HasErrors)
        {
            var messages = string.Join("; ", template.Messages);
            return (false, htmlTemplate, $"Template syntax error: {messages}");
        }

        var scriptObject = new ScriptObject();
        if (data.HasValue)
        {
            var converted = ConvertJsonElement(data.Value);
            if (converted is IDictionary<string, object?> dict)
            {
                foreach (var kvp in dict)
                {
                    scriptObject.SetValue(kvp.Key, kvp.Value, readOnly: false);
                }
            }
        }

        var context = new TemplateContext();
        context.PushGlobal(scriptObject);

        try
        {
            var rendered = template.Render(context);
            return (true, rendered, null);
        }
        catch (Exception ex)
        {
            return (false, htmlTemplate, $"Template render error: {ex.Message}");
        }
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                {
                    dict[prop.Name] = ConvertJsonElement(prop.Value);
                }
                return dict;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(ConvertJsonElement(item));
                }
                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }
}
