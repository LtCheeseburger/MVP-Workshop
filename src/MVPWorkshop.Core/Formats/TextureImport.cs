namespace MVPWorkshop.Core.Formats;

public static class TextureImport
{
    public const string ResizeBlocker = "Native upscales retain imported resolution in project/mod/ISO output. Storage is validated, but game-side texel UVs, GS texture state and VRAM allocation are not rewritten; runtime compatibility is experimental.";

    public static string Describe(SshFile file, SshImage image, int width, int height)
    {
        var h = image.Header;
        var pixels = checked((long)h.Width * h.Height);
        var indexed = image.TypeMasked is 1 or 2 or >= 8 and <= 15;
        var palette = image.Palette?.DataLength ?? 0;
        var retainedPixels = checked((long)width * height);
        var retainedResident = indexed ? retainedPixels * (image.TypeMasked == 1 ? 4 : 8) / 8 + palette : retainedPixels * SshFile.Bpp(h.Type) / 8;
        var baseStorage = image.TypeMasked is 1 or 2 or 4 or 5 or >= 8 and <= 15 ? SshWriter.ExpectedBytes(image) : image.DataLength;
        var resident = indexed ? pixels * (image.TypeMasked == 1 ? 4 : 8) / 8 + palette : pixels * SshFile.Bpp(h.Type) / 8;
        return $"Imported: {width}×{height} · decoded BGRA {checked((long)width * height * 4):N0} bytes\n" +
            $"Native: {h.Width}×{h.Height} · {SshFile.TypeName(h.Type)}\n" +
            $"Base storage: {baseStorage:N0} bytes; palette block: {palette:N0} bytes\n" +
            $"Estimated resident payload: {resident:N0} bytes (excludes GS page padding, mipmaps, staging and other textures; GS has 4 MiB shared VRAM)\n" +
            $"Retained-resolution payload estimate: {retainedResident:N0} bytes (same exclusions; not a runtime allocation guarantee)\n" +
            $"Alpha: {(image.TypeMasked == 4 ? "discarded (RGB)" : "PS2 0–128 alpha; indexed/GST palette quantization may lose detail")}\n" +
            (image.TypeMasked >= 8 ? "GST is lossy: each block shares a palette base and only 1/2-bit offsets. Even a 256-colour import may change visibly.\n" : "") +
            $"Native writer: {SshWriter.Unsupported(file, image) ?? "supported at source dimensions; game compatibility untested"}\n" +
            (width == h.Width && height == h.Height ? "Dimensions preserved." : (SshWriter.ResizeUnsupported(file, image, width, height) is { } resizeWhy ? "Native upscale unavailable: " + resizeWhy + "\n" : "Native upscale available. ") + ResizeBlocker) +
            "\nPNG exports are ordinary images, not emulator replacements. Emulator filenames require a matching runtime texture dump/hash; SSH tags are insufficient.";
    }
}
