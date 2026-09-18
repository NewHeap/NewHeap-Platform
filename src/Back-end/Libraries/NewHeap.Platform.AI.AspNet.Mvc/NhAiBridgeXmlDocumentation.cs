using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// Reads method summaries from the XML documentation file next to an assembly. A missing or
/// unreadable file is skipped silently: XML documentation is an optional description source.
/// </summary>
internal sealed class NhAiBridgeXmlDocumentation
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, string>> _summaries = new();

    public string? GetSummary(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var summaries = _summaries.GetOrAdd(method.Module.Assembly, Load);
        if (summaries.Count == 0)
        {
            return null;
        }

        var memberId = GetMemberId(method);
        if (summaries.TryGetValue(memberId, out var summary))
        {
            return summary;
        }

        // Fall back to a prefix match only when the method has no overloads, so an overload
        // never borrows another overload's summary.
        var overloads = method.DeclaringType!
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Count(candidate => string.Equals(candidate.Name, method.Name, StringComparison.Ordinal));
        if (overloads != 1)
        {
            return null;
        }

        var prefix = "M:" + GetTypeName(method.DeclaringType!) + "." + method.Name;
        var candidates = summaries
            .Where(pair => string.Equals(pair.Key, prefix, StringComparison.Ordinal)
                || pair.Key.StartsWith(prefix + "(", StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return candidates.Length == 1 ? candidates[0].Value : null;
    }

    private static IReadOnlyDictionary<string, string> Load(Assembly assembly)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(assembly.Location))
        {
            return result;
        }

        var path = Path.ChangeExtension(assembly.Location, ".xml");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var document = XDocument.Load(path);
            foreach (var member in document.Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                var summary = member.Element("summary");
                if (name is null || summary is null)
                {
                    continue;
                }

                var text = Whitespace.Replace(RenderText(summary), " ").Trim();
                if (text.Length > 0)
                {
                    result[name] = text;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or XmlException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return result;
    }

    private static string RenderText(XElement element)
    {
        var builder = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                builder.Append(text.Value);
            }
            else if (node is XElement child)
            {
                var reference = child.Attribute("cref")?.Value ?? child.Attribute("langword")?.Value;
                if (reference is not null)
                {
                    builder.Append(reference[(reference.IndexOf(':') + 1)..].Split('.').Last());
                }
                else
                {
                    builder.Append(RenderText(child));
                }
            }
        }
        return builder.ToString();
    }

    private static string GetMemberId(MethodInfo method)
    {
        var builder = new StringBuilder("M:");
        builder.Append(GetTypeName(method.DeclaringType!)).Append('.').Append(method.Name);
        if (method.IsGenericMethod)
        {
            builder.Append("``").Append(method.GetGenericArguments().Length);
        }

        var parameters = method.GetParameters();
        if (parameters.Length > 0)
        {
            builder.Append('(')
                .Append(string.Join(",", parameters.Select(parameter => GetParameterTypeName(parameter.ParameterType))))
                .Append(')');
        }
        return builder.ToString();
    }

    private static string GetTypeName(Type type)
    {
        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    private static string GetParameterTypeName(Type type)
    {
        if (type.IsByRef)
        {
            return GetParameterTypeName(type.GetElementType()!) + "@";
        }
        if (type.IsArray)
        {
            return GetParameterTypeName(type.GetElementType()!) + "[]";
        }
        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod is null ? "`" : "``") + type.GenericParameterPosition;
        }
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var name = (definition.FullName ?? definition.Name).Replace('+', '.');
            var tick = name.IndexOf('`');
            if (tick >= 0)
            {
                name = name[..tick];
            }
            return name + "{" + string.Join(",", type.GetGenericArguments().Select(GetParameterTypeName)) + "}";
        }
        return GetTypeName(type);
    }
}
