using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Grammophone.DataAccess;
using Grammophone.Domos.Accounting.Models;
using Grammophone.Domos.Domain.Accounting;

namespace Grammophone.Domos.Accounting
{
	/// <summary>
	/// Static class offering extension methods for 
	/// conversion between <see cref="BankAccountInfo"/>
	/// and <see cref="EncryptedBankAccountInfo"/>.
	/// </summary>
	/// <remarks>
	/// The encryption key is specified in the 'accountingEncryptionKey' and the
	/// initialization vector in 'accountingEncryptionIV' entries
	/// of section 'appSettings' in the application's configuration file,
	/// written in base64 format.
	/// </remarks>
	public static class AccountingEncryption
	{
		#region Private fields

		private static readonly Lazy<SymmetricAlgorithm> lazyEncryptionAlgorithm;

		#endregion

		#region Construction

		/// <summary>
		/// Static initialization.
		/// </summary>
		static AccountingEncryption()
		{
			lazyEncryptionAlgorithm = new Lazy<SymmetricAlgorithm>(
				CreateEncryptionAlgorithm, 
				System.Threading.LazyThreadSafetyMode.PublicationOnly);
		}

		#endregion

		#region Public methods

		#region BankAccountInfo

		/// <summary>
		/// Decrypt a <see cref="EncryptedBankAccountInfo"/> into 
		/// a <see cref="BankAccountInfo"/>.
		/// </summary>
		/// <param name="encryptedBankAccountInfo">The bank account info to decrypt.</param>
		public static BankAccountInfo Decrypt(
			this EncryptedBankAccountInfo encryptedBankAccountInfo)
		{
			if (encryptedBankAccountInfo == null) throw new ArgumentNullException(nameof(encryptedBankAccountInfo));

			return new BankAccountInfo
			{
				AccountCode = encryptedBankAccountInfo.AccountCode,
				BankNumber = encryptedBankAccountInfo.BankNumber,
				AccountNumber = DecryptString(encryptedBankAccountInfo.EncryptedAccountNumber),
				TransitNumber = DecryptString(encryptedBankAccountInfo.EncryptedTransitNumber)
			};
		}

		/// <summary>
		/// Encrypt a <see cref="BankAccountInfo"/> into 
		/// an <see cref="EncryptedBankAccountInfo"/>.
		/// </summary>
		/// <param name="bankAccountInfo">The bank account info to encrypt.</param>
		/// <param name="domainContainer">The domain container to create a proxy if necessary.</param>
		/// <returns>
		/// Returns an instance of a proxy to <see cref="EncryptedBankAccountInfo"/>
		/// if <paramref name="domainContainer"/> has <see cref="IDomainContainer.IsProxyCreationEnabled"/>
		/// set to true.
		/// </returns>
		public static EncryptedBankAccountInfo Encrypt(
			this BankAccountInfo bankAccountInfo, IDomainContainer domainContainer)
		{
			if (bankAccountInfo == null) throw new ArgumentNullException(nameof(bankAccountInfo));
			if (domainContainer == null) throw new ArgumentNullException(nameof(domainContainer));

			var encryptedInfo = new EncryptedBankAccountInfo
			{
				BankNumber = bankAccountInfo.BankNumber,
				AccountCode = bankAccountInfo.AccountCode,
				EncryptedAccountNumber = EncryptString(bankAccountInfo.AccountNumber),
				EncryptedTransitNumber = EncryptString(bankAccountInfo.TransitNumber)
			};

			return encryptedInfo;
		}

		/// <summary>
		/// Clone an <see cref="EncryptedBankAccountInfo"/>.
		/// </summary>
		/// <param name="info">The bank account info to clone.</param>
		/// <param name="domainContainer">The domain container to create a proxy if necessary.</param>
		/// <returns>
		/// Returns an instance of a proxy to <see cref="EncryptedBankAccountInfo"/>
		/// if <paramref name="domainContainer"/> has <see cref="IDomainContainer.IsProxyCreationEnabled"/>
		/// set to true.
		/// </returns>
		public static EncryptedBankAccountInfo Clone(
			this EncryptedBankAccountInfo info, IDomainContainer domainContainer)
		{
			if (info == null) throw new ArgumentNullException(nameof(info));
			if (domainContainer == null) throw new ArgumentNullException(nameof(domainContainer));

			var clonedInfo = new EncryptedBankAccountInfo
			{
				AccountCode = info.AccountCode,
				BankNumber = info.BankNumber,
				EncryptedAccountNumber = info.EncryptedAccountNumber,
				EncryptedTransitNumber = info.EncryptedTransitNumber
			};

			return clonedInfo;
		}

		#endregion

		#region string

		/// <summary>
		/// Encrypt a string.
		/// </summary>
		/// <param name="value">The string to be encrypted.</param>
		/// <returns>Returns the byte array of the encrypted string.</returns>
		public static byte[] EncryptString(string value)
		{
			if (value == null) throw new ArgumentNullException(nameof(value));

			var encryptor = lazyEncryptionAlgorithm.Value.CreateEncryptor();

			using (var memoryStream = new MemoryStream())
			{
				using (var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write))
				{
					using (var writer = new StreamWriter(cryptoStream))
					{
						writer.Write(value);
					}

					return memoryStream.ToArray();
				}
			}
		}

		/// <summary>
		/// Decrypt an encrypted string.
		/// </summary>
		/// <param name="encryptedText">The byte array holding the encrypted string.</param>
		/// <returns>Returns the decrypted string.</returns>
		public static string DecryptString(byte[] encryptedText)
		{
			if (encryptedText == null) return null;

			var decryptor = lazyEncryptionAlgorithm.Value.CreateDecryptor();

			using (var memoryStream = new MemoryStream(encryptedText))
			{
				using (var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read))
				{
					using (var reader = new StreamReader(cryptoStream))
					{
						return reader.ReadToEnd();
					}
				}
			}
		}

		#endregion

		#region decimal

		/// <summary>
		/// Encrypt a decimal value.
		/// </summary>
		/// <param name="value">The value to encrypt.</param>
		/// <returns>Returns byte array of for the encrypted value.</returns>
		public static byte[] EncryptDecimal(decimal value)
		{
			var encryptor = lazyEncryptionAlgorithm.Value.CreateEncryptor();

			using (var memoryStream = new MemoryStream())
			{
				using (var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write))
				{
					using (var writer = new BinaryWriter(cryptoStream))
					{
						writer.Write(value);
					}

					return memoryStream.ToArray();
				}
			}
		}

		/// <summary>
		/// Decrypt a decimal value.
		/// </summary>
		/// <param name="encryptedDecimal">The array of bytes holding the encrypted value.</param>
		/// <returns>Returns the decrypted decimal.</returns>
		public static decimal DecryptDecimal(byte[] encryptedDecimal)
		{
			var decryptor = lazyEncryptionAlgorithm.Value.CreateDecryptor();

			using (var memoryStream = new MemoryStream(encryptedDecimal))
			{
				using (var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read))
				{
					using (var reader = new BinaryReader(cryptoStream))
					{
						return reader.ReadDecimal();
					}
				}
			}
		}

		#endregion

		#endregion

		#region Private methods

		/// <summary>
		/// Create the encryption algorithm using the key and initializatio vector
		/// specified in the application's configuration.
		/// </summary>
		/// <returns>Returns a symmetric encryption algorithm</returns>
		/// <remarks>
		/// See the remarks of <see cref="AccountingEncryption"/> for details about 
		/// configuration.
		/// </remarks>
		private static SymmetricAlgorithm CreateEncryptionAlgorithm()
		{
			string base64EncryptionKey = ConfigurationManager.AppSettings["accountingEncryptionKey"];

			if (base64EncryptionKey == null)
				throw new AccountingException(
					"The 'accountingEncryptionKey' entry is missing from appSettings configuration section.");

			string base64EncryptionIV = ConfigurationManager.AppSettings["accountingEncryptionIV"];

			if (base64EncryptionIV == null)
				throw new AccountingException(
					"The 'accountingEncryptionIV' entry is missing from appSettings configuration section.");

			byte[] key;

			try
			{
				key = Convert.FromBase64String(base64EncryptionKey);
			}
			catch (FormatException ex)
			{
				throw new AccountingException(
					"The 'accountingEncryptionKey' value in appSettings is not a valid base64 form.", ex);
			}

			byte[] iv;

			try
			{
				iv = Convert.FromBase64String(base64EncryptionIV);
			}
			catch (FormatException ex)
			{
				throw new AccountingException(
					"The 'accountingEncryptionIV' value in appSettings is not a valid base64 form.", ex);
			}

			var algorithm = new AesManaged();

			algorithm.Key = key;
			algorithm.IV = iv;

			return algorithm;
		}

		#endregion
	}
}
