using System.Reflection;
using Jacobi.BinarySerializer.Schema.Json;
using Jacobi.BinarySerializer.Schema.Xml;

namespace Jacobi.BinarySerializer.Schema;

public sealed class SchemaSet
{
    private readonly Dictionary<string, SchemaDocument> _documents = new();

    public IReadOnlyCollection<SchemaDocument> Documents
        => _documents.Values;

    public SchemaDocument LoadFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var content = File.ReadAllText(path);
        var document = extension switch
        {
            ".xml" => LoadFromXml(content),
            ".json" => LoadFromJson(content),
            ".yaml" or ".yml" => LoadFromYaml(content),
            _ => throw new NotSupportedException($"File extension '{extension}' is not supported.")
        };

        ResolveReferences(document);
        _documents[document.Name] = document;
        return document;
    }

    public IEnumerable<SchemaDocument> LoadAssembly(string path, string resourcePath)
    {
        var documents = new List<SchemaDocument>();

        var assembly = Assembly.LoadFile(path);
        assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePath, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .ForEach(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Resource '{name}' not found in assembly '{path}'.");
                var document = LoadFromBinary(stream);

                documents.Add(document);
                _documents[document.Name] = document;
            });

        foreach (var document in documents)
        {
            ResolveReferences(document);
        }
        return documents;
    }

    public SchemaDocument LoadFromXml(string xml)
        => XmlSerializer.Deserialize(xml);

    public SchemaDocument LoadFromJson(string json)
        => JsonSerializer.Deserialize(json);

    public SchemaDocument LoadFromYaml(string yaml)
    {
        throw new NotImplementedException();
    }

    public SchemaDocument LoadFromBinary(Stream data)
    {
        throw new NotImplementedException();
    }

    private void ResolveReferences(SchemaDocument document)
    {
        foreach (var reference in document.Includes)
        {
            if (_documents.TryGetValue(reference.Schema, out var includedDocument))
            {
                reference.SchemaDocument = includedDocument;
            }
        }

        // Resolve references for groups, fields and codecs
        foreach (var group in document.Groups)
        {
            foreach (var field in group.Children)
            {

            }
        }
    }
}