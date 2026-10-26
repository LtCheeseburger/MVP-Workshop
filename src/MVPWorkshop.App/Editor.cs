using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public enum EditLevel
{
    /// <summary>No editor exists for this format yet.</summary>
    None,
    /// <summary>The tool can edit it, but the result cannot be written back (e.g. it lives inside an unsupported container).</summary>
    PreviewOnly,
    /// <summary>Edits are saved into the project and rebuilt into game archives on export.</summary>
    Savable,
}

public sealed record EditCapability(EditLevel Level, string Summary, string? Reason);

/// <summary>The active editing session plus the rules for what can be edited and saved.</summary>
public static class Editor
{
    public static EditSession? Session { get; private set; }

    /// <summary>Raised on the UI thread whenever the session is replaced or any change, undo, redo or save happens.</summary>
    public static event Action? Changed;

    public static void Attach(EditSession session)
    {
        if (Session is not null) Session.Changed -= Raise;
        Session = session;
        session.Changed += Raise;
        Raise();
    }

    private static void Raise() => Changed?.Invoke();

    public static bool IsDirty => Session?.IsDirty == true;

    /// <summary>Formats the tool has an editor for.</summary>
    public static bool HasEditor(AssetNode n) => !n.IsFolder && n.Format is FormatId.SshTexture or FormatId.AptMovie or FormatId.AptConst or FormatId.LocBinary or FormatId.Text or FormatId.LocText or FormatId.Csv;

    public static EditCapability CapabilityOf(AssetNode n)
    {
        if (Session is not { } s) return new(EditLevel.None, "No game loaded", null);
        if (!HasEditor(n)) return new(EditLevel.None, "NO EDITOR", "This format is view-only for now.");
        if (s.SaveBlocker(n) is { } why) return new(EditLevel.PreviewOnly, "PREVIEW ONLY", why);
        var disk = ModExporter.DiskFileOf(n);
        return new(EditLevel.Savable, "EDITABLE", $"Saved changes are written into {ModExporter.RelativePath(s.GameRoot, disk)} in the mod output folder.");
    }

    /// <summary>
    /// Apply new bytes for an asset. When the asset can be written back this is a saved change; otherwise the new contents are shown in
    /// the viewers only. Either way it goes through the one undo history.
    /// </summary>
    public static Change Apply(AssetNode node, byte[] bytes, string description)
    {
        var s = Session ?? throw new InvalidOperationException("Open a game folder first.");
        return s.SaveBlocker(node) is { } why ? s.CommitPreviewBytes(node, bytes, description, why) : s.Commit(node, bytes, description);
    }

    /// <summary>The bytes viewers should currently show for a node (edits included).</summary>
    public static byte[] Current(AssetNode node) => node.ReadAll();
}
