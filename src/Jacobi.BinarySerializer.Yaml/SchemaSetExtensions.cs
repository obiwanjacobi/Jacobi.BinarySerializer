using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Yaml;

public static class SchemaSetExtensions
{
    public static SchemaDocument LoadYaml(this SchemaSet schemaSet, string yaml)
    {
        ArgumentNullException.ThrowIfNull(schemaSet);
        return schemaSet.LoadFromJson(YamlSerializer.YamlToJson(yaml));
    }
}
