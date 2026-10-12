using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Yaml;

public static class SchemaSetBuilderExtensions
{
    public static SchemaDocument LoadYaml(this SchemaSetBuilder schemaSet, string yaml)
    {
        ArgumentNullException.ThrowIfNull(schemaSet);
        return schemaSet.LoadFromJson(YamlSerializer.YamlToJson(yaml));
    }
}

