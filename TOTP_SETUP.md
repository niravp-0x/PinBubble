# TOTP (Time-based One-Time Password) Support in PinBubble

## Overview

PinBubble now supports TOTP (Time-based One-Time Password) tokens, allowing you to store and generate rotating one-time passwords for each entry alongside your passwords. This is particularly useful when you have FreeIPA TOTP tokens set up.

## Setup

### FreeIPA TOTP Setup

If you have FreeIPA configured with TOTP tokens, you can link them to PinBubble entries:

```bash
# Add a TOTP token to your FreeIPA user
ipa otptoken-add \
  --type=totp \
  --description="Phone Authenticator"
```

The FreeIPA setup will provide you with:
- A **base32-encoded secret** (e.g., `JBSWY3DPEBLW64TMMQ======`)
- A **QR code** to scan with your authenticator app

### Configuring TOTP in PinBubble

To add TOTP to a PinBubble entry:

1. **Get your TOTP secret** from FreeIPA or your authenticator setup
2. **Edit the JSON storage** - Add the `totpSecret` field to your entry:

```json
[
  {
    "label": "My FreeIPA Account",
    "value": "my-password",
    "totpSecret": "JBSWY3DPEBLW64TMMQ======",
    "created": "2026-08-31T10:00:00Z",
    "modified": "2026-08-31T10:00:00Z",
    "expiryDate": "2026-09-30T10:00:00Z"
  }
]
```

## Usage

### Getting Current TOTP Code

Each entry with a `TotpSecret` will automatically generate a current 6-digit code that rotates every 30 seconds.

```csharp
// Access the current TOTP code in your application
var row = snippets[0]; // Your SnippetRow
var totpCode = row.CurrentTotp; // Returns "123456" or null if no secret
```

### TOTP with Expiry Information

Get the TOTP code along with how many seconds until it changes:

```csharp
var totpInfo = row.TotpWithExpiry; // Returns ("123456", 15) - 15 seconds remaining
if (totpInfo.HasValue)
{
    var code = totpInfo.Value.Code;
    var remainingSeconds = totpInfo.Value.RemainingSeconds;
}
```

### Verifying TOTP Codes

Verify that a user-entered code is valid:

```csharp
bool isValid = TotpProvider.VerifyTotp(row.TotpSecret, userEnteredCode);
```

### Generating QR Codes

Generate a provisioning URI for creating QR codes to share with authenticator apps:

```csharp
var uri = row.GetTotpProvisioningUri("MyCompany");
// Returns: otpauth://totp/MyCompany:My%20FreeIPA%20Account?secret=JBSWY3DPEBLW64TMMQ======&issuer=MyCompany
```

Use an online tool or QR code library to convert the URI to a QR code.

## Security Considerations

1. **Secret Encryption**: TOTP secrets are stored in the encrypted storage alongside your passwords. They are subject to the same encryption as your password values.

2. **Base32 Encoding**: Secrets are stored in base32 format, which is the standard for TOTP. Never share your secret with anyone.

3. **Time Sync**: TOTP relies on accurate system time. Ensure your device's clock is synchronized with NTP.

4. **Backup**: Store your TOTP secrets securely. If you lose access to your storage, you won't be able to generate codes until you reset the tokens in FreeIPA.

5. **Verification Window**: The TOTP verifier allows for ±1 time step tolerance to account for minor clock drift.

## Technical Details

- **Algorithm**: HMAC-SHA1 (RFC 6238 compliant)
- **Time Step**: 30 seconds (standard)
- **Digits**: 6-digit codes
- **Library**: OtpNet for TOTP generation
- **Format**: Base32-encoded secrets (standard for authenticator apps)

## Example

Given a FreeIPA TOTP secret:

```
Secret: JBSWY3DPEBLW64TMMQ======
Time: 15:45:30 UTC

Generated Code: 123456 (valid for 10 more seconds)
```

At 15:46:00 UTC, a new code will be generated automatically.

## Integration with Authenticator Apps

The TOTP secrets are compatible with:
- Google Authenticator
- Microsoft Authenticator
- Authy
- FreeOTP
- Any RFC 6238 compliant TOTP application
