using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Entity;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using Grammophone.Caching;
using Grammophone.DataAccess;
using Grammophone.Domos.Accounting.Models;
using Grammophone.Domos.DataAccess;
using Grammophone.Domos.Domain;
using Grammophone.Domos.Domain.Accounting;
using Grammophone.Domos.Domain.Workflow;
using Microsoft.Practices.Unity;
using Microsoft.Practices.Unity.Configuration;

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
	/// <typeparam name="BST">
	/// The base type of state transitions, derived from <see cref="StateTransition{U}"/>.
	/// </typeparam>
	/// <typeparam name="A">The type of accounts, derived from <see cref="Account{U}"/>.</typeparam>
	/// <typeparam name="P">The type of the postings, derived from <see cref="Posting{U, A}"/>.</typeparam>
	/// <typeparam name="R">The type of remittances, derived from <see cref="Remittance{U, A}"/>.</typeparam>
	/// <typeparam name="J">
	/// The type of accounting journals, derived from <see cref="Journal{U, ST, A, P, R}"/>.
	/// </typeparam>
	/// <typeparam name="D">The type of domain container for entities.</typeparam>
	public class AccountingSession<U, BST, A, P, R, J, D> : IDisposable
		where U : User
		where BST : StateTransition<U>
		where A : Account<U>
		where P : Posting<U, A>
		where R : Remittance<U, A>
		where J : Journal<U, BST, A, P, R>
		where D : IDomosDomainContainer<U, BST, A, P, R, J>
	{
		#region Constants

		/// <summary>
		/// The size of <see cref="diContainersCache"/>.
		/// </summary>
		private const int DIContainersCacheSize = 4096;

		#endregion

		#region Public classes

		/// <summary>
		/// Result of an accounting action.
		/// </summary>
		/// <remarks>
		/// Use this class in methods as a return type for easy integration with the accounting
		/// workflow actions provided by the Logic layer.
		/// </remarks>
		public class ActionResult
		{
			/// <summary>
			/// If not null, the journal which was recorded.
			/// </summary>
			public J Journal { get; set; }

			/// <summary>
			/// If not null, the funds transfer event which was recorded.
			/// </summary>
			public FundsTransferEvent FundsTransferEvent { get; set; }
		}

		#endregion

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

		/// <summary>
		/// Cache of DI conainers by configuration section names.
		/// </summary>
		private static MRUCache<string, IUnityContainer> diContainersCache;

		/// <summary>
		/// If not null, this entity listener is added to the <see cref="DomainContainer"/>
		/// in case of an absence of another <see cref="IUserTrackingEntityListener"/> in it.
		/// </summary>
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
		/// <param name="configurationSectionName">The element name of a Unity configuration section.</param>
		/// <param name="domainContainer">The entities domain container.</param>
		/// <param name="agent">The acting user.</param>
		public AccountingSession(string configurationSectionName, D domainContainer, U agent)
		{
			if (configurationSectionName == null) throw new ArgumentNullException(nameof(configurationSectionName));
			if (domainContainer == null) throw new ArgumentNullException(nameof(domainContainer));
			if (agent == null) throw new ArgumentNullException(nameof(agent));

			this.ConfigurationSectionName = configurationSectionName;

			this.DIContainer = diContainersCache.Get(configurationSectionName);

			Initialize(domainContainer, agent);
		}

		/// <summary>
		/// Create.
		/// If the <paramref name="domainContainer"/> does not 
		/// have a <see cref="IUserTrackingEntityListener"/>,
		/// it will be given one in which agent specified by <paramref name="agentPickPredicate"/>
		/// will be the acting user.
		/// </summary>
		/// <param name="configurationSectionName">The element name of a Unity configuration section.</param>
		/// <param name="domainContainer">The entities domain container.</param>
		/// <param name="agentPickPredicate">A predicate to select a user.</param>
		public AccountingSession(string configurationSectionName, D domainContainer, Expression<Func<U, bool>> agentPickPredicate)
		{
			if (configurationSectionName == null) throw new ArgumentNullException(nameof(configurationSectionName));
			if (domainContainer == null) throw new ArgumentNullException(nameof(domainContainer));
			if (agentPickPredicate == null) throw new ArgumentNullException(nameof(agentPickPredicate));

			this.ConfigurationSectionName = configurationSectionName;

			this.DIContainer = diContainersCache.Get(configurationSectionName);

			U agent = domainContainer.Users.FirstOrDefault(agentPickPredicate);

			if (agent == null)
				throw new ArgumentException("The specified user does not exist.", nameof(agentPickPredicate));

			Initialize(domainContainer, agent);
		}

		/// <summary>
		/// Create using an own <see cref="DomainContainer"/>
		/// specified in <see cref="DIContainer"/>.
		/// </summary>
		/// <param name="configurationSectionName">The element name of a Unity configuration section.</param>
		/// <param name="agentPickPredicate">A predicate to select a user.</param>
		public AccountingSession(string configurationSectionName, Expression<Func<U, bool>> agentPickPredicate)
		{
			if (configurationSectionName == null) throw new ArgumentNullException(nameof(configurationSectionName));
			if (agentPickPredicate == null) throw new ArgumentNullException(nameof(agentPickPredicate));

			this.ConfigurationSectionName = configurationSectionName;

			this.DIContainer = diContainersCache.Get(configurationSectionName);

			var domainContainer = this.DIContainer.Resolve<D>();

			U agent = domainContainer.Users.FirstOrDefault(agentPickPredicate);

			if (agent == null)
				throw new ArgumentException("The specified user does not exist.", nameof(agentPickPredicate));

			this.OwnsDomainContainer = true;

			Initialize(domainContainer, agent);
		}

		/// <summary>
		/// Static initialization.
		/// </summary>
		static AccountingSession()
		{
			diContainersCache = new MRUCache<string, IUnityContainer>(
				CreateDIContainer,
				DIContainersCacheSize);
		}

		#endregion

		#region Public properties

		/// <summary>
		/// The domain container used in the session.
		/// </summary>
		public D DomainContainer { get; private set; }

		/// <summary>
		/// The name of the configuration section for this accounting session.
		/// </summary>
		public string ConfigurationSectionName { get; private set; }

		/// <summary>
		/// The user operating the session actions.
		/// </summary>
		public U Agent { get; private set; }

		/// <summary>
		/// If true, the accounting session is the owner of <see cref="DomainContainer"/>
		/// and will dispose it upon <see cref="Dispose"/>.
		/// </summary>
		public bool OwnsDomainContainer { get; private set; }

		/// <summary>
		/// Get the <see cref="FundsTransferRequest"/>s which
		/// only have a <see cref="FundsTransferEvent"/> of <see cref="FundsTransferEvent.Type"/>
		/// set as <see cref="FundsTransferEventType.Queued"/>.
		/// </summary>
		public IQueryable<FundsTransferRequest> PendingFundTransferRequests
		{
			get
			{
				return from ftr in this.DomainContainer.FundsTransferRequests
							 let lastEventType = ftr.Events.OrderByDescending(e => e.Date).Select(e => e.Type).FirstOrDefault()
							 where lastEventType == FundsTransferEventType.Queued
							 select ftr;
			}
		}

		#endregion

		#region Protected properties

		/// <summary>
		/// The Unity container dedicated to the accounting session.
		/// </summary>
		protected IUnityContainer DIContainer { get; private set; }

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

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Queued"/>
		/// in it.
		/// </summary>
		/// <param name="bankAccountInfo">The bank account info to be encrypted and recorded.</param>
		/// <param name="amount">If positive, the amount to be deposited to the account, else withdrawed.</param>
		/// <param name="creditSystemID">The ID of the credit system.</param>
		/// <param name="utcDate">The time instant in UTC.</param>
		/// <param name="transactionID">The tracking ID of the transaction.</param>
		/// <param name="batchID">Optional batch ID.</param>
		/// <returns>Returns the event recording the queueing of the request.</returns>
		public async Task<FundsTransferEvent> CreateFundsTransferRequestAsync(
			BankAccountInfo bankAccountInfo,
			decimal amount,
			long creditSystemID,
			DateTime utcDate,
			string transactionID,
			string batchID = null)
		{
			if (bankAccountInfo == null) throw new ArgumentNullException(nameof(bankAccountInfo));

			var ownEncryptedBankAccountInfo = bankAccountInfo.Encrypt(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				ownEncryptedBankAccountInfo,
				amount,
				creditSystemID,
				utcDate,
				transactionID,
				batchID);
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Queued"/>
		/// in it.
		/// </summary>
		/// <param name="bankAccountHolder">An entity holding a bank account.</param>
		/// <param name="amount">If positive, the amount to be deposited to the account, else withdrawed.</param>
		/// <param name="creditSystemID">The ID of the credit system.</param>
		/// <param name="utcDate">The time instant in UTC.</param>
		/// <param name="transactionID">The tracking ID of the transaction.</param>
		/// <param name="batchID">Optional batch ID.</param>
		/// <returns>Returns the event recording the queueing of the request.</returns>
		public async Task<FundsTransferEvent> CreateFundsTransferRequestAsync(
			IBankAccountHolder bankAccountHolder,
			decimal amount,
			long creditSystemID,
			DateTime utcDate,
			string transactionID,
			string batchID = null)
		{
			if (bankAccountHolder == null) throw new ArgumentNullException(nameof(bankAccountHolder));

			var ownEncryptedBankAccountInfo = bankAccountHolder.EncryptedBankAccountInfo.Clone(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				ownEncryptedBankAccountInfo,
				amount,
				creditSystemID,
				utcDate,
				transactionID,
				batchID);
		}

		/// <summary>
		/// Add an event for a funds tranfer request.
		/// </summary>
		/// <param name="request">The funds tranfer request.</param>
		/// <param name="utcDate">The event time, in UTC.</param>
		/// <param name="eventType">The type of the event.</param>
		/// <param name="responseCode">The optinal response code of the event.</param>
		/// <param name="traceCode">The optional trace code for the event.</param>
		/// <param name="comments">Optional comments.</param>
		/// <returns>
		/// Returns the created event.
		/// </returns>
		/// <exception cref="AccountingException">
		/// Thrown when the <paramref name="request"/> already has an event of the
		/// given <paramref name="eventType"/>.
		/// </exception>
		public async Task<FundsTransferEvent> AddFundsTransferEventAsync(
			FundsTransferRequest request,
			DateTime utcDate,
			FundsTransferEventType eventType,
			string responseCode = null,
			string traceCode = null,
			string comments = null)
		{
			if (request == null) throw new ArgumentNullException(nameof(request));
			if (utcDate.Kind != DateTimeKind.Utc) throw new ArgumentException("Date is not UTC.", nameof(utcDate));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				bool typeIsAlreadyAdded = await
					this.DomainContainer.FundsTransferEvents
					.Where(e => e.RequestID == request.ID && e.Type == eventType)
					.AnyAsync();

				if (typeIsAlreadyAdded)
					throw new AccountingException(
						$"An event of type '{eventType}' already exists for request with transaction ID '{request.TransactionID}'.");

				var transferEvent = this.DomainContainer.FundsTransferEvents.Create();

				transferEvent.Comments = comments;
				transferEvent.ResponseCode = responseCode;
				transferEvent.TraceCode = traceCode;
				transferEvent.Type = eventType;
				transferEvent.Date = utcDate;

				transferEvent.Request = request;

				switch (eventType)
				{
					case FundsTransferEventType.Submitted:
					case FundsTransferEventType.Accepted:
						request.State = FundsTransferState.Submitted;
						break;

					case FundsTransferEventType.Failed:
						request.State = FundsTransferState.Failed;
						break;

					case FundsTransferEventType.Succeeded:
						request.State = FundsTransferState.Succeeded;
						break;
				}

				this.DomainContainer.FundsTransferEvents.Add(transferEvent);

				await transaction.CommitAsync();

				return transferEvent;
			}
		}

		/// <summary>
		/// From a set of funds transfer requests, filter those which are pending
		/// a response.
		/// </summary>
		/// <param name="creditSystemID">The credit system of the requests.</param>
		/// <param name="fundsTransferRequestsQuery">The set of requests.</param>
		/// <param name="includeSubmitted">In the results, include requests which are already submitted.</param>
		/// <returns>Returns the set of filtered requests.</returns>
		public IQueryable<FundsTransferRequest> FilterPendingFundsTransferRequests(
			long creditSystemID,
			IQueryable<FundsTransferRequest> fundsTransferRequestsQuery,
			bool includeSubmitted = false)
		{
			if (fundsTransferRequestsQuery == null) throw new ArgumentNullException(nameof(fundsTransferRequestsQuery));

			if (includeSubmitted)
			{
				return from ftr in fundsTransferRequestsQuery
							 let lastEventType = ftr.Events.OrderByDescending(e => e.Date).Select(e => e.Type).FirstOrDefault()
							 where lastEventType == FundsTransferEventType.Queued || lastEventType == FundsTransferEventType.Submitted
							 && ftr.CreditSystemID == creditSystemID
							 select ftr;
			}
			else
			{
				return from ftr in fundsTransferRequestsQuery
							 let lastEventType = ftr.Events.OrderByDescending(e => e.Date).Select(e => e.Type).FirstOrDefault()
							 where lastEventType == FundsTransferEventType.Queued
							 && ftr.CreditSystemID == creditSystemID
							 select ftr;
			}
		}

		/// <summary>
		/// Request withdrawal from a holder of funds.
		/// </summary>
		/// <param name="withdrawableFundsHolder">The holder of funds.</param>
		/// <param name="bankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="amount">The amount to withdraw.</param>
		/// <param name="creditSystemID">The ID of the credit system to transfer funds to.</param>
		/// <param name="utcDate">The date and time, in UTC.</param>
		/// <param name="transactionID">The ID of the transaction of the funds request.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <returns>
		/// Returns the journal which moves the amount to the retaining account of the holder
		/// and the queuing event of the funds transfer request.
		/// </returns>
		public async Task<ActionResult> RequestWithdrawAsync(
			IWithdrawableFundsHolder<U, A> withdrawableFundsHolder,
			BankAccountInfo bankAccountInfo,
			decimal amount,
			long creditSystemID,
			DateTime utcDate,
			string transactionID,
			string batchID = null)
		{
			if (bankAccountInfo == null) throw new ArgumentNullException(nameof(bankAccountInfo));

			var encryptedBankAccountInfo = bankAccountInfo.Encrypt(this.DomainContainer);

			return await RequestWithdrawAsync(
				withdrawableFundsHolder,
				encryptedBankAccountInfo,
				amount,
				creditSystemID,
				utcDate,
				transactionID,
				batchID);
		}

		/// <summary>
		/// Request withdrawal from a holder of funds.
		/// </summary>
		/// <param name="withdrawableFundsHolder">The holder of funds and owner of bank account.</param>
		/// <param name="amount">The amount to withdraw.</param>
		/// <param name="creditSystemID">The ID of the credit system to transfer funds to.</param>
		/// <param name="utcDate">The date and time, in UTC.</param>
		/// <param name="transactionID">The ID of the transaction of the funds request.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <returns>
		/// Returns the journal which moves the amount to the retaining account of the holder
		/// and the queuing event of the funds transfer request.
		/// </returns>
		public async Task<ActionResult> RequestWithdrawAsync(
			IWithdrawableFundsHolderWithBankAccount<U, A> withdrawableFundsHolder,
			decimal amount,
			long creditSystemID,
			DateTime utcDate,
			string transactionID,
			string batchID = null)
		{
			if (withdrawableFundsHolder == null) throw new ArgumentNullException(nameof(withdrawableFundsHolder));

			var bankAccountHolder = withdrawableFundsHolder.BankingDetail;

			if (bankAccountHolder == null)
				throw new ArgumentException(
					"The BankingDetail of the funds holder is not set.",
					nameof(withdrawableFundsHolder));

			var encryptedBankAccountInfo = 
				bankAccountHolder.EncryptedBankAccountInfo.Clone(this.DomainContainer);

			return await RequestWithdrawAsync(
				withdrawableFundsHolder,
				encryptedBankAccountInfo,
				amount,
				creditSystemID,
				utcDate,
				transactionID,
				batchID);
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

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Queued"/>
		/// in it.
		/// </summary>
		/// <param name="ownEncryptedBankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="amount">The amount of the transfer, positive for deposit, negative for withdrawal.</param>
		/// <param name="creditSystemID">The ID of the credit system.</param>
		/// <param name="utcDate">The time in UTC.</param>
		/// <param name="transactionID">The ID of the transaction.</param>
		/// <param name="batchID">Optional ID of the batch.</param>
		/// <returns>Returns the event recording the queueing of the request.</returns>
		private async Task<FundsTransferEvent> CreateFundsTransferRequestAsync(
			EncryptedBankAccountInfo ownEncryptedBankAccountInfo,
			decimal amount,
			long creditSystemID,
			DateTime utcDate,
			string transactionID,
			string batchID = null)
		{
			if (ownEncryptedBankAccountInfo == null) throw new ArgumentNullException(nameof(ownEncryptedBankAccountInfo));
			if (transactionID == null) throw new ArgumentNullException(nameof(transactionID));
			if (utcDate.Kind != DateTimeKind.Utc) throw new ArgumentException("Date is not UTC.", nameof(utcDate));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				var request = this.DomainContainer.FundsTransferRequests.Create();

				request.Amount = amount;
				request.State = FundsTransferState.Pending;
				request.TransactionID = transactionID;
				request.BatchID = batchID;
				request.CreditSystemID = creditSystemID;
				request.EncryptedBankAccountInfo = ownEncryptedBankAccountInfo;

				this.DomainContainer.FundsTransferRequests.Add(request);

				var queueEvent = await AddFundsTransferEventAsync(request, utcDate, FundsTransferEventType.Queued);

				await transaction.CommitAsync();

				return queueEvent;
			}
		}

		/// <summary>
		/// Create a Unity DI container from a configuration section.
		/// </summary>
		/// <param name="configurationSectionName">The element name of the configuratio section.</param>
		/// <returns>Returns the container.</returns>
		private static IUnityContainer CreateDIContainer(string configurationSectionName)
		{
			if (configurationSectionName == null) throw new ArgumentNullException(nameof(configurationSectionName));

			var configurationSection = ConfigurationManager.GetSection(configurationSectionName)
				as UnityConfigurationSection;

			if (configurationSection == null)
				throw new AccountingException($"The '{configurationSectionName}' configuration section is not defined.");

			return new UnityContainer().LoadConfiguration(configurationSection);
		}

		/// <summary>
		/// Request withdrawal from a holder of funds.
		/// </summary>
		/// <param name="withdrawableFundsHolder">The holder of funds.</param>
		/// <param name="ownEncryptedBankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="amount">The amount to withdraw.</param>
		/// <param name="creditSystemID">The ID of the credit system to transfer funds to.</param>
		/// <param name="utcDate">The date and time, in UTC.</param>
		/// <param name="transactionID">The ID of the transaction of the funds request.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <returns>
		/// Returns the journal which moves the amount to the retaining account of the holder
		/// and the queuing event of the funds transfer request.
		/// </returns>
		private async Task<ActionResult> RequestWithdrawAsync(
			IWithdrawableFundsHolder<U, A> withdrawableFundsHolder,
			EncryptedBankAccountInfo ownEncryptedBankAccountInfo,
			decimal amount,
			long creditSystemID,
			DateTime utcDate,
			string transactionID,
			string batchID = null)
		{
			if (withdrawableFundsHolder == null) throw new ArgumentNullException(nameof(withdrawableFundsHolder));
			if (ownEncryptedBankAccountInfo == null) throw new ArgumentNullException(nameof(ownEncryptedBankAccountInfo));
			if (transactionID == null) throw new ArgumentNullException(nameof(transactionID));
			if (utcDate.Kind != DateTimeKind.Utc) throw new ArgumentException("The date is not UTC", nameof(utcDate));
			if (amount <= 0.0M) throw new ArgumentException("The amount must be positive.", nameof(amount));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				var journal = CreateJournalForEntity(withdrawableFundsHolder);
				this.DomainContainer.Journals.Add(journal);

				journal.Description = AccountingMessages.WITHDRAWAL_DESCRIPTION;

				var moveFromMainAccountPosting = this.DomainContainer.Postings.Create();
				journal.Postings.Add(moveFromMainAccountPosting);

				moveFromMainAccountPosting.InheritOwnersFrom(journal);
				moveFromMainAccountPosting.Amount = -amount;
				moveFromMainAccountPosting.Account = withdrawableFundsHolder.MainAccount;
				moveFromMainAccountPosting.Description = AccountingMessages.MOVE_AMOUNT_FROM_MAIN;

				var moveToRetainingAccountPosting = this.DomainContainer.Postings.Create();
				journal.Postings.Add(moveToRetainingAccountPosting);

				moveToRetainingAccountPosting.InheritOwnersFrom(journal);
				moveToRetainingAccountPosting.Amount = amount;
				moveToRetainingAccountPosting.Account = withdrawableFundsHolder.RetainingAccount;
				moveToRetainingAccountPosting.Description = AccountingMessages.MOVE_AMOUNT_TO_RETAINING;

				EnsureSufficientBalances(journal);

				await ExecuteJournalAsync(journal);

				var fundsTransferQueuingEvent = await CreateFundsTransferRequestAsync(
					ownEncryptedBankAccountInfo,
					-amount,
					creditSystemID,
					utcDate,
					transactionID,
					batchID);

				return new ActionResult
				{
					Journal = journal,
					FundsTransferEvent = fundsTransferQueuingEvent
				};
			}
		}

		#endregion
	}
}
