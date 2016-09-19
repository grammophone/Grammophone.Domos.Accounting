using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grammophone.Domos.Accounting
{
	/// <summary>
	/// Thrown when the double-entry postings amounts within a journal don't 
	/// sum to zero.
	/// </summary>
	[Serializable]
	public class BalanceException : AccountingException
	{
		/// <summary>
		/// Create.
		/// </summary>
		public BalanceException()
			: this(AccountingMessages.UNBALANCED_POSTINGS)
		{
		}

		/// <summary>
		/// Create.
		/// </summary>
		/// <param name="message">The message of the exception.</param>
		public BalanceException(string message) : base(message) { }

		/// <summary>
		/// Used during serialization.
		/// </summary>
		protected BalanceException(
		System.Runtime.Serialization.SerializationInfo info,
		System.Runtime.Serialization.StreamingContext context)
			: base(info, context) { }
	}
}
