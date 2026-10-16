using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

public sealed class StartupHandshakeTests
{
    [Theory]
    [InlineData("token:42",42,false,42)]
    [InlineData("token:handoff:42:43",42,true,43)]
    public void ValidReadyOrExplicitHandoffIsAccepted(string text,int parent,bool handoff,int pid)
    {
        var signal=UpdateService.ParseStartupSignal(text,"token",parent);
        Assert.NotNull(signal);
        Assert.Equal(handoff,signal!.Value.Handoff);
        Assert.Equal(pid,signal.Value.Pid);
    }

    [Theory]
    [InlineData("other:42")]
    [InlineData("token:43")]
    [InlineData("token:handoff:99:43")]
    [InlineData("token:handoff:42:0")]
    [InlineData("token:handoff:42:-1")]
    [InlineData("token:handoff:42:42")]
    [InlineData("token:handoff:42:wrong")]
    public void WrongTokenParentOrChildCannotConfirmStartup(string text) =>
        Assert.Null(UpdateService.ParseStartupSignal(text,"token",42));
}
