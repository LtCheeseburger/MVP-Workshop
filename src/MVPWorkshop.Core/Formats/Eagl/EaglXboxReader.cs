using System.Numerics;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats.Eagl;

/// <summary>
/// Reads EAGL model objects as shipped on Xbox (MVP 06). The ELF container, <c>__Model</c>/<c>__Skeleton</c>/<c>__Bone</c> symbols and shape
/// names are the same as on PS2, but geometry is not VIF: every draw is a render method (<c>__RM</c>) that lists NV2A vertex streams
/// (<c>__VB</c>, one {byte size, stride, pointer} record each) and an optional 16-bit index buffer for a triangle strip.
/// </summary>
/// <remarks>
/// Layout as decoded (Xbox objects only):
/// <list type="bullet">
/// <item><c>__VB:::__GPvb_{slot}_{rm}[_{variation}]_{model}</c>: u32 next, u32 byteSize, u32 stride, u32 1, then the data pointer (+0x14).</item>
/// <item>An RM's stream table is a run of {pointer, vertexCount} pairs: position (12), UV (8), optional second UV (8), colour (4), normal (12) or packed normal (4), skin entry (2, listed with count 0).</item>
    /// <item>Skin: the 2-byte stream selects a 16-byte {w0, w1, w2, packed bones} record (see <c>AddDraw</c>); there is no palette, bone indices are skeleton indices.</item>
/// <item>After the <c>EAGLAnimationBuffer</c> constant slot comes {pointer, indexCount} to the index buffer header and {pointer, …} to its u16 data (header + 0x20).</item>
/// </list>
/// </remarks>
public static class EaglXboxReader
{
    public static bool LooksLikeXbox(RelocatedObject o) =>
        o.Symbols.Any(s => s.Name.StartsWith("__RM:::", StringComparison.Ordinal)) && o.Symbols.Any(s => s.Name.StartsWith("__VB:::", StringComparison.Ordinal));

    private sealed record VertexBuffer(string Name, int Header, int Size, int Stride, int Data)
    {
        public int Count => Stride == 0 ? 0 : Size / Stride;
    }

    public static EaglFile Read(RelocatedObject o, int variant = 0)
    {
        var file = new EaglFile { Object = o };
        foreach (var r in o.Relocs.Values)
            if (r.External is { } ext && ext.Contains("SHAPENAME=", StringComparison.Ordinal))
            {
                var t = EaglReader.TextureName(ext);
                if (!file.Textures.Contains(t)) file.Textures.Add(t);
            }
        foreach (var s in o.Symbols.OrderBy(s => s.Value))
        {
            if (s.Name.StartsWith("__Bone:::", StringComparison.Ordinal)) file.Bones.Add((s.Name[9..], (int)o.U32(s.Value)));
            else if (s.Name.StartsWith("__Skeleton:::", StringComparison.Ordinal)) { file.SkeletonName = s.Name[13..]; file.SkeletonBoneCount = (int)o.U32(s.Value + 8); }
        }
        try { file.Skeleton = EaglSkeleton.Read(o, file.Bones.GroupBy(b => b.Index).ToDictionary(g => g.Key, g => g.First().Name.Split('.').Last())); }
        catch { /* the skeleton block is optional for viewing geometry */ }

        var buffers = new Dictionary<string, VertexBuffer>(StringComparer.Ordinal);
        var byData = new Dictionary<int, VertexBuffer>();
        foreach (var s in o.Symbols.Where(s => s.Name.StartsWith("__VB:::", StringComparison.Ordinal)))
        {
            var size = (int)o.U32(s.Value + 4); var stride = (int)o.U32(s.Value + 8);
            var data = o.Ptr(s.Value + 16) ?? s.Value + 20;
            if (stride <= 0 || size <= 0 || data + size > o.Data.Length) continue;
            var vb = new VertexBuffer(s.Name[7..], s.Value, size, stride, data);
            buffers[vb.Name] = vb; byData[data] = vb;
        }

        var modelSymbols = o.Symbols.Where(s => s.Name.StartsWith("__Model:::", StringComparison.Ordinal)).OrderBy(s => s.Value).ToList();
        var baseSymbol = modelSymbols.FirstOrDefault(s => !s.Name.Contains(".variation", StringComparison.Ordinal));
        if (baseSymbol.Name is null) baseSymbol = modelSymbols[0];
        var modelName = baseSymbol.Name[10..];
        var mesh = new EaglMesh();
        var parts = new List<EaglPart>();
        var renderMethods = o.Symbols.Where(s => s.Name.StartsWith("__RM:::", StringComparison.Ordinal)).OrderBy(s => s.Value).ToList();
        var lastTable = -1;
        var anim = o.Relocs.Where(r => r.Value.External?.EndsWith("EAGLAnimationBuffer", StringComparison.Ordinal) == true).Select(r => r.Key).OrderBy(k => k).ToList();
        for (var k = 0; k < renderMethods.Count; k++)
        {
            var rm = renderMethods[k].Value;
            var limit = Math.Min(o.Data.Length, rm + 0x180);
            if (k + 1 < renderMethods.Count) limit = Math.Min(limit, renderMethods[k + 1].Value);
            try { AddDraw(o, parts, mesh, k, rm, limit, anim, byData, buffers, modelName, variant, ref lastTable); }
            catch (Exception ex) { mesh.Warnings.Add($"{renderMethods[k].Name}: {ex.Message}"); }
        }
        if (mesh.Draws.Count == 0) mesh.Warnings.Add("No renderable vertex streams were found in this Xbox object.");
        var (min, max) = mesh.Bounds();
        var model = new EaglModel
        {
            Name = modelName, Offset = baseSymbol.Value, GeometryKey = baseSymbol.Value, Mesh = mesh, BoundsMin = min, BoundsMax = max,
            Sphere = new Vector4((min + max) / 2, Vector3.Distance(min, max) / 2),
        };
        model.Variations.AddRange(modelSymbols.Where(s => s.Name != baseSymbol.Name).Select(s => s.Name[10..]));
        model.VariantCount = 1 + model.Variations.Count;
        model.Parts.AddRange(parts);
        file.Models.Add(model);
        return file;
    }

    private static bool IsWeightRecord(RelocatedObject o, int at)
    {
        if (at + 16 > o.Data.Length) return false;
        float w0 = o.F32(at), w1 = o.F32(at + 4), w2 = o.F32(at + 8), packed = o.F32(at + 12);
        if (!float.IsFinite(w0 + w1 + w2 + packed) || w0 < 0 || w1 < 0 || w2 < 0 || w0 + w1 + w2 is < 0.98f or > 1.02f) return false;
        return packed is >= 16777216f and < 2147483648f;
    }

    private static void AddDraw(RelocatedObject o, List<EaglPart> parts, EaglMesh mesh, int index, int rm, int limit, List<int> anim,
        Dictionary<int, VertexBuffer> byData, Dictionary<string, VertexBuffer> buffers, string modelName, int variant, ref int lastTable)
    {
        string? texture = null, textureSymbol = null, state = null;
        for (var at = rm; at < limit; at += 4)
        {
            var ext = o.External(at);
            if (ext is null) continue;
            if (texture is null && ext.Contains("SHAPENAME=", StringComparison.Ordinal)) { texture = EaglReader.TextureName(ext); textureSymbol = ext; }
            else if (state is null && ext.StartsWith("__EAGL::GeoPrimState:::", StringComparison.Ordinal)) state = ext;
        }

        // Most RMs inherit their GeoPrimState from a runtime parent RM. Cut-out textures (DXT1 punch-through, foliage, fences) still rely on an alpha test there.
        state ??= "__EAGL::GeoPrimState:::XBOX_INFERRED_ALPHA_TEST;;SetTransparencyMethod=EAGL::TM_OPAQUE;SetAlphaTestEnable=true;SetAlphaCompareValue=127;SetAlphaBlendMode=EAGL::ABM_OFF;SetPrimitiveType=EAGL::PT_TRIANGLESTRIP;";

        // Stream table: consecutive {pointer, count} pairs whose pointer is a vertex buffer.
        var streams = new List<VertexBuffer>();
        var first = -1; var streamEnd = rm;
        for (var at = rm; at + 8 <= limit; at += 4)
            if (o.Ptr(at) is { } t && byData.ContainsKey(t) && (at - rm) % 4 == 0 && o.U32(at + 4) == byData[t].Count) { first = at; break; }
        if (first < 0) return;
        for (var at = first; at + 8 <= o.Data.Length && o.Ptr(at) is { } t && byData.TryGetValue(t, out var vb); at += 8)
        {
            if (o.U32(at + 4) != vb.Count && streams.Count > 0 && !(vb.Stride == 2 && o.U32(at + 4) == 0)) break;
            streams.Add(vb); streamEnd = at + 8;
        }
        // Compressed objects (flags) store positions as 3×u16 normalised into the __BBOX extents.
        var pos = streams.FirstOrDefault(s => s.Stride == 12) ?? streams.FirstOrDefault(s => s.Stride == 6);
        if (pos is null) return;
        var compressed = pos.Stride == 6;
        Vector3 boxMin = default, boxSize = default;
        if (compressed && o.Symbols.FirstOrDefault(s => s.Name.StartsWith("__BBOX:::", StringComparison.Ordinal)) is { Name: not null } box)
        {
            boxMin = new Vector3(o.F32(box.Value), o.F32(box.Value + 4), o.F32(box.Value + 8));
            boxSize = new Vector3(o.F32(box.Value + 12), o.F32(box.Value + 16), o.F32(box.Value + 20)) - boxMin;
        }
        var n = pos.Count;
        VertexBuffer Variant(VertexBuffer vb)
        {
            if (variant <= 0) return vb;
            // __GPvb_{slot}_{rm}_{model} → __GPvb_{slot}_{rm}_{variation}_{model}
            var name = vb.Name;
            var cut = name.LastIndexOf('_' + modelName, StringComparison.Ordinal);
            return cut > 0 && buffers.TryGetValue($"{name[..cut]}_{variant}_{modelName}", out var alt) && alt.Count == vb.Count ? alt : vb;
        }
        var normalStream = streams.Where(s => s.Stride == 12 && s != pos).FirstOrDefault();
        var uvStream = streams.FirstOrDefault(s => s.Stride == 8);
        var colorStream = streams.FirstOrDefault(s => s.Stride == 4);
        var s16Stream = streams.FirstOrDefault(s => s.Stride == 2);
        var posVb = Variant(pos);
        var baseIndex = mesh.Positions.Count;

        // Skin: the 2-byte stream is a byte offset into the RM's weight table, 4 per entry and biased by -96 (the shader's constant-register base).
        // The table is the {pointer, 38} pair right before the EAGLAnimationBuffer constant slot.
        var animSlot = anim.FirstOrDefault(a => a >= rm && a < limit, -1);
        int tableAt = -1, tableCount = 0;
        if (animSlot >= 8 && o.Ptr(animSlot - 8) is { } tp && o.U32(animSlot - 4) is var tc and > 0 and <= 64) { tableAt = tp; tableCount = 256; lastTable = tp; }
        else if (lastTable >= 0) { tableAt = lastTable; tableCount = 256; } // no slot of its own: the RM is drawn with the preceding RM's weight table
        else
            // RMs without an animation-buffer slot (rigid hands, lenses, soles, some jerseys) embed the table after their constant list:
            // the first run of 16-byte records whose weights sum to 1 and whose fourth dword is a packed-bone float.
            for (var at = streamEnd; at + 16 <= o.Data.Length && at < rm + 0x400; at += 4)
                if (IsWeightRecord(o, at) && (IsWeightRecord(o, at + 16) || o.U32(at + 16) == 0)) { tableAt = at; tableCount = 256; break; }
        SkinWeights[]? weightTable = null;
        if (tableAt >= 0) tableCount = Math.Min(tableCount, (o.Data.Length - tableAt) / 16);
        if (tableAt >= 0 && tableCount > 0)
        {
            weightTable = new SkinWeights[tableCount];
            for (var e = 0; e < tableCount; e++)
            {
                var r = tableAt + e * 16;
                float w0 = o.F32(r), w1 = o.F32(r + 4), w2 = o.F32(r + 8);
                if (!IsWeightRecord(o, r)) { weightTable[e] = SkinWeights.None; continue; }
                // the fourth dword is a float whose integer value packs three bone indices: bone0·2^24 + bone1·2^16 + bone2·2^8
                var packed = (uint)o.F32(r + 12);
                int b0 = (int)(packed >> 24), b1 = (int)(packed >> 16) & 0xFF, b2 = (int)(packed >> 8) & 0xFF;
                var sum = w0 + w1 + w2;
                weightTable[e] = new SkinWeights(b0, w1 > 0 ? b1 : -1, w2 > 0 ? b2 : -1, w0 / sum, w1 / sum, w2 / sum);
            }
        }
        SkinWeights SkinOf(VertexBuffer? vb, int i)
        {
            if (vb is null || weightTable is null) return SkinWeights.None;
            // register slot of the vertex's weight set; slot 14 holds another shader constant, so records after it sit one slot lower
            var entry = (Bin.S16LE(o.Data, vb.Data + i * 2) + 96) / 4;
            entry = entry switch { < 14 => entry, 14 => -1, _ => entry - 1 };
            return entry >= 0 && entry < weightTable.Length ? weightTable[entry] : SkinWeights.None;
        }
        for (var i = 0; i < n; i++)
        {
            mesh.Positions.Add(compressed
                ? boxMin + boxSize * new Vector3(Bin.U16LE(o.Data, posVb.Data + i * 6) / 65535f, Bin.U16LE(o.Data, posVb.Data + i * 6 + 2) / 65535f, Bin.U16LE(o.Data, posVb.Data + i * 6 + 4) / 65535f)
                : new Vector3(o.F32(posVb.Data + i * 12), o.F32(posVb.Data + i * 12 + 4), o.F32(posVb.Data + i * 12 + 8)));
            if (normalStream is not null)
            {
                var nv = Variant(normalStream);
                var nrm = new Vector3(o.F32(nv.Data + i * 12), o.F32(nv.Data + i * 12 + 4), o.F32(nv.Data + i * 12 + 8));
                mesh.Normals.Add(nrm.LengthSquared() > 0 && float.IsFinite(nrm.X) ? Vector3.Normalize(nrm) : Vector3.Zero);
            }
            else mesh.Normals.Add(Vector3.Zero);
            if (uvStream is not null) { var uv = Variant(uvStream); mesh.Uvs.Add(new Vector2(o.F32(uv.Data + i * 8), o.F32(uv.Data + i * 8 + 4))); }
            else mesh.Uvs.Add(Vector2.Zero);
            if (colorStream is not null)
            {
                var c = colorStream.Data + i * 4;   // D3DCOLOR: B, G, R, A
                mesh.Colors.Add(new Vector4(o.Data[c + 2] / 255f, o.Data[c + 1] / 255f, o.Data[c] / 255f, o.Data[c + 3] / 255f));
            }
            else mesh.Colors.Add(Vector4.One);
            mesh.Skin.Add(SkinOf(s16Stream is null ? null : Variant(s16Stream), i));
        }

        // Index buffer: the first {header, indexCount}/{data = header + 0x20, …} pair after this RM's animation-buffer constant.
        List<int>? indices = null;
        var slot = anim.FirstOrDefault(a => a >= rm && a < limit, -1);
        if (slot >= 0 && o.Ptr(slot + 8) is { } hdr && hdr > 0x300 && o.Ptr(slot + 16) is { } data && data == hdr + 0x20 && !byData.ContainsKey(hdr))
        {
            var count = (int)o.U32(slot + 12);
            if (count >= 3 && data + count * 2 <= o.Data.Length)
            {
                var list = new List<int>(count);
                for (var i = 0; i < count; i++) list.Add(Bin.U16LE(o.Data, data + i * 2));
                if (list.All(v => v < n)) indices = list;
            }
        }

        var info = new System.Text.StringBuilder();
        info.AppendLine($"; render method @0x{rm:X}  texture {texture ?? "-"}  {n:N0} vertices");
        foreach (var vb in streams)
        {
            var kind = vb.Stride switch { 12 or 6 when vb == pos => compressed ? "position (3×u16 in bbox)" : "position (3×f32)", 12 => "normal (3×f32)", 8 when vb == uvStream => "UV (2×f32)", 8 => "UV set 2 (2×f32)", 4 when vb == colorStream => "colour (BGRA8)", 4 => "packed normal? (4 bytes, not decoded)", 2 => "skin entry (s16 = 4·index − 96)", _ => $"stride {vb.Stride}" };
            info.AppendLine($"  {vb.Name,-34} {kind,-34} {vb.Stride,2} B × {vb.Count,5}  data @0x{vb.Data:X}");
        }
        info.AppendLine(indices is null ? "  no index buffer: vertices are consumed as a triangle strip" : $"  index buffer: {indices.Count:N0} × u16 (triangle strip) @0x{o.Ptr(slot + 16):X}");
        var draw = new EaglDraw
        {
            StreamInfo = info.ToString(),
            Index = mesh.Draws.Count, ChainOffset = -1, Texture = texture, TextureSymbol = textureSymbol, PrimState = state,
            FirstTriangle = mesh.Indices.Count / 3, Part = index, VertexCount = n,
        };
        var order = indices ?? Enumerable.Range(0, n).ToList();
        for (var i = 2; i < order.Count; i++)
        {
            int a = order[i - 2], b = order[i - 1], c = order[i];
            if (a == b || b == c || a == c) continue;
            if ((i & 1) == 0) { mesh.Indices.Add(baseIndex + a); mesh.Indices.Add(baseIndex + b); mesh.Indices.Add(baseIndex + c); }
            else { mesh.Indices.Add(baseIndex + b); mesh.Indices.Add(baseIndex + a); mesh.Indices.Add(baseIndex + c); }
        }
        draw.TriangleCount = mesh.Indices.Count / 3 - draw.FirstTriangle;
        mesh.Draws.Add(draw);
        var part = new EaglPart { Index = index };
        part.Draws.Add(draw);
        parts.Add(part);
    }
}
