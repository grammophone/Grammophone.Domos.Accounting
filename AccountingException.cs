using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grammophone.Domos.Accounting
{
	/// <summary>
	/// Thrown due to an error in the accounting system.
	/// </summary>
	[Serializable]
	public class AccountingException : Exception
	{
		/// <summary>
		/// Create.
		/// </summary>
		/// <param name="message">The exception message.</param>
		public AccountingException(string message) : base(message) { }

		/// <summary>
		/// Create.
		/// </summary>
		/// <param name="message">The exception message.</param>
		/// <param name="inner">The inner exception cause.</param>
		public AccountingException(string message, Exception inner) : base(message, inner) { }

		/// <summary>
		/// Used for serialization.
		/// </summary>
		protected AccountingException(
		System.Runtime.Serialization.SerializationInfo info,
		System.Runtime.Serialization.StreamingContext context)
			: base(info, context) { }
	}
}
