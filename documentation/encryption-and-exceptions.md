# Encryption And Exceptions

`AccountingEncryption` encrypts and decrypts banking details and primitive values using configuration-provided cryptographic settings.

`BankAccountInfo` is the plain model used by accounting operations. `EncryptedBankAccountInfo` is the domain value object stored in funds transfer request groups.

Accounting exceptions include balance-related exceptions, negative-balance exceptions, journal execution exceptions and general accounting exceptions.

Funds transfer events can store serialized exception data. `AccountingExtensions.GetException` and accounting-session helpers can reconstruct exception information when reviewing transfer processing failures.
