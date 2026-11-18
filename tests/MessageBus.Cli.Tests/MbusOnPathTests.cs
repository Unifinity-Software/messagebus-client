using MessageBus.Cli.Hosting;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class MbusOnPathTests
{
    [Fact]
    public void A_path_with_a_space_is_written_so_the_owners_shell_runs_it()
    {
        // the original issue: PowerShell (Windows' shell) treats a quoted path alone as a string; POSIX shells take it single-quoted.
        Assert.Equal("""& "C:\Users\Jane Doe\AppData\Local\messagebus\mbus.exe" doctor""",
            new MbusOnPath(@"C:\Users\Jane Doe\AppData\Local\messagebus\mbus.exe", null) { Windows = true }.Advice("mbus doctor"));
        Assert.Equal("""& "C:\Users\J`$ne\mbus.exe" doctor""", new MbusOnPath(@"C:\Users\J$ne\mbus.exe", null) { Windows = true }.Advice("mbus doctor"));
        Assert.Equal("""'/Users/Jane Doe/it'\''s/mbus' doctor""", new MbusOnPath("/Users/Jane Doe/it's/mbus", null) { Windows = false }.Advice("mbus doctor"));
        Assert.Equal(@"C:\mbus\mbus.exe doctor", new MbusOnPath(@"C:\mbus\mbus.exe", null) { Windows = true }.Advice("mbus doctor"));
    }

    [Fact]
    public void Advice_rewrites_only_mbus_commands_the_owner_is_told_to_run()
    {
        var mbus = new MbusOnPath("/opt/mbus/mbus", null) { Windows = false };

        Assert.Equal("Fix: /opt/mbus/mbus login, then /opt/mbus/mbus runner.", mbus.Advice("Fix: mbus login, then mbus runner."));
        Assert.Equal(@"run C:\x\mbus doctor", mbus.Advice(@"run C:\x\mbus doctor"));
        Assert.Equal("https://h/mbus connect", mbus.Advice("https://h/mbus connect"));
        Assert.Equal("mbus tools", mbus.Advice("mbus tools"));
    }
}
