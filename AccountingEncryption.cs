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

		private static Lazy<SymmetricAlgorithm> lazyEncryptionAlgorithm;

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

		/// <summary>
		/// Decrypt a <see cref="EncryptedBankAccountInfo"/> into 
		/// a <see cref="BankAccountInfo"/>.
		/// </summary>
		/// <param name="encryptedBankAccountInfo">The bank account info to decrypt.</param>
		public static BankAccountInfo Decrypt(
			this EncryptedBankAccountInfo encryptedBankAccountInfo)
		{
			if (encryptedBankAccountInfo == null) throw new ArgumentNullException(nameof(encryptedBankAccountInfo));

			var algorithm = lazyEncryptionAlgorithm.Value;

			return new BankAccountInfo
			{
				AccountCode = encryptedBankAccountInfo.AccountCode,
				BankNumber = encryptedBankAccountInfo.BankNumber,
				AccountNumber = DecryptText(algorithm, encryptedBankAccountInfo.EncryptedAccountNumber),
				TransitNumber = DecryptText(algorithm, encryptedBankAccountInfo.EncryptedTransitNumber)
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

			var algorithm = lazyEncryptionAlgorithm.Value;

			var encryptedInfo = domainContainer.Create<EncryptedBankAccountInfo>();

			encryptedInfo.BankNumber = bankAccountInfo.BankNumber;
			encryptedInfo.AccountCode = bankAccountInfo.AccountCode;
			encryptedInfo.EncryptedAccountNumber = EncryptText(algorithm, bankAccountInfo.AccountNumber);
			encryptedInfo.EncryptedTransitNumber = EncryptText(algorithm, bankAccountInfo.TransitNumber);

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

			var clonedInfo = domainContainer.Create<EncryptedBankAccountInfo>();

			clonedInfo.AccountCode = info.AccountCode;
			clonedInfo.BankNumber = info.BankNumber;
			clonedInfo.EncryptedAccountNumber = info.EncryptedAccountNumber;
			clonedInfo.EncryptedTransitNumber = info.EncryptedTransitNumber;

			return clonedInfo;
		}

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

		/// <summary>
		/// Encrypt a <paramref name="text"/> using an encryption <paramref name="algorithm"/>.
		/// </summary>
		private static byte[] EncryptText(SymmetricAlgorithm algorithm, string text)
		{
			if (text == null) throw new ArgumentNullException(nameof(text));

			var encryptor = algorithm.CreateEncryptor();

			using (var memoryStream = new MemoryStream())
			{
				using (var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write))
				{
					using (var writer = new StreamWriter(cryptoStream))
					{
						writer.Write(text);
					}

					return memoryStream.ToArray();
				}
			}
		}

		/// <summary>
		/// Decrypt an <paramref name="encryptedText"/> byte array
		/// using an encryption <paramref name="algorithm"/>.
		/// </summary>
		private static string DecryptText(SymmetricAlgorithm algorithm, byte[] encryptedText)
		{
			if (encryptedText == null) throw new ArgumentNullException(nameof(encryptedText));

			var decryptor = algorithm.CreateDecryptor();

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
	}
}
