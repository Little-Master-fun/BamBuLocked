using PrintGate.Core;
using Xunit;

public sealed class AdministratorAccessTests
{
    [Fact]
    public void EmptyConfigurationDoesNotGrantAccess()
    {
        Assert.False(AdministratorAccess.IsAllowed(new("001","Admin"),[]));
        Assert.False(AdministratorAccess.IsAllowed(new("001","Admin"),null));
        Assert.False(AdministratorAccess.IsAllowed(new("","Admin"),[""]));
    }
    [Fact]
    public void OnlyExactVerifiedStudentIdGrantsAccess()
    {
        Assert.True(AdministratorAccess.IsAllowed(new("001","Name"),[" 001 "]));
        Assert.False(AdministratorAccess.IsAllowed(new("0012","Admin"),["001"]));
        Assert.False(AdministratorAccess.IsAllowed(new("1","Admin"),["001"]));
        Assert.False(AdministratorAccess.IsAllowed(new("other","001"),["001"]));
    }
}
