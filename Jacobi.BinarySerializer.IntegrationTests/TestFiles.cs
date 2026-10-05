namespace Jacobi.BinarySerializer.IntegrationTests;

/// <summary>
/// Resolves files that live next to the tests (schemas, sample data) in the test output directory.
/// Paths are relative to the project root, for example <c>Png/png.json</c>.
/// </summary>
internal static class TestFiles
{
    public static string Path(string relativePath)
        => System.IO.Path.Combine(AppContext.BaseDirectory, relativePath);

    public static string ReadText(string relativePath)
        => File.ReadAllText(Path(relativePath));

    public static byte[] ReadBytes(string relativePath)
        => File.ReadAllBytes(Path(relativePath));
}
