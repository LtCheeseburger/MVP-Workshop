using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace MVPWorkshop.App;

/// <summary>A newer build on GitHub: its version, notes, and the win-x64 zip (with its .sha256 when published).</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string ZipUrl, string ZipName, string? ShaUrl, string PageUrl);

/// <summary>
/// Checks the GitHub releases for a newer build and installs it: the zip is downloaded and checked first, then a small
/// script waits for this process to exit, copies the new files over the old ones and starts the new exe. Settings live in
/// %APPDATA% and aren't touched.
/// </summary>
public static class Updater
{
    public const string Repo = "LtCheeseburger/MVP-Workshop";

    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current => ParseVersion(AppInfo.Version) ?? new Version(0, 0, 0);

    static Version? ParseVersion(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>The latest release when it's newer than this build; null when up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var check = CancellationTokenSource.CreateLinkedTokenSource(ct);
        check.CancelAfter(TimeSpan.FromSeconds(15));
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", check.Token));
        var r = doc.RootElement;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (ParseVersion(tag) is not { } latest || latest <= Current) return null;
        string? zip = null, zipName = null, sha = null;
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) { zip = url; zipName = name; }
            else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (zip is null) return null;  // a release without the build attached yet
        var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(latest, tag, notes, zip, zipName!, sha, r.GetProperty("html_url").GetString() ?? "");
    }

    /// <summary>The folder holding MVP-Workshop.exe; the update replaces its files.</summary>
    public static string InstallDir => Path.GetDirectoryName(Environment.ProcessPath!)!;

    /// <summary>Why the files in <see cref="InstallDir"/> can't be replaced; null when they can.</summary>
    public static string? InstallProblem()
    {
        try
        {
            var probe = Path.Combine(InstallDir, ".update-check");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) { return $"Can't write to {InstallDir} ({ex.Message}). Move the tool to a folder you can write to, or download the update by hand."; }
    }

    /// <summary>Downloads, checks and unpacks the update; returns the folder holding the new files.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<string> progress, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "MVP-Workshop-update");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, update.ZipName);

        using (var resp = await Http.GetAsync(update.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[1 << 16];
            long done = 0, lastShown = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (done - lastShown >= 1 << 20)
                {
                    lastShown = done;
                    progress.Report(total is > 0 ? $"Downloading {update.Tag}: {done * 100 / total.Value}%" : $"Downloading {update.Tag}: {done >> 20} MB");
                }
            }
        }

        if (update.ShaUrl is not null)
        {
            progress.Report("Checking the download");
            var expected = (await Http.GetStringAsync(update.ShaUrl, ct)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
            await using var f = File.OpenRead(zipPath);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
            if (actual != expected) throw new InvalidDataException($"The download is damaged (sha256 {actual}, expected {expected}). Try again later.");
        }

        progress.Report("Unpacking");
        var staged = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zipPath, staged);
        // a zip holding one top folder: its contents are the build
        if (!File.Exists(Path.Combine(staged, "MVP-Workshop.exe")) && Directory.GetFiles(staged).Length == 0 && Directory.GetDirectories(staged) is [var only])
            staged = only;
        if (!File.Exists(Path.Combine(staged, "MVP-Workshop.exe"))) throw new InvalidDataException("The download has no MVP-Workshop.exe.");
        return staged;
    }

    /// <summary>Starts the script that waits for this process to exit, copies <paramref name="staged"/> over the install and relaunches.</summary>
    public static void LaunchSwap(string staged)
    {
        var exe = Environment.ProcessPath!;
        var script = Path.Combine(Path.GetDirectoryName(staged)!, "apply-update.ps1");
        var log = Path.Combine(Path.GetTempPath(), "MVP-Workshop-update.log");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            Start-Transcript -Path {{Q(log)}} -Force | Out-Null
            try { Wait-Process -Id {{Environment.ProcessId}} -Timeout 60 -ErrorAction SilentlyContinue } catch {}
            $src = {{Q(staged)}}; $dst = {{Q(InstallDir)}}
            $ok = $false
            for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
                try { Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force; $ok = $true }
                catch { Write-Output "copy failed: $_"; Start-Sleep -Milliseconds 500 }
            }
            if (-not $ok) { Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show("MVP-Workshop couldn't replace its files. The old version is still there; see $env:TEMP\MVP-Workshop-update.log.", 'MVP-Workshop update') | Out-Null }
            Start-Process -FilePath {{Q(exe)}} -WorkingDirectory $dst
            Stop-Transcript | Out-Null
            """);
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
