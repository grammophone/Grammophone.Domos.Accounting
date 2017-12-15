using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Entity;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using Grammophone.Caching;
using Grammophone.Domos.Accounting.Models;
using Grammophone.Domos.DataAccess;
using Grammophone.Domos.Domain;
using Grammophone.Domos.Domain.Accounting;
using Grammophone.Domos.Domain.Workflow;
using Grammophone.Setup;

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
	/// <typeparam name="P">The type of the postings, derived from <see cref="Posting{U}"/>.</typeparam>
	/// <typeparam name="R">The type of remittances, derived from <see cref="Remittance{U}"/>.</typeparam>
	/// <typeparam name="J">
	/// The type of accounting journals, derived from <see cref="Journal{U, ST, P, R}"/>.
	/// </typeparam>
	/// <typeparam name="D">The type of domain container for entities.</typeparam>
	public class AccountingSession<U, BST, P, R, J, D> : IDisposable
		where U : User
		where BST : StateTransition<U>
		where P : Posting<U>
		where R : Remittance<U>
		where J : Journal<U, BST, P, R>
		where D : IDomosDomainContainer<U, BST, P, R, J>
	{
		#region Constants

		/// <summary>
		/// The size of the cache of <see cref="settingsFactory"/>.
		/// </summary>
		private const int SettingsCacheSize = 2048;

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
			/// If not null, the journal which was executed.
			/// Do not append postings or remittances to it.
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

				if (entity is IUserTrackingEntity userTrackingEntity)
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
		/// Cache of settings by configuration section names.
		/// </summary>
		private static SettingsFactory settingsFactory = new SettingsFactory(SettingsCacheSize);

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

			this.Settings = settingsFactory.Get(configurationSectionName);

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

			this.Settings = settingsFactory.Get(configurationSectionName);

			U agent = domainContainer.Users.FirstOrDefault(agentPickPredicate);

			if (agent == null)
				throw new ArgumentException("The specified user does not exist.", nameof(agentPickPredicate));

			Initialize(domainContainer, agent);
		}

		/// <summary>
		/// Create using an own <see cref="DomainContainer"/>
		/// specified in <see cref="Settings"/>.
		/// </summary>
		/// <param name="configurationSectionName">The element name of a Unity configuration section.</param>
		/// <param name="agentPickPredicate">A predicate to select a user.</param>
		public AccountingSession(string configurationSectionName, Expression<Func<U, bool>> agentPickPredicate)
		{
			if (configurationSectionName == null) throw new ArgumentNullException(nameof(configurationSectionName));
			if (agentPickPredicate == null) throw new ArgumentNullException(nameof(agentPickPredicate));

			this.ConfigurationSectionName = configurationSectionName;

			this.Settings = settingsFactory.Get(configurationSectionName);

			var domainContainer = this.Settings.Resolve<D>();

			U agent = domainContainer.Users.FirstOrDefault(agentPickPredicate);

			if (agent == null)
				throw new ArgumentException("The specified user does not exist.", nameof(agentPickPredicate));

			this.OwnsDomainContainer = true;

			Initialize(domainContainer, agent);
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
		/// set as <see cref="FundsTransferEventType.Pending"/>.
		/// </summary>
		public IQueryable<FundsTransferRequest> PendingFundTransferRequests
		{
			get
			{
				return from ftr in this.DomainContainer.FundsTransferRequests
							 let lastEventType = ftr.Events.OrderByDescending(e => e.Date).Select(e => e.Type).FirstOrDefault()
							 where lastEventType == FundsTransferEventType.Pending
							 select ftr;
			}
		}

		#endregion

		#region Protected properties

		/// <summary>
		/// The Unity container dedicated to the accounting session.
		/// </summary>
		protected Settings Settings { get; private set; }

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
		/// Create and persist a batch for funds transfer requests.
		/// </summary>
		/// <param name="creditSystem">The credit system which will serve the batch.</param>
		/// <returns>Returns the created and persisted batch.</returns>
		public async Task<FundsTransferBatch> CreateFundsTransferBatchAsync(CreditSystem creditSystem)
		{
			if (creditSystem == null) throw new ArgumentNullException(nameof(creditSystem));

			var batch = this.DomainContainer.FundsTransferBatches.Create();
			this.DomainContainer.FundsTransferBatches.Add(batch);

			batch.ID = Guid.NewGuid();
			batch.CreditSystem = creditSystem;

			await this.DomainContainer.SaveChangesAsync();

			return batch;
		}

		/// <summary>
		/// Create and persist a collation for funds transfer events.
		/// </summary>
		/// <param name="collationID">The ID of the collation.</param>
		/// <returns>Returns the created and persisted collation.</returns>
		public async Task<FundsTransferEventCollation> CreateFundsTransferEventCollationAsync(Guid collationID)
		{
			var collation = this.DomainContainer.FundsTransferEventCollations.Create();
			this.DomainContainer.FundsTransferEventCollations.Add(collation);

			collation.ID = collationID;

			await this.DomainContainer.SaveChangesAsync();

			return collation;
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="bankAccountInfo">The bank account info to be encrypted and recorded.</param>
		/// <param name="amount">If positive, the amount to be deposited to the account, else withdrawed.</param>
		/// <param name="mainAccount">The main account being charged.</param>
		/// <param name="escrowAccount">The escrow account for holding outgoing funds.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID.</param>
		/// <param name="queueEventCollationID">The optional ID of the collation of queuing event being generated.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			BankAccountInfo bankAccountInfo,
			decimal amount,
			Account mainAccount,
			Account escrowAccount,
			Func<J, Task> asyncJournalAppendAction,
			Guid? batchID = null,
			Guid? queueEventCollationID = null)
		{
			if (bankAccountInfo == null) throw new ArgumentNullException(nameof(bankAccountInfo));

			var ownEncryptedBankAccountInfo = bankAccountInfo.Encrypt(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				ownEncryptedBankAccountInfo,
				amount,
				mainAccount,
				escrowAccount,
				asyncJournalAppendAction,
				batchID);
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="bankAccountHolder">An entity holding a bank account.</param>
		/// <param name="amount">If positive, the amount to be deposited to the account, else withdrawed.</param>
		/// <param name="mainAccount">The main account being charged.</param>
		/// <param name="escrowAccount">The escrow account for holding outgoing funds.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID.</param>
		/// <param name="queueEventCollationID">The optional ID of the collation of queuing event being generated.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			IBankAccountHolder bankAccountHolder,
			decimal amount,
			Account mainAccount,
			Account escrowAccount,
			Func<J, Task> asyncJournalAppendAction,
			Guid? batchID = null,
			Guid? queueEventCollationID = null)
		{
			if (bankAccountHolder == null) throw new ArgumentNullException(nameof(bankAccountHolder));

			var ownEncryptedBankAccountInfo = bankAccountHolder.EncryptedBankAccountInfo.Clone(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				ownEncryptedBankAccountInfo,
				amount,
				mainAccount,
				escrowAccount,
				asyncJournalAppendAction,
				batchID,
				queueEventCollationID);
		}

		/// <summary>
		/// Request withdrawal from a holder of funds.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds.</param>
		/// <param name="bankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="amount">The amount to withdraw.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="queueEventCollationID">The optional ID of the collation of queuing event being generated.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolder transferableFundsHolder,
			BankAccountInfo bankAccountInfo,
			decimal amount,
			Func<J, Task> asyncJournalAppendAction,
			Guid? batchID = null,
			Guid? queueEventCollationID = null)
		{
			if (bankAccountInfo == null) throw new ArgumentNullException(nameof(bankAccountInfo));

			var encryptedBankAccountInfo = bankAccountInfo.Encrypt(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				transferableFundsHolder,
				encryptedBankAccountInfo,
				amount,
				asyncJournalAppendAction,
				batchID,
				queueEventCollationID);
		}

		/// <summary>
		/// Request withdrawal from a holder of funds.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds.</param>
		/// <param name="bankAccountHolder">A holder of a bank account to be assigned to the request.</param>
		/// <param name="amount">The amount to withdraw.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="queueEventCollationID">The optional ID of the collation of queuing event being generated.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolder transferableFundsHolder,
			IBankAccountHolder bankAccountHolder,
			decimal amount,
			Func<J, Task> asyncJournalAppendAction,
			Guid? batchID = null,
			Guid? queueEventCollationID = null)
		{
			if (bankAccountHolder == null) throw new ArgumentNullException(nameof(bankAccountHolder));

			var encryptedBankAccountInfo = bankAccountHolder.EncryptedBankAccountInfo.Clone(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				transferableFundsHolder,
				encryptedBankAccountInfo,
				amount,
				asyncJournalAppendAction,
				batchID,
				queueEventCollationID);
		}

		/// <summary>
		/// Request withdrawal from a holder of funds.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds and owner of bank account.</param>
		/// <param name="amount">The amount to withdraw.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="queueEventCollationID">The optional ID of the collation of queuing event being generated.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolderWithBankAccount transferableFundsHolder,
			decimal amount,
			Func<J, Task> asyncJournalAppendAction,
			Guid? batchID = null,
			Guid? queueEventCollationID = null)
		{
			if (transferableFundsHolder == null) throw new ArgumentNullException(nameof(transferableFundsHolder));

			var bankAccountHolder = transferableFundsHolder.BankingDetail;

			if (bankAccountHolder == null)
				throw new ArgumentException(
					"The BankingDetail of the funds holder is not set.",
					nameof(transferableFundsHolder));

			var encryptedBankAccountInfo =
				bankAccountHolder.EncryptedBankAccountInfo.Clone(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				transferableFundsHolder,
				encryptedBankAccountInfo,
				amount,
				asyncJournalAppendAction,
				batchID,
				queueEventCollationID);
		}

		/// <summary>
		/// Add an event for a funds tranfer request.
		/// </summary>
		/// <param name="request">The funds tranfer request.</param>
		/// <param name="utcDate">The event time, in UTC.</param>
		/// <param name="eventType">The type of the event.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="collationID">Optional ID of the event collation.</param>
		/// <param name="responseCode">The optinal response code of the event.</param>
		/// <param name="traceCode">The optional trace code for the event.</param>
		/// <param name="comments">Optional comments.</param>
		/// <param name="exception">Optional exception to record in the event.</param>
		/// <returns>
		/// Returns an action holding the created event
		/// and optionally any journal executed because of the event.
		/// </returns>
		/// <remarks>
		/// For other event type other than <see cref="FundsTransferEventType.Pending"/>,
		/// the funds transfer <paramref name="request"/> must have been enlisted under a batch,
		/// ie its <see cref="FundsTransferRequest.Batch"/> property must not be null.
		/// </remarks>
		/// <exception cref="AccountingException">
		/// Thrown when the <paramref name="request"/> already has an event of the
		/// given <paramref name="eventType"/>.
		/// </exception>
		/// <exception cref="ArgumentException">
		/// Thrown when the event type is other than <see cref="FundsTransferEventType.Pending"/>
		/// and the <paramref name="request"/> is not enlisted under a batch,
		/// ie its <see cref="FundsTransferRequest.Batch"/> property is null.
		/// </exception>
		public async Task<ActionResult> AddFundsTransferEventAsync(
			FundsTransferRequest request,
			DateTime utcDate,
			FundsTransferEventType eventType,
			Func<J, Task> asyncJournalAppendAction = null,
			Guid? collationID = null,
			string responseCode = null,
			string traceCode = null,
			string comments = null,
			Exception exception = null)
		{
			if (request == null) throw new ArgumentNullException(nameof(request));
			if (utcDate.Kind != DateTimeKind.Utc) throw new ArgumentException("Date is not UTC.", nameof(utcDate));

			var batch = request.Batch;

			// Any event type other than Pending must belong to a request enlisted under a batch.
			switch (eventType)
			{
				case FundsTransferEventType.Pending:
					break;

				default:
					if (batch == null)
						throw new ArgumentException("The funds transfer request has not been enlisted in a batch.", nameof(request));
					break;
			}

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				// Wllow only one pending or success event per request.
				switch (eventType)
				{
					case FundsTransferEventType.Pending:
					case FundsTransferEventType.Succeeded:
						{
							bool typeIsAlreadyAdded = await
								this.DomainContainer.FundsTransferEvents
								.Where(e => e.RequestID == request.ID && e.Type == eventType)
								.AnyAsync();

							if (typeIsAlreadyAdded)
								throw new AccountingException(
									$"An event of type '{eventType}' already exists for request with transaction ID '{request.TransactionID}'.");

						}
						break;

					default:
						break;
				}

				bool eventIsNotnew = await
					this.DomainContainer.FundsTransferEvents
					.Where(e => e.Request.ID == request.ID && e.Date >= utcDate)
					.AnyAsync();

				if (eventIsNotnew)
					throw new AccountingException(
						"The added event is not newer than all the existing events of the request.");

				var transferEvent = this.DomainContainer.FundsTransferEvents.Create();

				transferEvent.Comments = comments;
				transferEvent.ResponseCode = responseCode;
				transferEvent.TraceCode = traceCode;
				transferEvent.Type = eventType;
				transferEvent.CollationID = collationID;
				transferEvent.Date = utcDate;

				transferEvent.Request = request;

				if (exception != null)
				{
					var serializationFormatter = new Serialization.FastBinaryFormatter();

					try
					{
						using (var stream = new System.IO.MemoryStream())
						{
							serializationFormatter.Serialize(stream, exception);

							transferEvent.ExceptionData = stream.ToArray();
						}
					}
					catch (System.Runtime.Serialization.SerializationException serializationException)
					{
						using (var stream = new System.IO.MemoryStream())
						{
							serializationFormatter.Serialize(stream, serializationException);

							transferEvent.ExceptionData = stream.ToArray();
						}
					}
				}

				J journal = null;

				switch (eventType)
				{
					case FundsTransferEventType.Pending:
						if (request.Amount > 0.0M)
						{
							journal = CreateJournalForFundsTransferEvent(transferEvent);

							journal.Description = AccountingMessages.WITHDRAWAL_ESCROW_DESCRIPTION;

							P moveFromMainAccountPosting = CreatePostingForJournal(journal);

							moveFromMainAccountPosting.Amount = -request.Amount;
							moveFromMainAccountPosting.Account = request.MainAccount;
							moveFromMainAccountPosting.Description = AccountingMessages.MOVE_AMOUNT_FROM_MAIN_ACCOUNT;

							P moveToEscrowAccountPosting = CreatePostingForJournal(journal);

							moveToEscrowAccountPosting.Amount = request.Amount;
							moveToEscrowAccountPosting.Account = request.EscrowAccount;
							moveToEscrowAccountPosting.Description = AccountingMessages.MOVE_AMOUNT_TO_ESCROW_ACCOUNT;
						}
						break;

					case FundsTransferEventType.Submitted:
					case FundsTransferEventType.Accepted:
						request.State = FundsTransferState.Submitted;
						break;

					case FundsTransferEventType.WorkflowFailed:
						request.State = FundsTransferState.WorkflowFailed;
						break;

					case FundsTransferEventType.Failed:
						request.State = FundsTransferState.Failed;

						if (request.Amount > 0.0M)
						{
							journal = CreateJournalForFundsTransferEvent(transferEvent);

							journal.Description = AccountingMessages.REFUND_FAILED_TRANSFER;

							P moveFromEscrowAccountPosting = CreatePostingForJournal(journal);

							moveFromEscrowAccountPosting.Amount = -request.Amount;
							moveFromEscrowAccountPosting.Account = request.EscrowAccount;
							moveFromEscrowAccountPosting.Description = AccountingMessages.MOVE_AMOUNT_FROM_ESCROW_ACCOUNT;

							P moveToMainAccountPosting = CreatePostingForJournal(journal);

							moveToMainAccountPosting.Amount = request.Amount;
							moveToMainAccountPosting.Account = request.MainAccount;
							moveToMainAccountPosting.Description = AccountingMessages.MOVE_AMOUNT_TO_MAIN_ACCOUNT;
						}

						break;

					case FundsTransferEventType.Succeeded:
						request.State = FundsTransferState.Succeeded;

						journal = CreateJournalForFundsTransferEvent(transferEvent);

						journal.Description = AccountingMessages.TRANSFER_SUCCEEDED;

						{
							var remittance = CreateRemittanceForJournal(journal, batch.CreditSystemID);

							remittance.Amount = -request.Amount;

							if (request.Amount > 0.0M)
							{
								remittance.Account = request.EscrowAccount;
								remittance.Description = AccountingMessages.DEPLETE_ESCROW_ACCOUNT;
							}
							else
							{
								remittance.Account = request.MainAccount;
								remittance.Description = AccountingMessages.FUND_MAIN_ACCOUNT;
							}
						}
						break;
				}

				this.DomainContainer.FundsTransferEvents.Add(transferEvent);

				if (asyncJournalAppendAction != null)
				{
					if (journal == null)
					{
						journal = CreateJournalForFundsTransferEvent(transferEvent);
					}

					await asyncJournalAppendAction(journal);
				}

				if (journal != null)
				{
					EnsureSufficientBalances(journal);

					await ExecuteJournalAsync(journal);
				}

				await transaction.CommitAsync();

				return new ActionResult
				{
					FundsTransferEvent = transferEvent,
					Journal = journal
				};
			}
		}

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
		public Exception GetFundsTransferEventException(FundsTransferEvent fundsTransferEvent)
		{
			if (fundsTransferEvent == null) throw new ArgumentNullException(nameof(fundsTransferEvent));

			if (fundsTransferEvent.ExceptionData != null)
			{
				var serializationFormatter = new Serialization.FastBinaryFormatter();

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

		/// <summary>
		/// From a set of funds transfer requests, filter those which are pending
		/// a response.
		/// </summary>
		/// <param name="fundsTransferRequestsQuery">The set of requests.</param>
		/// <param name="includeSubmitted">In the results, include requests which are already submitted.</param>
		/// <returns>Returns the set of filtered requests.</returns>
		public IQueryable<FundsTransferRequest> FilterPendingFundsTransferRequests(
			IQueryable<FundsTransferRequest> fundsTransferRequestsQuery,
			bool includeSubmitted = false)
		{
			if (fundsTransferRequestsQuery == null) throw new ArgumentNullException(nameof(fundsTransferRequestsQuery));

			if (includeSubmitted)
			{
				return from ftr in fundsTransferRequestsQuery
							 let lastEventType = ftr.Events.OrderByDescending(e => e.Date).Select(e => e.Type).FirstOrDefault()
							 where lastEventType == FundsTransferEventType.Pending || lastEventType == FundsTransferEventType.Submitted
							 select ftr;
			}
			else
			{
				return from ftr in fundsTransferRequestsQuery
							 let lastEventType = ftr.Events.OrderByDescending(e => e.Date).Select(e => e.Type).FirstOrDefault()
							 where lastEventType == FundsTransferEventType.Pending
							 select ftr;
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
		protected void AmendAccounts(IEnumerable<JournalLine<U>> journalLines)
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
		/// <exception cref="NegativeBalanceException">
		/// Thrown when at least one account balance would turn to negative
		/// if the journal would be executed.
		/// </exception>
		protected void EnsureSufficientBalances(J journal)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));

			IReadOnlyDictionary<Account, decimal> futureBalancesByAccount = PredictAccountBalances(journal);

			if (futureBalancesByAccount.Any(entry => entry.Value < 0.0M))
				throw new NegativeBalanceException(futureBalancesByAccount);
		}

		/// <summary>
		/// Ensures that no account will fall to negative balance
		/// after the execution of the journal.
		/// </summary>
		/// <param name="journal">The journal to test.</param>
		/// <param name="accountPredicate">
		/// A predicate to select which accounts are tested for negative balance.
		/// </param>
		/// <exception cref="NegativeBalanceException">
		/// Thrown when at least one account balance would turn to negative
		/// if the journal would be executed.
		/// </exception>
		protected void EnsureSufficientBalances(J journal, Func<Account, bool> accountPredicate)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));
			if (accountPredicate == null) throw new ArgumentNullException(nameof(accountPredicate));

			IReadOnlyDictionary<Account, decimal> futureBalancesByAccount = PredictAccountBalances(journal);

			if (futureBalancesByAccount.Any(entry => entry.Value < 0.0M && accountPredicate(entry.Key)))
				throw new NegativeBalanceException(futureBalancesByAccount);
		}

		/// <summary>
		/// Predict the account balances if a journal were to be executed.
		/// </summary>
		/// <param name="journal">The prospective journal.</param>
		/// <returns>Returns a dictionary having the accounts as keys and the predicted balances as values.</returns>
		protected IReadOnlyDictionary<Account, decimal> PredictAccountBalances(J journal)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));

			var journalLines = new List<JournalLine<U>>(journal.Remittances.Count + journal.Postings.Count);

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
		/// Returns a created but not persisted empty journal.
		/// </returns>
		protected virtual J CreateJournalForEntity(object entity)
		{
			if (entity == null) throw new ArgumentNullException(nameof(entity));

			var journal = this.DomainContainer.Journals.Create();
			this.DomainContainer.Journals.Add(journal);

			return journal;
		}

		/// <summary>
		/// Create a journal to refer to a <see cref="FundsTransferEvent"/> and
		/// inherit any appropriate owners.
		/// </summary>
		/// <param name="transferEvent">The funds transfer event.</param>
		/// <returns>
		/// Returns a created but not persisted empty journal.
		/// </returns>
		protected virtual J CreateJournalForFundsTransferEvent(FundsTransferEvent transferEvent)
		{
			if (transferEvent == null) throw new ArgumentNullException(nameof(transferEvent));

			var request = transferEvent.Request;

			var journal = this.DomainContainer.Journals.Create();
			this.DomainContainer.Journals.Add(journal);

			journal.Description = String.Format(AccountingMessages.GENERIC_FUNDS_TRANSFER_JOURNAL, transferEvent.Type);

			return journal;
		}

		/// <summary>
		/// Create a posting suitable for a journal.
		/// </summary>
		/// <param name="journal">The journal.</param>
		/// <returns>Returns the posting.</returns>
		protected virtual P CreatePostingForJournal(J journal)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));

			var posting = this.DomainContainer.Postings.Create();
			journal.Postings.Add(posting);

			return posting;
		}

		/// <summary>
		/// Create a remittance suitable for a journal.
		/// </summary>
		/// <param name="journal">The journal.</param>
		/// <param name="creditSystemID">The ID of the credit system to which the remittance refers.</param>
		/// <returns>Returns the remittance.</returns>
		protected virtual R CreateRemittanceForJournal(J journal, long creditSystemID)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));

			var remittance = this.DomainContainer.Remittances.Create();
			journal.Remittances.Add(remittance);

			remittance.CreditSystemID = creditSystemID;

			return remittance;
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
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="ownEncryptedBankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="amount">The amount of the transfer, positive for deposit, negative for withdrawal.</param>
		/// <param name="mainAccount">The main account being charged.</param>
		/// <param name="escrowAccount">The escrow account for holding outgoing funds.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional ID of the batch.</param>
		/// <param name="queueEventCollationID">The optional ID of the collation of queuing event being generated.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		private async Task<ActionResult> CreateFundsTransferRequestAsync(
			EncryptedBankAccountInfo ownEncryptedBankAccountInfo,
			decimal amount,
			Account mainAccount,
			Account escrowAccount,
			Func<J, Task> asyncJournalAppendAction = null,
			Guid? batchID = null,
			Guid? queueEventCollationID = null)
		{
			if (ownEncryptedBankAccountInfo == null) throw new ArgumentNullException(nameof(ownEncryptedBankAccountInfo));
			if (mainAccount == null) throw new ArgumentNullException(nameof(mainAccount));
			if (escrowAccount == null) throw new ArgumentNullException(nameof(escrowAccount));
			if (amount == 0.0M) throw new ArgumentException("The amount must not be zero.", nameof(amount));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				var request = this.DomainContainer.FundsTransferRequests.Create();

				request.Amount = amount;
				request.State = FundsTransferState.Pending;
				request.TransactionID = Guid.NewGuid();
				request.BatchID = batchID;
				request.MainAccount = mainAccount;
				request.EscrowAccount = escrowAccount;
				request.EncryptedBankAccountInfo = ownEncryptedBankAccountInfo;

				this.DomainContainer.FundsTransferRequests.Add(request);

				var queueEvent = await AddFundsTransferEventAsync(
					request, 
					DateTime.UtcNow, 
					FundsTransferEventType.Pending,
					asyncJournalAppendAction,
					queueEventCollationID);

				await transaction.CommitAsync();

				return queueEvent;
			}
		}

		/// <summary>
		/// Create a funds transfer request.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds.</param>
		/// <param name="ownEncryptedBankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="amount">The amount of the transfer, positive for deposit, negative for withdrawal.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="queueEventCollationID">The optional ID of the collation of queuing event being generated.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		private async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolder transferableFundsHolder,
			EncryptedBankAccountInfo ownEncryptedBankAccountInfo,
			decimal amount,
			Func<J, Task> asyncJournalAppendAction,
			Guid? batchID = null,
			Guid? queueEventCollationID = null)
		{
			if (transferableFundsHolder == null) throw new ArgumentNullException(nameof(transferableFundsHolder));

			return await CreateFundsTransferRequestAsync(
				ownEncryptedBankAccountInfo,
				amount,
				transferableFundsHolder.MainAccount,
				transferableFundsHolder.EscrowAccount,
				asyncJournalAppendAction,
				batchID,
				queueEventCollationID);
		}

		#endregion
	}
}
