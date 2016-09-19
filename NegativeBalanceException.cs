using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grammophone.Domos.Domain;
using Grammophone.Domos.Domain.Accounting;

namespace Grammophone.Domos.Accounting
{
	/// <summary>
	/// Thrown when a charge cannot take place because of insufficient account balance.
	/// </summary>
	/// <typeparam name="U">
	/// The type of users, derived from <see cref="User"/>.
	/// </typeparam>
	/// <typeparam name="A">The type of accounts, derived from <see cref="Account{U}"/>.</typeparam>
	[Serializable]
	public class NegativeBalanceException<U, A> : AccountingException
		where U : User
		where A : Account<U>
	{
		/// <summary>
		/// Create with default message.
		/// </summary>
		/// <param name="futureBalancesByAccount">
		/// The balances, indexed by account, that would result after the 
		/// execution of the journal, including positive and pathological negative balances.
		/// </param>
		public NegativeBalanceException(IReadOnlyDictionary<A, decimal> futureBalancesByAccount)
			: this(
			futureBalancesByAccount,
			AccountingMessages.INSUFFICIENT_BALANCE)
		{ }

		/// <summary>
		/// Create.
		/// </summary>
		/// <param name="futureBalancesByAccount">
		/// The balances, indexed by account, that would result after the 
		/// execution of the journal, including positive and pathological negative balances.
		/// </param>
		/// <param name="message">The exception message.</param>
		public NegativeBalanceException(IReadOnlyDictionary<A, decimal> futureBalancesByAccount, string message)
			: base(message)
		{
			if (futureBalancesByAccount == null) throw new ArgumentNullException(nameof(futureBalancesByAccount));

			this.FutureBalancesByAccount = futureBalancesByAccount;
		}

		/// <summary>
		/// Used for deserialization.
		/// </summary>
		protected NegativeBalanceException(
		System.Runtime.Serialization.SerializationInfo info,
		System.Runtime.Serialization.StreamingContext context)
			: base(info, context) { }

		/// <summary>
		/// The balances, indexed by account, that would result after the 
		/// execution of the journal, including positive and pathological negative balances.
		/// </summary>
		public IReadOnlyDictionary<A, decimal> FutureBalancesByAccount { get; private set; }
	}
}
