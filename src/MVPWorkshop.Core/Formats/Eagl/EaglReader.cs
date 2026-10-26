using System.Numerics;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats.Eagl;

/// <summary>
/// Reads EA EAGL (PS2) model objects. See docs/FORMATS.md "EAGL models" for the layout this implements:
/// __Model → render program (opcode list) → draw DMA chains → VIF batches → slot-ordered triangle strips.
/// </summary>
public static class EaglReader
{
    // __Model field offsets (relative to the symbol)
    private const int ModelQuantScale = 0x4C, ModelQuantHalf = 0x5C, ModelBoundsMin = 0x6C, ModelBoundsMax = 0x7C, ModelSphere = 0x8C;
    private const int ModelNameTable = 0xA0, ModelName = 0xB4, ModelGeometry = 0xCC;

    public static bool LooksLikeModel(RelocatedObject o) => o.Symbols.Any(s => s.Name.StartsWith("__Model:::", StringComparison.Ordinal));

    public static EaglFile Read(byte[] joinedElf, int variant = 0) => Read(RelocatedObject.Load(joinedElf), variant);

    /// <param name="variant">Geometry variation to select from CALL jump tables (head shapes; __Model ... .variationN).</param>
    public static EaglFile Read(RelocatedObject o, int variant = 0)
    {
        if (EaglXboxReader.LooksLikeXbox(o)) return EaglXboxReader.Read(o, variant);
        var file = new EaglFile { Object = o };
        foreach (var r in o.Relocs.Values)
        {
            if (r.External is not { } ext) continue;
            if (ext.Contains("SHAPENAME=", StringComparison.Ordinal)) { var t = TextureName(ext); if (!file.Textures.Contains(t)) file.Textures.Add(t); }
            else if (ext.EndsWith("__EAGLMicroCode", StringComparison.Ordinal) && !file.Microcode.Contains(ext)) file.Microcode.Add(ext);
        }
        foreach (var s in o.Symbols.OrderBy(s => s.Value))
        {
            if (s.Name.StartsWith("__Bone:::", StringComparison.Ordinal))
                file.Bones.Add((s.Name[9..], (int)o.U32(s.Value)));
            else if (s.Name.StartsWith("__Skeleton:::", StringComparison.Ordinal))
            { file.SkeletonName = s.Name[13..]; file.SkeletonBoneCount = (int)o.U32(s.Value + 8); }
            else if (s.Name.StartsWith("__EAGL_TOOLLIB_VERSION", StringComparison.Ordinal)) file.ToolLibVersion = s.Name.Split(":::").Last();
        }

        file.Skeleton = EaglSkeleton.Read(o, file.Bones.GroupBy(b => b.Index).ToDictionary(g => g.Key, g => g.First().Name.Split('.').Last()));
        file.Morphs = EaglMorphSet.Read(o);

        // Variations of one model share the same geometry block; decode it once.
        var byGeometry = new Dictionary<int, EaglModel>();
        foreach (var s in o.Symbols.Where(s => s.Name.StartsWith("__Model:::", StringComparison.Ordinal)).OrderBy(s => s.Value))
        {
            var name = s.Name[10..];
            var m = s.Value;
            var geo = o.Ptr(m + ModelGeometry) ?? -1;
            if (geo >= 0 && byGeometry.TryGetValue(geo, out var existing)) { existing.Variations.Add(name); continue; }

            var mesh = new EaglMesh();
            var model = new EaglModel
            {
                Name = name, Offset = m, GeometryKey = geo, Mesh = mesh,
                IsApt = file.Microcode.Any(c => c.Contains("Apt", StringComparison.Ordinal)),
                QuantScale = V3(o, m + ModelQuantScale), QuantOffset = V3(o, m + ModelQuantHalf),
                BoundsMin = V3(o, m + ModelBoundsMin), BoundsMax = V3(o, m + ModelBoundsMax),
                Sphere = new Vector4(V3(o, m + ModelSphere), o.F32(m + ModelSphere + 12)),
            };
            if (o.Ptr(m + ModelNameTable) is { } nt)
                for (var i = 0; i < 256 && o.Ptr(nt + i * 4) is { } np; i++) model.Names.Add(Bin.CString(o.Data, np, 128));

            if (geo >= 0) FindParts(o, geo, model);
            if (model.Programs.Count == 0)
                mesh.Warnings.Add(o.Data.Length < 0x200 ? "Empty placeholder model (no geometry in this object)." : "No render program found under the geometry pointer.");
            foreach (var part in model.Parts)
                foreach (var prog in part.Programs)
                {
                    var before = mesh.Draws.Count;
                    RunProgram(o, prog, model, mesh, variant);
                    for (var d = before; d < mesh.Draws.Count; d++) { mesh.Draws[d].Part = part.Index; part.Draws.Add(mesh.Draws[d]); }
                }
            file.Models.Add(model);
            if (geo >= 0) byGeometry[geo] = model;
        }
        return file;
    }

    private static Vector3 V3(RelocatedObject o, int at) => new(o.F32(at), o.F32(at + 4), o.F32(at + 8));

    public static string TextureName(string tarSymbol)
    {
        var i = tarSymbol.IndexOf("SHAPENAME=", StringComparison.Ordinal);
        if (i < 0) return tarSymbol;
        var rest = tarSymbol[(i + 10)..];
        var end = rest.IndexOfAny([',', ';']);
        return end < 0 ? rest : rest[..end];
    }

    private static bool IsProgram(RelocatedObject o, int at) =>
        o.U32(at) >> 24 == 0x01 && o.External(at + 4)?.EndsWith("__EAGLMicroCode", StringComparison.Ordinal) == true;

    /// <summary>
    /// Geometry block: u32 0, then one pointer per part. Each part pointer → slot → array of program pointers
    /// (programs start with opcode 0x01 + microcode). Heads have 1 part; bodies have ~38 toggleable parts.
    /// </summary>
    private static void FindParts(RelocatedObject o, int geo, EaglModel model)
    {
        for (var k = 1; k < 512 && o.Ptr(geo + k * 4) is { } entry; k++)
        {
            var part = new EaglPart { Index = k - 1 };
            if (o.Ptr(entry) is { } arr)
                for (var i = 0; i < 64 && o.Ptr(arr + i * 4) is { } prog && IsProgram(o, prog); i++)
                    if (!model.Programs.Contains(prog)) { part.Programs.Add(prog); model.Programs.Add(prog); }
            if (part.Programs.Count > 0) model.Parts.Add(part);
        }
    }

    /// <summary>
    /// Interpret the EAGL render program. Known opcodes (top byte): 01 microcode, 03 texture list, 05 upload,
    /// 06 upload texture (TAR), 07 GeoPrimState, 04 draw DMA chain. Unknown words are skipped.
    /// </summary>
    private static void RunProgram(RelocatedObject o, int start, EaglModel model, EaglMesh mesh, int variant)
    {
        string? tex = null, state = null, texSymbol = null, baseTexture = null;
        int[]? palette = null;
        Matrix3x2? textureMatrix = null;
        var o2 = start;
        for (var guard = 0; guard < 20000 && o2 + 8 <= o.Data.Length; guard++)
        {
            var w = o.U32(o2);
            if (w == 0 && o.U32(o2 + 4) == 0 && o2 > start) break;
            if (w >> 24 == 0x01 && o2 > start && o.External(o2 + 4)?.EndsWith("__EAGLMicroCode", StringComparison.Ordinal) == true && model.Programs.Contains(o2)) break;
            if (w >> 16 == 0x0601 && o.External(o2 + 8) is { } t && t.Contains("SHAPENAME=")) { tex = TextureName(t); texSymbol = t; }
            if (w == 0x03000002 && o.External(o2 + 4) is { } first && first.Contains("SHAPENAME=")) baseTexture = TextureName(first);
            if (w >> 24 == 0x07 && o.External(o2 + 8) is { } ps) state = ps;
            // APT texture-coordinate matrix: 0D000001, relocated MATRIX4 pointer.
            // Coordinates are texels; image dimensions normalize them at rendering time.
            if (model.IsApt && w == 0x0D000001 && o.Ptr(o2 + 8) is { } tm && tm + 64 <= o.Data.Length)
                textureMatrix = new Matrix3x2(o.F32(tm), o.F32(tm + 4), o.F32(tm + 16), o.F32(tm + 20), o.F32(tm + 48), o.F32(tm + 52));
            // 0b0000nn →EAGLAnimationBuffer count →list: matrix palette (skeleton bone index per VU slot)
            if (w >> 24 == 0x0B && o.External(o2 + 4)?.Contains("AnimationBuffer", StringComparison.Ordinal) == true)
            {
                var count = (int)o.U32(o2 + 8);
                palette = o.Ptr(o2 + 12) is { } lp && count is > 0 and < 128
                    ? Enumerable.Range(0, count).Select(k => (int)o.U32(lp + k * 4)).ToArray()
                    : null;
            }
            // 04000002 <chain ptr>: draw a DMA chain stored elsewhere.
            // 14000004 <inline DMA tag (16 bytes)>: draw a single REF tag stored in the program itself.
            var isChain = w == 0x04000002 && o.Ptr(o2 + 4) is not null;
            var isInline = w == 0x14000004 && (o.U32(o2 + 4) >> 28 & 7) is 3 or 4;
            if (isChain || isInline)
            {
                var chain = isChain ? o.Ptr(o2 + 4)!.Value : o2 + 4;
                var draw = new EaglDraw { Index = mesh.Draws.Count, ChainOffset = chain, Texture = tex, TextureSymbol = texSymbol, BaseTexture = baseTexture, PrimState = state, TextureMatrix = textureMatrix, Palette = palette, FirstTriangle = mesh.Indices.Count / 3, FirstLine = mesh.Lines.Count };
                var v0 = mesh.Positions.Count;
                try { DecodeChain(o, chain, model, mesh, draw, isInline, variant); }
                catch (Exception ex) { mesh.Warnings.Add($"draw {draw.Index} @0x{chain:X}: {ex.Message}"); }
                draw.TriangleCount = mesh.Indices.Count / 3 - draw.FirstTriangle;
                draw.LineCount = mesh.Lines.Count - draw.FirstLine;
                draw.VertexCount = mesh.Positions.Count - v0;
                mesh.Draws.Add(draw);
                o2 += isChain ? 8 : 20;
                continue;
            }
            o2 += 4;
        }
    }

    /// <summary>Follow a PS2 DMA source chain (REF/REFS/CNT/NEXT/CALL/RET/END) and return the VIF bytes it would send.</summary>
    /// <summary>Number of consecutive empty CALL tags at a chain start (≥2 = a variation jump table, not a call sequence).</summary>
    public static int JumpTableSize(RelocatedObject o, int at, bool singleTag)
    {
        if (singleTag) return 0;
        var n = 0;
        while (n < 64 && (o.U32(at + n * 16) >> 28 & 7) == 5 && (o.U32(at + n * 16) & 0xFFFF) == 0 && o.Ptr(at + n * 16 + 4) is not null) n++;
        return n;
    }

    public static byte[] FlattenChain(RelocatedObject o, int at, bool singleTag = false, List<(int Vif, int Data, int Length)>? segments = null, int variant = 0)
    {
        if (singleTag)
        {
            var q = (int)(o.U32(at) & 0xFFFF);
            var src = o.Ptr(at + 4) ?? (int)o.U32(at + 4);
            segments?.Add((0, src, q * 16));
            return src >= 0 && src + q * 16 <= o.Data.Length ? o.Data.AsSpan(src, q * 16).ToArray() : [];
        }
        // A run of CALL tags with no RET after it is a jump table indexed by variation: follow only one entry.
        var table = JumpTableSize(o, at, false);
        if (table > 1) at = o.Ptr(at + Math.Clamp(variant, 0, table - 1) * 16 + 4)!.Value;
        var outp = new List<byte>();
        var stack = new Stack<int>();
        for (var guard = 0; guard < 100000; guard++)
        {
            var tag = o.U32(at);
            var qwc = (int)(tag & 0xFFFF);
            var id = (int)(tag >> 28) & 7;
            var addr = o.Ptr(at + 4) ?? (int)o.U32(at + 4);
            void Take(int from, int q)
            {
                if (from < 0 || from + q * 16 > o.Data.Length) return;
                segments?.Add((outp.Count, from, q * 16));
                outp.AddRange(o.Data.AsSpan(from, q * 16).ToArray());
            }
            switch (id)
            {
                case 3 or 4: Take(addr, qwc); at += 16; break;           // REF / REFS
                case 1: Take(at + 16, qwc); at += 16 + qwc * 16; break;   // CNT
                case 2: Take(at + 16, qwc); at = addr; break;             // NEXT
                case 5: Take(at + 16, qwc); stack.Push(at + 16 + qwc * 16); at = addr; break; // CALL
                case 6: Take(at + 16, qwc); if (stack.Count == 0) return [.. outp]; at = stack.Pop(); break; // RET
                case 0: Take(addr, qwc); return [.. outp];               // REFE
                default: Take(at + 16, qwc); return [.. outp];           // END
            }
            if (outp.Count > 64 * 1024 * 1024) throw new InvalidDataException("DMA chain too long.");
        }
        throw new InvalidDataException("DMA chain did not terminate.");
    }

    /// <summary>
    /// Vertex data of one VU1 batch (between kicks). Two layouts exist:
    /// skinned — V4-16 quantised positions (usn, STMOD offset), V4-16 UV+output slots, V3-32 normals;
    /// rigid   — V3-32 float positions, V2-32 float UVs, V4-8 colours, vertices already in strip order.
    /// </summary>
    private sealed class Batch
    {
        public byte[]? Pos16, Pos3x16, Pos2D, PosF, Uv16, UvF, Nrm, Col;
        public int Count;
        public List<byte> Restarts = [];
        public bool HeaderSeen;
        public int PosDataOffset = -1;
    }

    /// <summary>Skinning state carried across the batches of one draw.</summary>
    private sealed class SkinState
    {
        /// <summary>Weight table (≤3 weights per entry; bone slot ×4 in each weight's low mantissa byte).</summary>
        public List<(float W, int Slot)[]>? Table;
        public (float W, int Slot)[]? Current;
    }

    private static void DecodeChain(RelocatedObject o, int chain, EaglModel model, EaglMesh mesh, EaglDraw draw, bool singleTag, int variant)
    {
        var segments = new List<(int Vif, int Data, int Length)>();
        var tableSize = JumpTableSize(o, chain, singleTag);
        if (tableSize > 1) model.VariantCount = Math.Max(model.VariantCount, tableSize);
        var vif = FlattenChain(o, chain, singleTag, segments, variant);
        int DataOffsetOf(int vifOffset)
        {
            foreach (var s in segments) if (vifOffset >= s.Vif && vifOffset < s.Vif + s.Length) return s.Data + vifOffset - s.Vif;
            return -1;
        }
        var skin = new SkinState();
        var cur = new Batch();
        foreach (var c in Vif.Parse(vif))
        {
            if (c.IsKick) { Emit(cur, model, mesh, draw, skin); cur = new Batch(); continue; }
            // Weight table: absolute (non-TOPS) V4-32 upload to VU address 4, shared by the draw's batches.
            if (c.IsUnpack && !c.TopsRelative && c.Format == 0x0C && c.Address == 4 && c.PayloadSize >= c.Count * 16)
            {
                skin.Table = [];
                for (var e = 0; e < c.Count; e++)
                {
                    var at = c.PayloadOffset + e * 16;
                    var entry = new List<(float, int)>();
                    for (var k = 0; k < 3; k++)
                    {
                        var bits = Bin.U32LE(vif, at + k * 4);
                        var wgt = BitConverter.Int32BitsToSingle((int)(bits & 0xFFFFFF00));
                        if (wgt > 0) entry.Add((wgt, (int)(bits & 0xFF) / 4));
                    }
                    skin.Table.Add([.. entry]);
                }
                continue;
            }
            if (!c.IsUnpack || !c.TopsRelative || c.PayloadSize < Vif.UnpackSize(c.Cmd, c.Count)) continue;
            var payload = vif.AsSpan(c.PayloadOffset, c.PayloadSize).ToArray();
            var n = c.Count;
            if (c.Format == 0x0D && c.Unsigned && cur.Pos16 is null && n > 2) cur.PosDataOffset = DataOffsetOf(c.PayloadOffset);
            if (n == 1)
            {
                if (c.Format == 0x0C && c.Address == 0) cur.HeaderSeen = true;     // batch header (vertex count, …)
                else if (c.Format == 0x0E && cur.HeaderSeen && cur.Restarts.Count < 12) cur.Restarts.AddRange(payload); // strip restarts
                continue;
            }
            switch (c.Format)
            {
                case 0x05 when model.IsApt && cur.Pos2D is null: cur.Pos2D = payload; cur.Count = n; break;
                case 0x0D when c.Unsigned && cur.Pos16 is null && cur.PosF is null: cur.Pos16 = payload; cur.Count = n; break;
                case 0x0D when !c.Unsigned && cur.Uv16 is null: cur.Uv16 = payload; break;
                case 0x09 when c.Unsigned && cur.Pos3x16 is null: cur.Pos3x16 = payload; cur.Count = n; break;
                case 0x08 when cur.Pos16 is null && cur.Pos3x16 is null && cur.PosF is null: cur.PosF = payload; cur.Count = n; break;
                case 0x08 when cur.Nrm is null: cur.Nrm = payload; break;
                case 0x04 when cur.UvF is null: cur.UvF = payload; break;
                case 0x0E when cur.Col is null: cur.Col = payload; break;
            }
        }
        Emit(cur, model, mesh, draw, skin);
    }

    private static void Emit(Batch b, EaglModel model, EaglMesh mesh, EaglDraw draw, SkinState skin)
    {
        if ((b.Pos16 is null && b.Pos3x16 is null && b.PosF is null && b.Pos2D is null) || b.Count < 3) return;
        draw.Batches++;
        var n = b.Count;
        var baseIndex = mesh.Positions.Count;
        if (b.PosDataOffset >= 0) mesh.BatchByDataOffset[b.PosDataOffset] = baseIndex;

        // Skin: vertices are sorted by weight set; w = entry*7 starts a new set, 0xFFFF repeats the previous one.
        for (var i = 0; i < n; i++)
        {
            var sw = SkinWeights.None;
            if (b.Pos16 is not null && skin.Table is { Count: > 0 } table && draw.Palette is { Length: > 0 } pal)
            {
                var w = Bin.U16LE(b.Pos16, i * 8 + 6);
                if (w != 0xFFFF && w / 7 < table.Count) skin.Current = table[w / 7];
                skin.Current ??= table[^1];
                sw = SkinWeights.From(skin.Current, pal);
            }
            else if (draw.Palette is { Length: 1 } single)
                sw = new SkinWeights(single[0], -1, -1, 1, 0, 0);
            mesh.Skin.Add(sw);
        }
        var s = model.QuantScale; var h = model.QuantOffset;
        var slots = new Dictionary<int, int>();
        for (var i = 0; i < n; i++)
        {
            if (b.Pos2D is not null)
                mesh.Positions.Add(new Vector3(Bin.U16LE(b.Pos2D, i * 4) / 65536f * s.X - h.X, Bin.U16LE(b.Pos2D, i * 4 + 2) / 65536f * s.Y - h.Y, 0));
            else if (b.Pos3x16 is not null)
                mesh.Positions.Add(new Vector3(Bin.U16LE(b.Pos3x16, i * 6) / 65536f * s.X - h.X, Bin.U16LE(b.Pos3x16, i * 6 + 2) / 65536f * s.Y - h.Y, Bin.U16LE(b.Pos3x16, i * 6 + 4) / 65536f * s.Z - h.Z));
            else if (b.Pos16 is not null)
            {
                int x = Bin.U16LE(b.Pos16, i * 8), y = Bin.U16LE(b.Pos16, i * 8 + 2), z = Bin.U16LE(b.Pos16, i * 8 + 4);
                mesh.Positions.Add(new Vector3(x / 65536f * s.X - h.X, y / 65536f * s.Y - h.Y, z / 65536f * s.Z - h.Z));
            }
            else
                mesh.Positions.Add(new Vector3(BitConverter.ToSingle(b.PosF!, i * 12), BitConverter.ToSingle(b.PosF!, i * 12 + 4), BitConverter.ToSingle(b.PosF!, i * 12 + 8)));

            if (b.Uv16 is not null && i * 8 + 8 <= b.Uv16.Length)
            {
                short u = Bin.S16LE(b.Uv16, i * 8), v = Bin.S16LE(b.Uv16, i * 8 + 2), s0 = Bin.S16LE(b.Uv16, i * 8 + 4), s1 = Bin.S16LE(b.Uv16, i * 8 + 6);
                mesh.Uvs.Add(new Vector2(u / 4096f, v / 4096f));
                if (s0 >= 0) slots[s0 / 3] = baseIndex + i;
                if (s1 >= 0) slots[s1 / 3] = baseIndex + i;
            }
            else if (b.UvF is not null && i * 8 + 8 <= b.UvF.Length)
            {
                mesh.Uvs.Add(new Vector2(BitConverter.ToSingle(b.UvF, i * 8), BitConverter.ToSingle(b.UvF, i * 8 + 4)));
                slots[i] = baseIndex + i;
            }
            else if (b.Pos2D is not null)
            {
                var p = mesh.Positions[^1];
                mesh.Uvs.Add(draw.TextureMatrix is { } mapping ? Vector2.Transform(new Vector2(p.X, p.Y), mapping) : Vector2.Zero);
                slots[i] = baseIndex + i;
            }
            else { mesh.Uvs.Add(Vector2.Zero); slots[i] = baseIndex + i; }

            mesh.Colors.Add(b.Col is not null && i * 4 + 4 <= b.Col.Length
                ? new Vector4(b.Col[i * 4] / 128f, b.Col[i * 4 + 1] / 128f, b.Col[i * 4 + 2] / 128f, b.Col[i * 4 + 3] / 128f)
                : Vector4.One);

            var nrm = Vector3.Zero;
            if (b.Nrm is not null && i * 12 + 12 <= b.Nrm.Length)
            {
                var raw = new Vector3(BitConverter.ToSingle(b.Nrm, i * 12), BitConverter.ToSingle(b.Nrm, i * 12 + 4), BitConverter.ToSingle(b.Nrm, i * 12 + 8));
                if (raw.LengthSquared() > 0 && float.IsFinite(raw.X)) nrm = Vector3.Normalize(raw);
            }
            mesh.Normals.Add(nrm);
        }
        if (slots.Count == 0) return;
        if (draw.IsLineList)
        {
            for (var k = 0; k + 1 < n; k += 2) if (slots.TryGetValue(k, out var l0) && slots.TryGetValue(k + 1, out var l1)) mesh.Lines.Add((l0, l1));
            return;
        }

        // Strip restarts: zero-terminated list of VU output-buffer offsets, one vertex = 3 quadwords when skinned/rigid
        // (position, UV, normal) but 2 for APT (position, UV/colour).
        var restarts = new HashSet<int>();
        var stride = b.Pos2D is not null ? 2 : 3;
        foreach (var r in b.Restarts) { if (r == 0) break; restarts.Add(r / stride); }

        var maxSlot = slots.Keys.Max();
        void Corner(int vertex, int slot)
        {
            if (draw.BaseTexture is not null && b.Uv16 is not null && b.UvF is not null && slot*8+8<=b.UvF.Length)
                draw.SecondaryUvs[mesh.Indices.Count] = new Vector2(BitConverter.ToSingle(b.UvF,slot*8),BitConverter.ToSingle(b.UvF,slot*8+4));
            mesh.Indices.Add(vertex);
        }
        var stripStart = 0;
        for (var k = 2; k <= maxSlot; k++)
        {
            if (restarts.Contains(k - 2) || k - 2 == 0) stripStart = k - 2;
            if (restarts.Contains(k) || restarts.Contains(k - 1)) continue;
            if (!slots.TryGetValue(k - 2, out var a) || !slots.TryGetValue(k - 1, out var c1) || !slots.TryGetValue(k, out var c2)) continue;
            if (a == c1 || c1 == c2 || a == c2) continue;
            // alternate winding along the strip
            if (((k - stripStart) & 1) == 0) { Corner(a,k-2); Corner(c1,k-1); Corner(c2,k); }
            else { Corner(c1,k-1); Corner(a,k-2); Corner(c2,k); }
        }
    }
}










