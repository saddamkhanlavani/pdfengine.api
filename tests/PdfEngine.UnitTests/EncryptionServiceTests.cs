using Xunit;
using PdfEngine.Infrastructure.Services;

namespace PdfEngine.UnitTests;

public class EncryptionServiceTests
{
    [Fact]
    public void EncryptAndDecrypt_ShouldReturnOriginalText()
    {
        // Arrange
        var service = new EncryptionService();
        var originalText = "Hello, this is a secret rendering payload for PDFEngine!";

        // Act
        var encrypted = service.Encrypt(originalText);
        var decrypted = service.Decrypt(encrypted);

        // Assert
        Assert.NotEqual(originalText, encrypted);
        Assert.Equal(originalText, decrypted);
    }

    [Fact]
    public void Encrypt_EmptyString_ShouldReturnOriginalString()
    {
        // Arrange
        var service = new EncryptionService();

        // Act
        var encrypted = service.Encrypt("");
        var decrypted = service.Decrypt("");

        // Assert
        Assert.Equal("", encrypted);
        Assert.Equal("", decrypted);
    }
}
