using System.Collections.Concurrent;
using LongshotLauncher;
using LongshotLauncher.Core;
using LongshotLauncher.ViewModels;

namespace LongshotScreenshots;

/// Launcher-level check: dotnet run --project tools/screenshots -- --check-vulkan
static class VulkanChecks
{
    public static int Run()
    {
        var home = Directory.CreateTempSubdirectory("longshot-vulkan-check-");
        var post = Ui.Post;
        using var ready = new SemaphoreSlim(0);
        var queue = new ConcurrentQueue<Action>();
        try
        {
            Ui.Post = action => { queue.Enqueue(action); ready.Release(); };
            var service = LauncherService.Open(Platform.Windows, new LauncherHome(home.FullName));
            var player = Path.Combine(home.FullName, "player");
            service.Settings.BuildsFolder = Path.Combine(player, "Games", "Longshot");
            service.SaveEnv = new SaveEnvironment(Platform.Windows, name => name switch
            {
                "HOME" or "USERPROFILE" => player,
                "XDG_DATA_HOME" => Path.Combine(player, ".local", "share"),
                "APPDATA" => Path.Combine(player, "AppData", "Roaming"),
                "LOCALAPPDATA" => Path.Combine(player, "AppData", "Local"),
                _ => null,
            });
            service.ShaderStoreOverride = () => 0;
            service.GpuCards = [new Gpu(SystemInfo.Amd, "AMD Radeon(TM) Graphics"), new Gpu(SystemInfo.Nvidia, "NVIDIA GeForce RTX 5080")];
            service.Settings.Fsr = true;
            service.Settings.FsrLicense = true;
            var pending = new TaskCompletionSource<IReadOnlyList<GpuRequirement>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int probes = 0;
            service.VulkanProbe = () => { probes++; return pending.Task; };
            var main = new MainViewModel(service, StartRequest.Parse([]));
            var job = new BuildJob(main, JobKind.Build, "stable", "v3.0.0", null);
            var settings = new SettingsStepViewModel(job);
            var changed = new HashSet<string?>();
            settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Check(settings.FsrYourGpu && !settings.FsrUnsupported, "Fallback initially includes AMD graphics.");
            var build = new BuildStepViewModel(job, start: false);
            Check(build.Log.Last(10).Any(l => l.Contains("FSR 4 (Auto GPU check pending)")), "Early build records an unknown GPU check.");
            pending.SetResult([new(true, 16UL << 30, true, GpuKind.Discrete, 1, SystemInfo.Nvidia, "NVIDIA GeForce RTX 5080")]);
            while (!changed.Contains(nameof(settings.FsrLine)))
            {
                Check(ready.Wait(TimeSpan.FromSeconds(5)), "GPU refresh was not posted.");
                if (queue.TryDequeue(out var action)) action();
            }
            Check(changed.Contains(nameof(settings.FsrYourGpu)) && changed.Contains(nameof(settings.FsrUnsupported))
                && !settings.FsrYourGpu && settings.FsrUnsupported && settings.ShowFsrLine
                && settings.FsrLine.Contains("estimated Auto GPU"), "Badge and red line refresh for the selected GPU.");
            var resolved = new BuildStepViewModel(job, start: false);
            Check(resolved.Log.Last(10).Any(l => l.Contains("FSR 4 (estimated Auto GPU does not run it)")), "Resolved build log uses the estimate.");
            var next = new BuildJob(main, JobKind.Build, "stable", "v3.0.0", null);
            _ = next.VulkanGpus;
            Check(probes == 2, "Each wizard starts a fresh probe; its steps share one.");
            return 0;
        }
        finally
        {
            Ui.Post = post;
            Directory.Delete(home.FullName, true);
        }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
