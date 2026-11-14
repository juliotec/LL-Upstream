// Linux always runs the link checks. Windows can run them too when Developer Mode or the symlink privilege is available;
// otherwise only successful link creation is omitted, with a visible notice. Rejection/security checks still run.
static class TestLinks
{
    public static readonly bool Available = Probe();

    static bool Probe()
    {
        if (!OperatingSystem.IsWindows()) return true;
        var target = Path.GetTempFileName();
        var link = target + ".link";
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException e) when ((e.HResult & 0xffff) == 1314) // ERROR_PRIVILEGE_NOT_HELD only
        {
            Console.WriteLine("SKIP: successful symbolic-link fixtures (Windows lacks the privilege); run Linux for full link coverage.");
            return false;
        }
        finally { File.Delete(link); File.Delete(target); }
    }
}
