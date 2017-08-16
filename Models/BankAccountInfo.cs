using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Grammophone.DataAccess;
using Grammophone.Domos.Domain.Accounting;

namespace Grammophone.Domos.Accounting.Models
{
	/// <summary>
	/// Unencrypted bank account specification. Conversions between
	/// instances of this class and <see cref="EncryptedBankAccountInfo"/> can be performed
	/// via <see cref="AccountingEncryption.Decrypt(EncryptedBankAccountInfo)"/>
	/// and <see cref="AccountingEncryption.Encrypt(BankAccountInfo, IDomainContainer)"/>.
	/// </summary>
	[Serializable]
	public class BankAccountInfo
	{
		/// <summary>
		/// The account number.
		/// </summary>
		[Required]
		[MaxLength(50)]
		[XmlAttribute]
		[Display(
			Name = nameof(BankAccountInfoResources.AccountNumber_Name),
			ResourceType = typeof(BankAccountInfoResources))]
		public virtual string AccountNumber { get; set; }

		/// <summary>
		/// The bank number, used in Canada.
		/// </summary>
		[MaxLength(6)]
		[XmlAttribute]
		[Display(
			Name = nameof(BankAccountInfoResources.BankNumber_Name),
			ResourceType = typeof(BankAccountInfoResources))]
		public virtual string BankNumber { get; set; }

		/// <summary>
		/// The transit number, specified in the cheque.
		/// </summary>
		[MaxLength(16)]
		[XmlAttribute]
		[Display(
			Name = nameof(BankAccountInfoResources.TransitNumber_Name),
			ResourceType = typeof(BankAccountInfoResources))]
		public virtual string TransitNumber { get; set; }

		/// <summary>
		/// Account code, used in United states.
		/// </summary>
		[MaxLength(6)]
		[XmlAttribute]
		[Display(
			Name = nameof(BankAccountInfoResources.AccountCode_Name),
			ResourceType = typeof(BankAccountInfoResources))]
		public virtual string AccountCode { get; set; }
	}
}
