using Xunit;
using PdfEngine.Infrastructure.Security;

namespace PdfEngine.UnitTests;

public class HashHelperTests
{
    [Fact]
    public void ComputeSha256Hash_ShouldReturnCorrectHash()
    {
        // Arrange
        var rawData = "test-api-key-123";
        
        // Act
        var hash = HashHelper.ComputeSha256Hash(rawData);
        
        // Assert
        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length);
    }
}
