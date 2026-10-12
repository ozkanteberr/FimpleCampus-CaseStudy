using System.Security.Cryptography;
using System.Text;

namespace AtmCash.Domain;

public sealed class CardIdentity
{
    private readonly byte[] key;

    public CardIdentity(string secret)
    {
        CashRules.Require(!string.IsNullOrEmpty(secret) && Encoding.UTF8.GetByteCount(secret) >= 32,
            "INVALID_CARD_KEY", "Card identity key must contain at least 32 UTF-8 bytes.");
        key = Encoding.UTF8.GetBytes(secret);
    }

    public string Fingerprint(string cardNumber)
    {
        CashRules.Require(!string.IsNullOrWhiteSpace(cardNumber), "INVALID_CARD_NUMBER", "Card number is required.");
        var digits = new StringBuilder(cardNumber.Length);
        foreach (var character in cardNumber)
        {
            if (character is >= '0' and <= '9') digits.Append(character);
            else CashRules.Require(character is ' ' or '-', "INVALID_CARD_NUMBER", "Card number may contain only digits, spaces and hyphens.");
        }
        CashRules.Require(digits.Length is >= 12 and <= 19,
            "INVALID_CARD_NUMBER", "Card number must contain 12 to 19 digits.");
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(digits.ToString())));
    }
}
