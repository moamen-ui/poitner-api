using Pointer.Infrastructure.Auth;
using Xunit;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_then_Verify_roundtrips()
    {
        var h = new BcryptPasswordHasher();
        var hash = h.Hash("s3cret!");
        Assert.True(h.Verify("s3cret!", hash));
        Assert.False(h.Verify("wrong", hash));
    }
}
