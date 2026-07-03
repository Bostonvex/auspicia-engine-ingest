using System.Text.Json;
using Auspicia.Engine;
using Xunit;

namespace Auspicia.Engine.Tests;

/// <summary>
/// Proves the C# checksum is byte-identical to the server. Every case in the shared reference-vector file
/// (schema/checksum-test-vectors.json) is recomputed here; the canonical string AND the checksum must match
/// exactly. If you port this client to another language, port this test too — the vectors are the contract.
/// </summary>
public class ChecksumParityTests
{
    private static readonly JsonSerializerOptions Json = AuspiciaEngineClient.Json;

    public static IEnumerable<object[]> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "checksum-test-vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return new object[]
            {
                c.GetProperty("name").GetString()!,
                c.GetProperty("run").GetRawText(),
                c.GetProperty("canonical").GetString()!,
                c.GetProperty("checksum").GetString()!,
            };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Reproduces_reference_vector(string name, string runJson, string canonical, string checksum)
    {
        var run = JsonSerializer.Deserialize<EngineRun>(runJson, Json)!;
        Assert.Equal(canonical, EngineChecksum.Canonical(run));   // exact byte string
        Assert.Equal(checksum, EngineChecksum.Compute(run));      // sha256:…
    }
}
