using System.Text.Json;

namespace MVPWorkshop.Core.Editing;

public sealed record TestModPublication(string Revision, string Folder, string RequestPath, MvpModReport Report);
public sealed record TestModAcknowledgement(string Revision, string Status, IReadOnlyList<string> Messages);

/// <summary>Immutable generations and an atomic publication pointer; readers never observe a partially exported mod.</summary>
public static class TestModPublisher
{
    public const int Protocol = 1;
    public static bool Supported(string executable)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(executable)!, "port-manifest.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return false;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("workshopReload", out var value) && value.TryGetInt32(out var version) && version == Protocol;
        }
        catch { return false; }
    }

    public static TestModPublication Publish(EditSession session, ModInfo info, string directory, Action<string>? progress = null)
    {
        var revision = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Path.GetFullPath(directory), "generations", revision);
        var request = Path.Combine(Path.GetFullPath(directory), "reload.json");
        var report = MvpModExporter.Export(session, info, folder, asFolder: true, progress, allowEmpty: true);
        if (report.Ok)
        {
            string? previousGeneration = null;
            if (File.Exists(request))
            {
                using var previous = JsonDocument.Parse(File.ReadAllText(request));
                var relative = previous.RootElement.GetProperty("mod").GetString() ?? "";
                if (!relative.StartsWith("generations/") || relative.Length != 44 || relative[12..].Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Unsafe previous test generation.");
                previousGeneration = Path.Combine(Path.GetFullPath(directory), relative);
            }
            progress?.Invoke("Preparing live texture identities and ownership checks");
            LiveTexturePlan.Write(session, folder, previousGeneration);
            // A single rename publishes the complete generation. Old generations remain readable by the game.
            var temporary = request + "." + revision + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { format = Protocol, revision, mod = "generations/" + revision }));
            File.Move(temporary, request, overwrite: true);
        }
        return new(revision, folder, request, report);
    }

    public static TestModAcknowledgement? ReadAcknowledgement(string requestPath, string revision)
    {
        var path = Path.Combine(Path.GetDirectoryName(requestPath)!, "reload.ack.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Oversized reload acknowledgement.");
        using var json = JsonDocument.Parse(File.ReadAllText(path)); var value = json.RootElement;
        if (value.GetProperty("format").GetInt32() != Protocol) throw new InvalidDataException("Unsupported reload acknowledgement format.");
        var received = value.GetProperty("revision").GetString();
        if (received != revision) return null; // An older generation cannot acknowledge the new request.
        var status = value.GetProperty("status").GetString();
        if (status is not ("applied" or "restart-required" or "rejected")) throw new InvalidDataException("Invalid reload acknowledgement status.");
        var messages = value.TryGetProperty("messages", out var list) ? list.EnumerateArray().Select(m => m.GetString() ?? "").ToList() : [];
        return new(revision, status, messages);
    }
}
