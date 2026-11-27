// dotnet run --project tests/update_handoff -p:StableCore=/.../v1.3.1/Core -- <tag> <dist> <fixture-key> <scratch>
// The network fixture serves the release files from disk. Verification, extraction, staging, self-check and swap
// are the previous stable launcher's own implementation. The fixture key is never added to the production keyring.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LongshotLauncher.Core;

var (tag, dist, fixture, scratch) = (args[0], args[1], args[2], args[3]);
string[] testedPredecessors = ["v1.3.0", "v1.3.1"];
var originalTag = Environment.GetEnvironmentVariable("HANDOFF_ORIGINAL_TAG") ?? testedPredecessors[^1];
if (!testedPredecessors.Contains(originalTag)) throw new ArgumentException("Unknown original launcher tag.");
var channel = tag.StartsWith("nightly-", StringComparison.Ordinal) ? "nightly" : "stable";
var keys = ReleaseManifest.LauncherKeys;
var files = keys.Select(key => ReleaseManifest.LauncherFile(tag, key)).ToList();
// Include the entire release in its signed fixture. Stable clients select their
// legacy names even when the new canonical downloads are also available.
files.Add($"LongshotLauncher-{tag}-windows-x64.exe");
files.Add($"LongshotLauncher-{tag}-linux-x64.AppImage");
var assets = new List<(string Name, string Url, long Size)>();
var sums = new StringBuilder();
foreach (var name in files)
{
    using var f = File.OpenRead(Path.Combine(dist, name));
    sums.Append(Convert.ToHexStringLower(SHA256.HashData(f))).Append("  ").Append(name).Append('\n');
    assets.Add((name, "https://fixture.invalid/" + name, f.Length));
}
byte[] metadata;
string signature;
ReleaseKey releaseKey;
if (args.Length == 5)
{
    // Windows exercises public-key verification just as an installed launcher does.
    metadata = File.ReadAllBytes(Path.Combine(args[4], "SHA256SUMS"));
    signature = File.ReadAllText(Path.Combine(args[4], "SHA256SUMS.sig"));
    releaseKey = ReleaseKey.FromPem(File.ReadAllText(fixture));
}
else
{
    metadata = Encoding.UTF8.GetBytes(sums.ToString());
    using var ec = ECDsa.Create();
    ec.ImportFromPem(File.ReadAllText(fixture));
    signature = Convert.ToBase64String(ec.SignData(metadata, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    releaseKey = ReleaseKey.FromPem(ec.ExportSubjectPublicKeyInfoPem());
    var directory = Directory.CreateDirectory(Path.Combine(scratch, "signed-fixture")).FullName;
    File.WriteAllBytes(Path.Combine(directory, "SHA256SUMS"), metadata);
    File.WriteAllText(Path.Combine(directory, "SHA256SUMS.sig"), signature);
}
var checksums = SignedSums.Open(metadata, signature, [releaseKey]);
var release = ReleaseManifest.Build(new ListedRelease(tag, false, channel == "nightly", null, null, "", assets), checksums, Product.Launcher);
foreach (var key in keys)
{
    var linux = key == "linux-x64";
    var platform = linux ? Platform.Linux : Platform.Windows;
    var dir = Path.Combine(scratch, key);
    Directory.CreateDirectory(dir);
    var current = Path.Combine(dir, linux ? "Renamed launcher.AppImage" : "LongshotLauncher.exe");
    var original = Environment.GetEnvironmentVariable(linux ? "HANDOFF_ORIGINAL_LINUX" : "HANDOFF_ORIGINAL_WINDOWS");
    if (!string.IsNullOrEmpty(original))
    {
        // Optional: use the actual release, already checked against its signed SHA256SUMS by the caller.
        File.Copy(original, current, overwrite: true);
        if (linux == OperatingSystem.IsLinux() && !await SelfUpdate.SelfCheckAsync(current, originalTag, TimeSpan.FromSeconds(60))) throw new Exception("Wrong original launcher.");
    }
    else if (linux) File.WriteAllBytes(current, [0x7f, 0x45, 0x4c, 0x46, 2, 1, 1, 0, 0x41, 0x49, 2]);
    else File.WriteAllText(current, "This is the original launcher file.");
    var previous = Digest(current);
    foreach (var predecessor in testedPredecessors)
        if (SelfUpdate.UpdateTo(predecessor, release, platform, channel, key) != tag)
            throw new Exception($"{predecessor} does not offer {tag} for {key}.");
    var staged = await SelfUpdate.DownloadAsync(new LocalFiles(dist), release.Launcher[key], current,
        zipEntry: key == ReleaseManifest.WindowsPortable ? "LongshotLauncher.exe" : null);
    if (key == ReleaseManifest.WindowsPortable) File.WriteAllText(Path.Combine(dir, "portable"), "portable");
    if (linux == OperatingSystem.IsLinux())
    {
        if (!await SelfUpdate.SelfCheckAsync(staged, tag, TimeSpan.FromSeconds(60))) throw new Exception("Staged candidate failed --self-check.");
        SelfUpdate.Swap(current, staged, platform);
        if (Environment.GetEnvironmentVariable("HANDOFF_GUI_TEST") is { Length: > 0 } gui)
        {
            var test = new ProcessStartInfo("xvfb-run") { UseShellExecute = false };
            foreach (var arg in new[] { "-a", "python3", "-I", gui, "--check-handoff", current }) test.ArgumentList.Add(arg);
            using var firstStart = Process.Start(test) ?? throw new Exception("The first start check did not run.");
            await firstStart.WaitForExitAsync();
            if (firstStart.ExitCode != 0) throw new Exception("The updated launcher did not open the existing library.");
        }
        var p = new ProcessStartInfo(current) { UseShellExecute = false };
        p.ArgumentList.Add("--rollback");
        using var rollback = Process.Start(p) ?? throw new Exception("Rollback did not start.");
        await rollback.WaitForExitAsync();
        if (rollback.ExitCode != 0 || !Digest(current).SequenceEqual(previous)) throw new Exception("Rollback did not restore the previous launcher.");
        if (!string.IsNullOrEmpty(original) && !await SelfUpdate.SelfCheckAsync(current, originalTag, TimeSpan.FromSeconds(60))) throw new Exception("Restored launcher self-check failed.");
        Console.WriteLine($"{key}: v1.3.0 and v1.3.1 offered the candidate. Their updater verified, self-checked and swapped it. Rust restored the original.");
    }
    else if (!linux)
    {
        using var unpacked = File.OpenRead(staged);
        using var expected = File.OpenRead(Path.Combine(dist, files[0]));
        if (!SHA256.HashData(unpacked).SequenceEqual(SHA256.HashData(expected))) throw new Exception("Wrong Windows program.");
        if (Environment.GetEnvironmentVariable("HANDOFF_WINE") is { Length: > 0 } wine)
        {
            // Drive the actual PE files, including the staged .new, through Wine on a Linux host.
            var check = await RunWine(wine, staged, "--self-check");
            if (!check.Split('\n').Any(l => l.Trim() == SelfUpdate.CheckLine(tag))) throw new Exception("Windows self-check returned the wrong version.");
            SelfUpdate.Swap(current, staged, Platform.Windows);
            await RunWine(wine, current, "--rollback");
            if (!Digest(current).SequenceEqual(previous)) throw new Exception("Windows rollback failed.");
            Console.WriteLine($"{key}: The stable updater staged the Windows executable, Wine ran its self-check, and Rust restored the original.");
        }
        else Console.WriteLine($"{key}: The stable updater verified and staged the Windows executable.");
    }
}

static byte[] Digest(string path)
{
    using var file = File.OpenRead(path);
    return SHA256.HashData(file);
}

static async Task<string> RunWine(string wine, string file, string argument)
{
    var info = new ProcessStartInfo(wine) { UseShellExecute = false, RedirectStandardOutput = true };
    info.ArgumentList.Add(file);
    info.ArgumentList.Add(argument);
    using var child = Process.Start(info) ?? throw new Exception("Wine did not start.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var output = child.StandardOutput.ReadToEndAsync(deadline.Token);
    try { await child.WaitForExitAsync(deadline.Token); }
    catch (OperationCanceledException) { child.Kill(entireProcessTree: true); throw; }
    if (child.ExitCode != 0) throw new Exception($"Windows launcher failed {argument} ({child.ExitCode}).");
    return await output;
}

sealed class LocalFiles(string root) : IFetcher
{
    public Task<string> GetTextAsync(string url, string? accept, CancellationToken cancel) => throw new NotSupportedException();
    public async Task DownloadAsync(string url, Stream to, Action<long>? progress, CancellationToken cancel)
    {
        using var f = File.OpenRead(Path.Combine(root, new Uri(url).Segments.Last()));
        await f.CopyToAsync(to, cancel);
        progress?.Invoke(f.Length);
    }
}
