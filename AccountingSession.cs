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
using Z.EntityFramework.Plus;

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
							 let lastEventType = ftr.Events.OrderByDescending(e => e.Time).Select(e => e.Type).FirstOrDefault()
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
		/// <returns>Returns the pending event of the created and persisted batch.</returns>
		public async Task<FundsTransferBatchMessage> CreateFundsTransferBatchAsync(CreditSystem creditSystem)
		{
			if (creditSystem == null) throw new ArgumentNullException(nameof(creditSystem));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				var batch = this.DomainContainer.FundsTransferBatches.Create();
				this.DomainContainer.FundsTransferBatches.Add(batch);

				batch.ID = Guid.NewGuid();
				batch.CreditSystem = creditSystem;

				var batchMessage = await AddFundsTransferBatchMessageAsync(batch, FundsTransferBatchMessageType.Pending, DateTime.UtcNow);

				await transaction.CommitAsync();

				return batchMessage;
			}
		}

		/// <summary>
		/// Enroll a set of funds transfer requests into a new <see cref="FundsTransferBatch"/>.
		/// The requests must not be already under an existing batch.
		/// </summary>
		/// <param name="creditSystem">The credit system to be aassigned to the batch.</param>
		/// <param name="requests">The set of dunds transfer requests.</param>
		/// <returns>Returns the pending message of the created batch, where the requests are attached.</returns>
		/// <exception cref="AccountingException">
		/// Thrown when at least one request is already assigned to a batch.
		/// </exception>
		public async Task<FundsTransferBatchMessage> EnrollRequestsIntoBatchAsync(CreditSystem creditSystem, IQueryable<FundsTransferRequest> requests)
		{
			if (creditSystem == null) throw new ArgumentNullException(nameof(creditSystem));
			if (requests == null) throw new ArgumentNullException(nameof(requests));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				bool requestsAlreadyInBatch = await requests.AnyAsync(r => r.Batch != null);

				if (requestsAlreadyInBatch)
					throw new AccountingException("There is at least one request already in a batch.");

				var pendingBatchMessage = await CreateFundsTransferBatchAsync(creditSystem);

				try
				{
					// First update the events, otherqise, changing the requests to have a batch renders void the requests query.
					var pendingEvents = from r in requests
															from e in r.Events
															where e.Type == FundsTransferEventType.Pending
															select e;

					await pendingEvents.UpdateAsync(e => new FundsTransferEvent { BatchMessageID = pendingBatchMessage.ID });

					// Now that the events are updated, safely update the requests to have a batch.
					await requests.UpdateAsync(r => new FundsTransferRequest { BatchID = pendingBatchMessage.BatchID });
				}
				catch (SystemException ex) // Tranlation exception is needed for the batch update operations.
				{
					throw this.DomainContainer.TranslateException(ex);
				}

				await transaction.CommitAsync();

				return pendingBatchMessage;
			}
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="bankAccountInfo">The bank account info to be encrypted and recorded.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="mainAccount">The main account being charged.</param>
		/// <param name="escrowAccount">The escrow account for holding outgoing funds.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
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
			Func<J, Task> asyncJournalAppendAction = null,
			Guid? batchID = null,
			string requestComments = null,
			string pendingEventComments = null)
		{
			if (bankAccountInfo == null) throw new ArgumentNullException(nameof(bankAccountInfo));

			var ownEncryptedBankAccountInfo = bankAccountInfo.Encrypt(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				ownEncryptedBankAccountInfo,
				amount,
				mainAccount,
				escrowAccount,
				asyncJournalAppendAction,
				batchID,
				requestComments,
				pendingEventComments);
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="bankAccountHolder">An entity holding a bank account.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="mainAccount">The main account being charged.</param>
		/// <param name="escrowAccount">The escrow account for holding outgoing funds.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
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
			Func<J, Task> asyncJournalAppendAction = null,
			Guid? batchID = null,
			string requestComments = null,
			string pendingEventComments = null)
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
				requestComments,
				pendingEventComments);
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds inside the platform.</param>
		/// <param name="bankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolder transferableFundsHolder,
			BankAccountInfo bankAccountInfo,
			decimal amount,
			Func<J, Task> asyncJournalAppendAction = null,
			Guid? batchID = null,
			string requestComments = null,
			string pendingEventComments = null)
		{
			if (bankAccountInfo == null) throw new ArgumentNullException(nameof(bankAccountInfo));

			var encryptedBankAccountInfo = bankAccountInfo.Encrypt(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				transferableFundsHolder,
				encryptedBankAccountInfo,
				amount,
				asyncJournalAppendAction,
				batchID,
				requestComments,
				pendingEventComments);
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds.</param>
		/// <param name="bankAccountHolder">A holder of a bank account to be assigned to the request.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolder transferableFundsHolder,
			IBankAccountHolder bankAccountHolder,
			decimal amount,
			Func<J, Task> asyncJournalAppendAction = null,
			Guid? batchID = null,
			string requestComments = null,
			string pendingEventComments = null)
		{
			if (bankAccountHolder == null) throw new ArgumentNullException(nameof(bankAccountHolder));

			var encryptedBankAccountInfo = bankAccountHolder.EncryptedBankAccountInfo.Clone(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				transferableFundsHolder,
				encryptedBankAccountInfo,
				amount,
				asyncJournalAppendAction,
				batchID,
				requestComments,
				pendingEventComments);
		}

		/// <summary>
		/// Request withdrawal from a holder of funds.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds and owner of bank account.</param>
		/// <param name="amount">The amount to withdraw.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolderWithBankAccount transferableFundsHolder,
			decimal amount,
			Func<J, Task> asyncJournalAppendAction = null,
			Guid? batchID = null,
			string requestComments = null,
			string pendingEventComments = null)
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
				requestComments,
				pendingEventComments);
		}

		/// <summary>
		/// Record a message for a <see cref="FundsTransferBatch"/>.
		/// </summary>
		/// <param name="batch">The <see cref="FundsTransferBatch"/>.</param>
		/// <param name="messageType">The type of the message.</param>
		/// <param name="utcTime">The UTC time of the message.</param>
		/// <param name="comments">Optional comments to record in the message. Maximum length is <see cref="FundsTransferBatchMessage.CommentsLength"/>.</param>
		/// <param name="messageCode">Optional code to record inthe message. Maximum length is <see cref="FundsTransferBatchMessage.MessageCodeLength"/>.</param>
		/// <param name="messageID">Optional specification of the message ID, else a new GUID will be assigned to it.</param>
		/// <returns>Returns the created and persisted event.</returns>
		/// <exception cref="AccountingException">
		/// Thrown when <paramref name="messageType"/> is <see cref="FundsTransferBatchMessageType.Pending"/>
		/// or <see cref="FundsTransferBatchMessageType.Responded"/> and
		/// there already exists a message with the same type,
		/// or when a more recent message than <paramref name="utcTime"/> exists.
		/// </exception>
		public async Task<FundsTransferBatchMessage> AddFundsTransferBatchMessageAsync(
			FundsTransferBatch batch,
			FundsTransferBatchMessageType messageType,
			DateTime utcTime,
			string comments = null,
			string messageCode = null,
			Guid? messageID = null)
		{
			if (batch == null) throw new ArgumentNullException(nameof(batch));
			if (utcTime.Kind != DateTimeKind.Utc) throw new ArgumentException("Time is not UTC.", nameof(utcTime));

			if (comments != null && comments.Length > FundsTransferBatchMessage.CommentsLength)
				throw new ArgumentException($"Maximum length for comments is {FundsTransferBatchMessage.CommentsLength}.", nameof(comments));

			if (messageCode != null && messageCode.Length > FundsTransferBatchMessage.MessageCodeLength)
				throw new ArgumentException($"Maximum length for message code is {FundsTransferBatchMessage.MessageCodeLength}.", nameof(messageCode));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				switch (messageType)
				{
					// Allow only one event of type Pending or Responded in a batch.
					case FundsTransferBatchMessageType.Pending:
					case FundsTransferBatchMessageType.Responded:
						{
							bool messageTypeAlreadyExists = batch.Messages.Any(m => m.Type == messageType);

							if (messageTypeAlreadyExists)
								throw new AccountingException(
									$"A message of type '{messageType}' already exists for batch with ID '{batch.ID}'.");
						}
						break;
				}

				bool moreRecentMessageExists = batch.Messages.Any(e => e.Time >= utcTime);

				if (moreRecentMessageExists)
					throw new AccountingException(
						$"An more recent message already exists for batch with ID '{batch.ID}'.");

				var message = this.DomainContainer.FundsTransferBatchMessages.Create();
				this.DomainContainer.FundsTransferBatchMessages.Add(message);

				message.ID = messageID ?? Guid.NewGuid();
				message.Type = messageType;
				message.Batch = batch;
				message.Time = utcTime;
				message.Comments = comments;
				message.MessageCode = messageCode;

				await transaction.CommitAsync();

				return message;
			}
		}

		/// <summary>
		/// Add an event for a funds tranfer request.
		/// </summary>
		/// <param name="request">The funds tranfer request.</param>
		/// <param name="utcTime">The event time, in UTC.</param>
		/// <param name="eventType">The type of the event.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchMessageID">Optional ID of the batch message where the event belongs.</param>
		/// <param name="responseCode">The optinal response code of the event.</param>
		/// <param name="traceCode">The optional trace code for the event.</param>
		/// <param name="comments">Optional comments. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
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
			DateTime utcTime,
			FundsTransferEventType eventType,
			Func<J, Task> asyncJournalAppendAction = null,
			Guid? batchMessageID = null,
			string responseCode = null,
			string traceCode = null,
			string comments = null,
			Exception exception = null)
		{
			if (request == null) throw new ArgumentNullException(nameof(request));
			if (utcTime.Kind != DateTimeKind.Utc) throw new ArgumentException("Time is not UTC.", nameof(utcTime));

			if (responseCode != null && responseCode.Length > FundsTransferEvent.ResponseCodeLength)
				throw new ArgumentException($"The maximum length for response code is {FundsTransferEvent.ResponseCodeLength}.", nameof(responseCode));

			if (traceCode != null && traceCode.Length > FundsTransferEvent.TraceCodeLength)
				throw new ArgumentException($"The maximum length for trace code is {FundsTransferEvent.TraceCodeLength}.", nameof(traceCode));

			if (comments != null && comments.Length > FundsTransferEvent.CommentsLength)
				throw new ArgumentException($"The maximum length for comments is {FundsTransferEvent.CommentsLength}.", nameof(comments));

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

			if (batchMessageID.HasValue)
			{
				if (batch == null)
					throw new InvalidOperationException("Non-null batch message ID is specified while there is no batch.");

				bool batchContainsMessage = batch.Messages.Any(m => m.ID == batchMessageID.Value);

				if (!batchContainsMessage)
					throw new InvalidOperationException("The batch of the request does not contain the specidied message.");
			}

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				// Allow only one pending or success event per request.
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
									$"An event of type '{eventType}' already exists for request with ID '{request.ID}'.");

						}
						break;
				}

				bool eventIsNotnew = await
					this.DomainContainer.FundsTransferEvents
					.Where(e => e.Request.ID == request.ID && e.Time >= utcTime)
					.AnyAsync();

				if (eventIsNotnew)
					throw new AccountingException(
						"The added event is not newer than all the existing events of the request.");

				var transferEvent = this.DomainContainer.FundsTransferEvents.Create();

				transferEvent.Comments = comments;
				transferEvent.ResponseCode = responseCode;
				transferEvent.TraceCode = traceCode;
				transferEvent.Type = eventType;
				transferEvent.BatchMessageID = batchMessageID;
				transferEvent.Time = utcTime;

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
							remittance.FundsTransferEvent = transferEvent;
							remittance.TransactionID = request.TransactionID.ToString();

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
		/// Add an event for a funds tranfer request.
		/// </summary>
		/// <param name="requestID">The ID of the funds tranfer request.</param>
		/// <param name="utcTime">The event time, in UTC.</param>
		/// <param name="eventType">The type of the event.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchMessageID">Optional ID of the batch message where the event belongs.</param>
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
		/// the funds transfer request must have been enlisted under a batch,
		/// ie its <see cref="FundsTransferRequest.Batch"/> property must not be null.
		/// </remarks>
		/// <exception cref="AccountingException">
		/// Thrown when the request already has an event of the
		/// given <paramref name="eventType"/>.
		/// </exception>
		/// <exception cref="ArgumentException">
		/// Thrown when the event type is other than <see cref="FundsTransferEventType.Pending"/>
		/// and the request is not enlisted under a batch,
		/// ie its <see cref="FundsTransferRequest.Batch"/> property is null.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// Thrown when no <see cref="FundsTransferRequest"/> exists having ID equal to <paramref name="requestID"/>.
		/// </exception>
		public async Task<ActionResult> AddFundsTransferEventAsync(
			long requestID,
			DateTime utcTime,
			FundsTransferEventType eventType,
			Func<J, Task> asyncJournalAppendAction = null,
			Guid? batchMessageID = null,
			string responseCode = null,
			string traceCode = null,
			string comments = null,
			Exception exception = null)
		{
			var request = await
				this.DomainContainer.FundsTransferRequests
				.Include(r => r.MainAccount)
				.Include(r => r.EscrowAccount)
				.Include(r => r.Batch.Messages)
				.SingleAsync(r => r.ID == requestID);

			return await AddFundsTransferEventAsync(
				request,
				utcTime,
				eventType,
				asyncJournalAppendAction,
				batchMessageID,
				responseCode,
				traceCode,
				comments,
				exception);
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
			=> fundsTransferEvent.GetException();

		/// <summary>
		/// From a set of funds transfer requests, filter those which are pending
		/// a response.
		/// </summary>
		/// <param name="requestsQuery">The set of requests.</param>
		/// <param name="includeSubmitted">If true, include in the results the requests which are already submitted.</param>
		/// <returns>Returns the set of filtered requests.</returns>
		public IQueryable<FundsTransferRequest> FilterPendingFundsTransferRequests(
			IQueryable<FundsTransferRequest> requestsQuery,
			bool includeSubmitted = false)
		{
			if (requestsQuery == null) throw new ArgumentNullException(nameof(requestsQuery));

			if (includeSubmitted)
			{
				return FilterFundsTransferRequestsByLatestEvent(
					requestsQuery,
					latestEvent => latestEvent.Type == FundsTransferEventType.Pending || latestEvent.Type == FundsTransferEventType.Submitted);
			}
			else
			{
				return FilterFundsTransferRequestsByLatestEvent(
					requestsQuery,
					latestEvent => latestEvent.Type == FundsTransferEventType.Pending);
			}
		}

		/// <summary>
		/// From a set of funds transfer requests, filter those whose
		/// last event matches a predicate.
		/// </summary>
		/// <param name="requestsQuery">The set of requests.</param>
		/// <param name="latestEventPredicate">The predicate to apply to the last event of each request.</param>
		/// <returns>Returns the set of filtered requests.</returns>
		public IQueryable<FundsTransferRequest> FilterFundsTransferRequestsByLatestEvent(
			IQueryable<FundsTransferRequest> requestsQuery,
			Expression<Func<FundsTransferEvent, bool>> latestEventPredicate)
		{
			if (requestsQuery == null) throw new ArgumentNullException(nameof(requestsQuery));
			if (latestEventPredicate == null) throw new ArgumentNullException(nameof(latestEventPredicate));

			return requestsQuery
				.Select(r => r.Events.OrderByDescending(e => e.Time).FirstOrDefault())
				.Where(latestEventPredicate)
				.Select(e => e.Request);
		}

		/// <summary>
		/// From a set of funds transfer batches, filter those whose
		/// last message matches a predicate.
		/// </summary>
		/// <param name="batchesQuery">The set of batches.</param>
		/// <param name="latestMessagePredicate">The predicate to apply to the last message of each batch.</param>
		/// <returns>Returns the set of filtered batches.</returns>
		public IQueryable<FundsTransferBatch> FilterFundsTransferBatchesByLatestMessage(
			IQueryable<FundsTransferBatch> batchesQuery,
			Expression<Func<FundsTransferBatchMessage, bool>> latestMessagePredicate)
		{
			if (batchesQuery == null) throw new ArgumentNullException(nameof(batchesQuery));
			if (latestMessagePredicate == null) throw new ArgumentNullException(nameof(latestMessagePredicate));

			return batchesQuery
				.Select(b => b.Messages.OrderByDescending(m => m.Time).FirstOrDefault())
				.Where(latestMessagePredicate)
				.Select(m => m.Batch);
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
			journal.FundsTransferEvent = transferEvent;

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
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="mainAccount">The main account being charged.</param>
		/// <param name="escrowAccount">The escrow account if <paramref name="amount"/> is positive, otherwise ignored.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional ID of the batch.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
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
			string requestComments = null,
			string pendingEventComments = null)
		{
			if (ownEncryptedBankAccountInfo == null) throw new ArgumentNullException(nameof(ownEncryptedBankAccountInfo));
			if (mainAccount == null) throw new ArgumentNullException(nameof(mainAccount));
			if (amount > 0.0M && escrowAccount == null) throw new ArgumentNullException(nameof(escrowAccount));
			if (amount == 0.0M) throw new ArgumentException("The amount must not be zero.", nameof(amount));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				var request = this.DomainContainer.FundsTransferRequests.Create();

				request.Amount = amount;
				request.State = FundsTransferState.Pending;
				request.TransactionID = Guid.NewGuid();
				request.BatchID = batchID;
				request.MainAccount = mainAccount;
				request.EscrowAccount = amount > 0.0M ? escrowAccount : null; // Escrow is only needed during withdrawal.
				request.EncryptedBankAccountInfo = ownEncryptedBankAccountInfo;
				request.Comments = requestComments;

				this.DomainContainer.FundsTransferRequests.Add(request);

				Guid? pendingBatchMessageID = null;

				if (batchID.HasValue)
				{
					var batch = await
						this.DomainContainer.FundsTransferBatches
						.Include(b => b.Messages)
						.SingleOrDefaultAsync(b => b.ID == batchID.Value);

					if (batch == null)
						throw new ArgumentException("Invalid batch ID.", nameof(batchID));

					request.Batch = batch;

					var pendingBatchMessage = batch.Messages.SingleOrDefault(m => m.Type == FundsTransferBatchMessageType.Pending);

					if (pendingBatchMessage == null)
						throw new AccountingException("There is no 'Pending' message for the batch.");

					pendingBatchMessageID = pendingBatchMessage.ID;
				}

				var queueEvent = await AddFundsTransferEventAsync(
					request, 
					DateTime.UtcNow, 
					FundsTransferEventType.Pending,
					asyncJournalAppendAction,
					pendingBatchMessageID,
					pendingEventComments);

				await transaction.CommitAsync();

				return queueEvent;
			}
		}

		/// <summary>
		/// Create a funds transfer request.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds.</param>
		/// <param name="ownEncryptedBankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
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
			string requestComments = null,
			string pendingEventComments = null)
		{
			if (transferableFundsHolder == null) throw new ArgumentNullException(nameof(transferableFundsHolder));

			return await CreateFundsTransferRequestAsync(
				ownEncryptedBankAccountInfo,
				amount,
				transferableFundsHolder.MainAccount,
				transferableFundsHolder.EscrowAccount,
				asyncJournalAppendAction,
				batchID,
				requestComments,
				pendingEventComments);
		}

		#endregion
	}
}
