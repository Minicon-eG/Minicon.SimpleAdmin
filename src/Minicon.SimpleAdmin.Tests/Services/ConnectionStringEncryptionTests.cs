using FluentAssertions;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Tests.Services;

public class ConnectionStringEncryptionTests
{
    // A valid 32-byte key in Base64 (used throughout all tests)
    private const string ValidKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="; // 32 zero bytes

    // ── IsEncrypted ──────────────────────────────────────────────────────────

    [Fact]
    public void IsEncrypted_WithEncPrefix_ReturnsTrue()
    {
        ConnectionStringEncryption.IsEncrypted("enc:v1:abc.def.ghi").Should().BeTrue();
    }

    [Fact]
    public void IsEncrypted_WithPlaintext_ReturnsFalse()
    {
        ConnectionStringEncryption.IsEncrypted("Server=sql01;Database=test;").Should().BeFalse();
    }

    [Fact]
    public void IsEncrypted_WithNull_ReturnsFalse()
    {
        ConnectionStringEncryption.IsEncrypted(null).Should().BeFalse();
    }

    [Fact]
    public void IsEncrypted_WithEmptyString_ReturnsFalse()
    {
        ConnectionStringEncryption.IsEncrypted("").Should().BeFalse();
    }

    // ── Encrypt / Decrypt round-trip ─────────────────────────────────────────

    [Fact]
    public void RoundTrip_StandardConnectionString_PreservesValue()
    {
        const string plaintext = "Server=sql01;Database=BizTalkMgmtDb;Integrated Security=true;";

        var encrypted = ConnectionStringEncryption.Encrypt(plaintext, ValidKey);
        var decrypted = ConnectionStringEncryption.Decrypt(encrypted, ValidKey);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void RoundTrip_EmptyString_PreservesValue()
    {
        var encrypted = ConnectionStringEncryption.Encrypt("", ValidKey);
        ConnectionStringEncryption.Decrypt(encrypted, ValidKey).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Server=.;Trusted_Connection=True;")]
    [InlineData("Data Source=mydb.sqlite")]
    [InlineData("host=localhost;port=5432;database=mydb;username=user;password=p@ss!word123")]
    public void RoundTrip_VariousConnectionStringFormats_PreservesValue(string plaintext)
    {
        var encrypted = ConnectionStringEncryption.Encrypt(plaintext, ValidKey);
        ConnectionStringEncryption.Decrypt(encrypted, ValidKey).Should().Be(plaintext);
    }

    // ── Non-determinism ──────────────────────────────────────────────────────

    [Fact]
    public void Encrypt_SamePlaintext_ProducesDifferentCiphertexts()
    {
        const string plaintext = "Server=sql01;Database=test;";

        var first  = ConnectionStringEncryption.Encrypt(plaintext, ValidKey);
        var second = ConnectionStringEncryption.Encrypt(plaintext, ValidKey);

        // Random salt + IV means each encryption produces a distinct ciphertext
        first.Should().NotBe(second);
    }

    [Fact]
    public void Encrypt_OutputStartsWithPrefix()
    {
        var encrypted = ConnectionStringEncryption.Encrypt("test", ValidKey);
        encrypted.Should().StartWith("enc:v1:");
    }

    [Fact]
    public void Encrypt_OutputContainsThreeSegments()
    {
        var encrypted = ConnectionStringEncryption.Encrypt("test", ValidKey);
        var payload = encrypted["enc:v1:".Length..];
        payload.Split('.').Should().HaveCount(3);
    }

    // ── Decrypt passthrough for plain-text values ────────────────────────────

    [Fact]
    public void Decrypt_Plaintext_ReturnsUnchanged()
    {
        const string plaintext = "Server=sql01;Database=test;";
        ConnectionStringEncryption.Decrypt(plaintext, ValidKey).Should().Be(plaintext);
    }

    // ── Error handling ───────────────────────────────────────────────────────

    [Fact]
    public void Decrypt_WrongKey_ThrowsCryptographicException()
    {
        const string plaintext = "Server=sql01;Database=test;";
        string wrongKey = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE="; // different bytes

        var encrypted = ConnectionStringEncryption.Encrypt(plaintext, ValidKey);

        var act = () => ConnectionStringEncryption.Decrypt(encrypted, wrongKey);
        act.Should().Throw<Exception>(); // CryptographicException or padding error
    }

    [Fact]
    public void Decrypt_TwoSegmentsOnly_ThrowsFormatException()
    {
        var act = () => ConnectionStringEncryption.Decrypt("enc:v1:seg1.seg2", ValidKey);
        act.Should().Throw<FormatException>()
           .WithMessage("*3 segments*");
    }

    [Fact]
    public void Decrypt_FourSegments_ThrowsFormatException()
    {
        var act = () => ConnectionStringEncryption.Decrypt("enc:v1:a.b.c.d", ValidKey);
        act.Should().Throw<FormatException>();
    }
}
