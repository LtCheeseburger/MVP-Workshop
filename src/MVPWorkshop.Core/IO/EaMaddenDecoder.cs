using System.Diagnostics;
using System.Buffers.Binary;

namespace MVPWorkshop.Core.IO;

/// <summary>The 32-bit decoder runs in a separate process; requests are serialized and bounded.</summary>
public static class EaMaddenDecoder
{
    private static readonly object Gate = new();
    private static Process? worker;
    public static string ExecutablePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "NASCAR-Decoder.exe");
    public static bool Available => OperatingSystem.IsWindows() && File.Exists(ExecutablePath);

    static EaMaddenDecoder() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();

    private static void Stop()
    {
        try { if (worker is { HasExited: false }) worker.Kill(); } catch { }
        worker?.Dispose(); worker = null;
    }

    public static byte[] Decode(byte[] stored, int size)
    {
        if (stored.Length == 0 || stored.Length > 256 * 1024 * 1024 || size <= 0 || size > 256 * 1024 * 1024)
            throw new InvalidDataException("EA Madden entry exceeds decoder limits.");
        lock (Gate)
        {
            if (!Available) throw new NotSupportedException("NASCAR-Decoder.exe is required for TERF compression 5.");
            try
            {
                if (worker is null || worker.HasExited)
                    worker = Process.Start(new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var header = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(header, stored.Length);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), size);
                var input = worker.StandardInput.BaseStream;
                input.WriteAsync(header, timeout.Token).AsTask().GetAwaiter().GetResult();
                input.WriteAsync(stored, timeout.Token).AsTask().GetAwaiter().GetResult();
                input.FlushAsync(timeout.Token).GetAwaiter().GetResult();
                var output = worker.StandardOutput.BaseStream;
                output.ReadExactlyAsync(header.AsMemory(0, 4), timeout.Token).AsTask().GetAwaiter().GetResult();
                if (BinaryPrimitives.ReadInt32LittleEndian(header) != size) throw new InvalidDataException("EA Madden decoder returned an unexpected output size.");
                var result = new byte[size];
                output.ReadExactlyAsync(result, timeout.Token).AsTask().GetAwaiter().GetResult();
                return result;
            }
            catch (Exception ex) { Stop(); throw new InvalidDataException("EA Madden decode failed: " + ex.Message, ex); }
        }
    }
}
