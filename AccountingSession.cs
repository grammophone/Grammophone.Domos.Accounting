﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using Grammophone.DataAccess;
using Grammophone.Domos.Accounting.Models;
using Grammophone.Domos.DataAccess;
using Grammophone.Domos.Domain;
using Grammophone.Domos.Domain.Accounting;
using Grammophone.Domos.Domain.Workflow;

namespace Grammophone.Domos.Accounting
{
	/// <summary>
	/// An <see cref="IDisposable"/> session for accounting actions. 
	/// CAUTION: All actions taking entities as parameters
	/// should have the entities connected via the <see cref="DomainContainer"/> of the class.
	/// </summary>
	/// <typeparam name="U">
	/// The type of users, derived from <see cref="User"/>.
	/// </typeparam>
	/// <typeparam name="ST">
	/// The type of state transitions, derived from <see cref="StateTransition{U}"/>.
	/// </typeparam>
	/// <typeparam name="A">The type of accounts, derived from <see cref="Account{U}"/>.</typeparam>
	/// <typeparam name="P">The type of the postings, derived from <see cref="Posting{U, A}"/>.</typeparam>
	/// <typeparam name="R">The type of remittances, derived from <see cref="Remittance{U, A}"/>.</typeparam>
	/// <typeparam name="J">
	/// The type of accounting journals, derived from <see cref="Journal{U, ST, A, P, R}"/>.
	/// </typeparam>
	/// <typeparam name="D">The type of domain container for entities.</typeparam>
	public class AccountingSession<U, ST, A, P, R, J, D> : IDisposable
		where U : User
		where ST : StateTransition<U>
		where A : Account<U>
		where P : Posting<U, A>
		where R : Remittance<U, A>
		where J : Journal<U, ST, A, P, R>
		where D : IDomosDomainContainer<U, ST, A, P, R, J>
	{
		#region Private classes

		/// <summary>
		/// Entity listener to set the fields
		/// of entities of type <see cref="ITrackingEntity"/>
		/// or <see cref="IUserTrackingEntity"/>.
		/// </summary>
		private class EntityListener : IUserTrackingEntityListener
		{
			#region Private fields

			/// <summary>
			/// The ID of the acting user.
			/// </summary>
			private long agentID;

			#endregion

			#region Construction

			/// <summary>
			/// Create.
			/// </summary>
			/// <param name="agentID">The ID of the acting user.</param>
			public EntityListener(long agentID)
			{
				this.agentID = agentID;
			}

			#endregion

			#region Public methods

			public void OnAdding(object entity)
			{
				MarkAsModified(entity);
			}

			public void OnChanging(object entity)
			{
				MarkAsModified(entity);
			}

			public void OnDeleting(object entity)
			{
				// NOP.
			}

			public void OnRead(object entity)
			{
				// NOP.
			}

			#endregion

			#region Private methods

			/// <summary>
			/// If the entity is of type <see cref="ITrackingEntity"/>
			/// or <see cref="IUserTrackingEntity"/>, sets the corresponding fields.
			/// </summary>
			/// <param name="entity">The entity to handle.</param>
			private void MarkAsModified(object entity)
			{
				var trackingEntity = entity as ITrackingEntity;

				if (trackingEntity == null) return;

				DateTime now = DateTime.UtcNow;

				trackingEntity.LastModifierUserID = agentID;
				trackingEntity.LastModificationDate = now;

				if (trackingEntity.CreatorUserID == 0L)
				{
					trackingEntity.CreatorUserID = agentID;
				}

				trackingEntity.CreationDate = now;

				var userTrackingEntity = entity as IUserTrackingEntity;

				if (userTrackingEntity != null)
				{
					if (userTrackingEntity.OwningUserID == 0L)
					{
						userTrackingEntity.OwningUserID = agentID;
					}
				}
			}

			#endregion
		}

		#endregion

		#region Private fields

		private EntityListener entityListener;

		#endregion

		#region Construction

		/// <summary>
		/// Create.
		/// If the <paramref name="domainContainer"/> does not 
		/// have a <see cref="IUserTrackingEntityListener"/>,
		/// it will be given one in which the <paramref name="agent"/> will be 
		/// the acting user.
		/// </summary>
		/// <param name="domainContainer">The entities domain container.</param>
		/// <param name="agent">The acting user.</param>
		public AccountingSession(D domainContainer, U agent)
		{
			if (domainContainer == null) throw new ArgumentNullException(nameof(domainContainer));
			if (agent == null) throw new ArgumentNullException(nameof(agent));

			Initialize(domainContainer, agent);
		}

		/// <summary>
		/// Create.
		/// If the <paramref name="domainContainer"/> does not 
		/// have a <see cref="IUserTrackingEntityListener"/>,
		/// it will be given one in which agent specified by <paramref name="agentPickPredicate"/>
		/// will be the acting user.
		/// </summary>
		/// <param name="domainContainer">The entities domain container.</param>
		/// <param name="agentPickPredicate">A predicate to select a user.</param>
		public AccountingSession(D domainContainer, Expression<Func<U, bool>> agentPickPredicate)
		{
			if (domainContainer == null) throw new ArgumentNullException(nameof(domainContainer));
			if (agentPickPredicate == null) throw new ArgumentNullException(nameof(agentPickPredicate));

			U agent = domainContainer.Users.FirstOrDefault(agentPickPredicate);

			if (agent == null)
				throw new ArgumentException("The specified user does not exist.", nameof(agentPickPredicate));

			Initialize(domainContainer, agent);
		}

		#endregion

		#region Public properties

		/// <summary>
		/// The domain container used in the session.
		/// </summary>
		public D DomainContainer { get; private set; }

		/// <summary>
		/// The user operating the session actions.
		/// </summary>
		public U Agent { get; private set; }

		/// <summary>
		/// If true, the accounting session is the owner of <see cref="DomainContainer"/>
		/// and will dispose it upon <see cref="Dispose"/>.
		/// </summary>
		public bool OwnsDomainContainer { get; private set; }

		#endregion

		#region Public methods

		/// <summary>
		/// Restore preexisting entity listeners of <see cref="DomainContainer"/>
		/// and, if <see cref="OwnsDomainContainer"/> is true, dispose it.
		/// </summary>
		public void Dispose()
		{
			if (this.DomainContainer != null)
			{
				this.DomainContainer.EntityListeners.Clear();

				if (entityListener != null)
				{
					this.DomainContainer.EntityListeners.Remove(entityListener);
				}

				if (this.OwnsDomainContainer)
				{
					this.DomainContainer.Dispose();
				}

				this.DomainContainer = default(D);
			}
		}

		#endregion

		#region Public methods

		/// <summary>
		/// Create and persist a funds transfer request.
		/// </summary>
		/// <param name="bankAccountInfo">The bank account info to be encrypted and recorded.</param>
		/// <param name="amount">If positive, the amount to be deposited to the account, else withdrawed.</param>
		/// <param name="creditSystemID">The ID of the credit system.</param>
		/// <param name="utcDate">The time instant in UTC.</param>
		/// <param name="transactionID">The tracking ID of the transaction.</param>
		/// <param name="lineID">Optional tracking ID of the line.</param>
		/// <returns>Returns a persisted request.</returns>
		public async Task<FundsTransferRequest> CreateFundsTransferRequestAsync(
			BankAccountInfo bankAccountInfo,
			decimal amount,
			long creditSystemID,
			DateTime utcDate,
			string transactionID,
			string lineID)
		{
			if (bankAccountInfo == null) throw new ArgumentNullException(nameof(bankAccountInfo));

			var ownEncryptedBankAccountInfo = bankAccountInfo.Encrypt(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				ownEncryptedBankAccountInfo, 
				amount, 
				creditSystemID,
				utcDate, 
				transactionID, 
				lineID);
		}

		/// <summary>
		/// Create and persist a funds transfer request.
		/// </summary>
		/// <param name="bankAccountHolder">An entity holding a bank account.</param>
		/// <param name="amount">If positive, the amount to be deposited to the account, else withdrawed.</param>
		/// <param name="creditSystemID">The ID of the credit system.</param>
		/// <param name="utcDate">The time instant in UTC.</param>
		/// <param name="transactionID">The tracking ID of the transaction.</param>
		/// <param name="lineID">Optional tracking ID of the line.</param>
		/// <returns>Returns a persisted request.</returns>
		public async Task<FundsTransferRequest> CreateFundsTransferRequestAsync(
			IBankAccountHolder bankAccountHolder,
			decimal amount,
			long creditSystemID,
			DateTime utcDate,
			string transactionID,
			string lineID = null)
		{
			if (bankAccountHolder == null) throw new ArgumentNullException(nameof(bankAccountHolder));

			var ownEncryptedBankAccountInfo = bankAccountHolder.EncryptedBankAccountInfo.Clone(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				ownEncryptedBankAccountInfo,
				amount,
				creditSystemID,
				utcDate,
				transactionID,
				lineID);
		}

		/// <summary>
		/// Add an event for a funds tranfer request.
		/// </summary>
		/// <param name="request">The funds tranfer request.</param>
		/// <param name="responseCode">The response code of the event.</param>
		/// <param name="utcDate">The event time, in UTC.</param>
		/// <param name="traceCode">The trace code for the event.</param>
		/// <param name="eventType">The type of the event.</param>
		/// <param name="comments">Optional comments.</param>
		/// <returns>
		/// Returns the created event.
		/// </returns>
		public async Task<FundsTransferEvent> AddFundsTransferEventAsync(
			FundsTransferRequest request,
			string responseCode,
			DateTime utcDate,
			string traceCode,
			FundsTransferEventType eventType,
			string comments = null)
		{
			if (request == null) throw new ArgumentNullException(nameof(request));
			if (responseCode == null) throw new ArgumentNullException(nameof(responseCode));
			if (utcDate.Kind != DateTimeKind.Utc) throw new ArgumentException("Date is not UTC.", nameof(utcDate));
			if (traceCode == null) throw new ArgumentNullException(nameof(traceCode));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				var transferEvent = this.DomainContainer.FundsTransferEvents.Create();

				transferEvent.Comments = comments;
				transferEvent.ResponseCode = responseCode;
				transferEvent.TraceCode = traceCode;
				transferEvent.Type = eventType;
				transferEvent.Date = utcDate;

				transferEvent.Request = request;

				switch (eventType)
				{
					case FundsTransferEventType.Accepted:
						request.State = FundsTransferState.Pending;
						break;

					case FundsTransferEventType.Failed:
						request.State = FundsTransferState.Failed;
						break;

					case FundsTransferEventType.Succeeded:
						request.State = FundsTransferState.Succeeded;
						break;
				}

				await this.DomainContainer.SaveChangesAsync();

				return transferEvent;
			}
		}

		#endregion

		#region Protected methods

		/// <summary>
		/// Execute and persist a fresh journal, which must have not been previously
		/// executed or persisted.
		/// </summary>
		/// <param name="journal">A fresh journal.</param>
		/// <returns>Returns a task completing the action.</returns>
		/// <exception cref="BalanceException">
		/// Thrown when the double-entry postings amounts within the <paramref name="journal"/> 
		/// don't sum to zero.
		/// </exception>
		/// <exception cref="JournalAlreadyExecutedException">
		/// Thrown when the the journal has already been executed and persisted.
		/// </exception>
		protected async Task ExecuteJournalAsync(J journal)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));

			ValidateJournal(journal);

			if (journal.ID > 0)
				throw new JournalAlreadyExecutedException();

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				this.DomainContainer.Journals.Add(journal);

				AmendAccounts(journal.Postings);
				AmendAccounts(journal.Remittances);

				await transaction.CommitAsync();
			}

		}

		/// <summary>
		/// Ensures that all postings amounts within a journal sum to zero.
		/// </summary>
		/// <exception cref="BalanceException">
		/// Thrown when the double-entry postings amounts within the <paramref name="journal"/> 
		/// don't sum to zero.
		/// </exception>
		protected void ValidateJournal(J journal)
		{
			if (journal == null) throw new ArgumentNullException("journal");

			decimal postingsBalance = journal.Postings.Sum(p => p.Amount);

			if (postingsBalance != 0.0M)
				throw new BalanceException();
		}

		/// <summary>
		/// Amend account balances according to a collection of journal lines.
		/// </summary>
		protected void AmendAccounts(IEnumerable<JournalLine<U, A>> journalLines)
		{
			if (journalLines == null) throw new ArgumentNullException(nameof(journalLines));

			foreach (var line in journalLines)
			{
				if (line.ID > 0)
					throw new JournalAlreadyExecutedException(AccountingMessages.JOURNAL_LINE_ALREADY_EXECUTED);

				line.Account.Balance += line.Amount;
			}
		}

		/// <summary>
		/// Ensures that no account will fall to negative balance
		/// after the execution of the journal.
		/// </summary>
		/// <param name="journal">The journal to test.</param>
		/// <exception cref="NegativeBalanceException{U, A}">
		/// Thrown when at least one account balance would turn to negative
		/// if the journal would be executed.
		/// </exception>
		protected void EnsureSufficientBalances(J journal)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));

			IReadOnlyDictionary<A, decimal> futureBalancesByAccount = PredictAccountBalances(journal);

			if (futureBalancesByAccount.Any(entry => entry.Value < 0.0M))
				throw new NegativeBalanceException<U, A>(futureBalancesByAccount);
		}

		/// <summary>
		/// Ensures that no account will fall to negative balance
		/// after the execution of the journal.
		/// </summary>
		/// <param name="journal">The journal to test.</param>
		/// <param name="accountPredicate">
		/// A predicate to select which accounts are tested for negative balance.
		/// </param>
		/// <exception cref="NegativeBalanceException{U, A}">
		/// Thrown when at least one account balance would turn to negative
		/// if the journal would be executed.
		/// </exception>
		protected void EnsureSufficientBalances(J journal, Func<A, bool> accountPredicate)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));
			if (accountPredicate == null) throw new ArgumentNullException(nameof(accountPredicate));

			IReadOnlyDictionary<A, decimal> futureBalancesByAccount = PredictAccountBalances(journal);

			if (futureBalancesByAccount.Any(entry => entry.Value < 0.0M && accountPredicate(entry.Key)))
				throw new NegativeBalanceException<U, A>(futureBalancesByAccount);
		}

		/// <summary>
		/// Predict the account balances if a journal were to be executed.
		/// </summary>
		/// <param name="journal">The prospective journal.</param>
		/// <returns>Returns a dictionary having the accounts as keys and the predicted balances as values.</returns>
		protected IReadOnlyDictionary<A, decimal> PredictAccountBalances(J journal)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));

			var journalLines = new List<JournalLine<U, A>>(journal.Remittances.Count + journal.Postings.Count);

			journalLines.AddRange(journal.Remittances);
			journalLines.AddRange(journal.Postings);

			var futureBalancesByAccount =
					journalLines.Select(jl => jl.Account).Distinct().ToDictionary(a => a, a => a.Balance);

			for (int i = 0; i < journalLines.Count; i++)
			{
				var journalLine = journalLines[i];

				decimal futureAccountBalance = 0.0M;

				futureBalancesByAccount.TryGetValue(journalLine.Account, out futureAccountBalance);

				futureAccountBalance += journalLine.Amount;

				futureBalancesByAccount[journalLine.Account] = futureAccountBalance;
			}

			return futureBalancesByAccount;
		}

		/// <summary>
		/// Create a journal to refer to an entity and 
		/// inherit any owners of it.
		/// </summary>
		/// <param name="entity">The entity being referred, for example, a stateful object.</param>
		/// <returns>
		/// Returns a created but not persisted journal.
		/// </returns>
		protected J CreateJournalForEntity(object entity)
		{
			if (entity == null) throw new ArgumentNullException(nameof(entity));

			var journal = this.DomainContainer.Journals.Create();

			journal.OwningUsers.Add(this.Agent);

			var userEntity = entity as IUserTrackingEntity<U>;

			if (userEntity != null)
			{
				journal.InheritOwnerFrom(userEntity);
			}
			else
			{
				var userGroupEntity = entity as IUserGroupTrackingEntity<U>;

				if (userGroupEntity != null)
				{
					journal.InheritOwnersFrom(userGroupEntity);
				}
			}

			return journal;
		}

		#endregion

		#region Private methods

		private void Initialize(D domainContainer, U agent)
		{
			this.DomainContainer = domainContainer;
			this.Agent = agent;

			// Does the container have user tracking? If not, add our own.
			bool hasUserTracking = 
				domainContainer.EntityListeners.Any(el => el is IUserTrackingEntityListener);

			if (!hasUserTracking)
			{
				entityListener = new EntityListener(agent.ID);
				domainContainer.EntityListeners.Add(entityListener);
			}
		}

		private async Task<FundsTransferRequest> CreateFundsTransferRequestAsync(
			EncryptedBankAccountInfo ownEncryptedBankAccountInfo,
			decimal amount,
			long creditSystemID,
			DateTime utcDate,
			string transactionID,
			string lineID = null)
		{
			if (ownEncryptedBankAccountInfo == null) throw new ArgumentNullException(nameof(ownEncryptedBankAccountInfo));
			if (transactionID == null) throw new ArgumentNullException(nameof(transactionID));
			if (utcDate.Kind != DateTimeKind.Utc) throw new ArgumentException("Date is not UTC.", nameof(utcDate));

			var request = this.DomainContainer.FundsTransferRequests.Create();

			request.Date = utcDate;
			request.Amount = amount;
			request.State = FundsTransferState.Pending;
			request.TransactionID = transactionID;
			request.LineID = lineID;
			request.CreditSystemID = creditSystemID;
			request.EncryptedBankAccountInfo = ownEncryptedBankAccountInfo;

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				this.DomainContainer.FundsTransferRequests.Add(request);

				await transaction.CommitAsync();
			}

			return request;
		}

		#endregion
	}
}
