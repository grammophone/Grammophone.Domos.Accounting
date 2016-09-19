using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grammophone.Domos.Accounting
{
	/// <summary>
	/// Thrown when the a journal has already been executed and persisted.
	/// </summary>
	[Serializable]
	public class JournalAlreadyExecutedException : AccountingException
	{
		/// <summary>
		/// Create.
		/// </summary>
		public JournalAlreadyExecutedException()
			: this(AccountingMessages.JOURNAL_ALREADY_EXECUTED)
		{ }

		/// <summary>
		/// Create.
		/// </summary>
		/// <param name="message">The exception message.</param>
		public JournalAlreadyExecutedException(string message) : base(message) { }

		/// <summary>
		/// Used during deserialization.
		/// </summary>
		protected JournalAlreadyExecutedException(
		System.Runtime.Serialization.SerializationInfo info,
		System.Runtime.Serialization.StreamingContext context)
			: base(info, context) { }
	}
}
