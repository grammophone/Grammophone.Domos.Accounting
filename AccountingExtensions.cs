using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grammophone.Domos.Domain.Accounting;

namespace Grammophone.Domos.Accounting
{
	/// <summary>
	/// Extension methods for accounting domain entities.
	/// </summary>
	public static class AccountingExtensions
	{
		#region Private fields

		private static readonly Serialization.FastBinaryFormatter serializationFormatter = new Serialization.FastBinaryFormatter();

		#endregion

		#region Public methods

		/// <summary>
		/// Get the exception stored in <see cref="FundsTransferEvent.ExceptionData"/>
		/// of a funds transfer event,
		/// if any, else return null.
		/// </summary>
		/// <param name="fundsTransferEvent">The funds transfer event.</param>
		/// <returns>
		/// If the <see cref="FundsTransferEvent.ExceptionData"/> is not null,
		/// returns the exception, else returns null.
		/// </returns>
		public static Exception GetException(this FundsTransferEvent fundsTransferEvent)
		{
			if (fundsTransferEvent.ExceptionData != null)
			{
				using (var stream = new System.IO.MemoryStream(fundsTransferEvent.ExceptionData))
				{
					return (Exception)serializationFormatter.Deserialize(stream);
				}
			}
			else
			{
				return null;
			}
		}

		#endregion
	}
}
