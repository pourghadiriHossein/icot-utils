namespace ICOT.Core.Utils.Services;

/// <summary>
/// Static facade for <see cref="IEncryptionService"/> providing backward compatibility across existing services.
/// </summary>
public static class EncryptionHelper
{
    private static IEncryptionService _service = AesEncryptionService.Instance;

    public static void Configure(IEncryptionService service)
    {
        _service = service ?? AesEncryptionService.Instance;
    }

    public static string Encrypt(string plainText, string? key = null) => _service.Encrypt(plainText, key);

    public static string Decrypt(string cipherText, string? key = null) => _service.Decrypt(cipherText, key);
}
