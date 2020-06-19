using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Entity;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
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
			private readonly long agentID;

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

				batch.GUID = Guid.NewGuid();
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

					// Don't use 'requests' directly for batch update because it fails when called by a CompositeFundsTransferManager due to some EF+ bug.
					var pendingRequests = from pr in this.DomainContainer.FundsTransferRequests
																where requests.Any(r => r.ID == pr.ID)
																select pr;

					// Now that the events are updated, safely update the requests to have a batch.
					await pendingRequests.UpdateAsync(r => new FundsTransferRequest { BatchID = pendingBatchMessage.BatchID });
				}
				catch (SystemException ex) // Translation exception is needed for the batch update operations.
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
		/// <param name="bankAccountHolderName">The name of the holder of the bank account.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="mainAccount">The main account being charged.</param>
		/// <param name="transferAccount">The transfer account for outgoing funds, if <paramref name="amount"/> is positive, otherwise ignored.</param>
		/// <param name="category">Optional application-defined category for the request.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
		/// <param name="accountHolderToken">Optional token for specifying a different grouping of requests to the same holder in lines of the transfer file. Requests with null tokens are also grouped together.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			BankAccountInfo bankAccountInfo,
			string bankAccountHolderName,
			decimal amount,
			Account mainAccount,
			Account transferAccount,
			int category = 0,
			Func<J, Task> asyncJournalAppendAction = null,
			long? batchID = null,
			string requestComments = null,
			string pendingEventComments = null,
			string accountHolderToken = null)
		{
			if (bankAccountInfo == null) throw new ArgumentNullException(nameof(bankAccountInfo));
			if (bankAccountHolderName == null) throw new ArgumentNullException(nameof(bankAccountHolderName));

			var ownEncryptedBankAccountInfo = bankAccountInfo.Encrypt(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				ownEncryptedBankAccountInfo,
				bankAccountHolderName,
				amount,
				mainAccount,
				transferAccount,
				category,
				asyncJournalAppendAction,
				batchID,
				requestComments,
				pendingEventComments,
				accountHolderToken);
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="bankingDetail">Bank account details.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="mainAccount">The main account being charged.</param>
		/// <param name="transferAccount">The transfer account for outgoing funds, if <paramref name="amount"/> is positive, otherwise ignored.</param>
		/// <param name="category">Optional application-defined category for the request.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
		/// <param name="accountHolderToken">Optional token for specifying a different grouping of requests to the same holder in lines of the transfer file. Requests with null tokens are also grouped together.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			IBankingDetail bankingDetail,
			decimal amount,
			Account mainAccount,
			Account transferAccount,
			int category = 0,
			Func<J, Task> asyncJournalAppendAction = null,
			long? batchID = null,
			string requestComments = null,
			string pendingEventComments = null,
			string accountHolderToken = null)
		{
			if (bankingDetail == null) throw new ArgumentNullException(nameof(bankingDetail));

			return await CreateFundsTransferRequestAsync(
				bankingDetail.EncryptedBankAccountInfo,
				bankingDetail.GetBankAccountHolderName(),
				amount,
				mainAccount,
				transferAccount,
				category,
				asyncJournalAppendAction,
				batchID,
				requestComments,
				pendingEventComments,
				accountHolderToken);
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds inside the platform.</param>
		/// <param name="bankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="bankAccountHolderName">The name of the holder of the bank account.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="category">Optional application-defined category for the request.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
		/// <param name="accountHolderToken">Optional token for specifying a different grouping of requests to the same holder in lines of the transfer file. Requests with null tokens are also grouped together.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolder transferableFundsHolder,
			BankAccountInfo bankAccountInfo,
			string bankAccountHolderName,
			decimal amount,
			int category = 0,
			Func<J, Task> asyncJournalAppendAction = null,
			long? batchID = null,
			string requestComments = null,
			string pendingEventComments = null,
			string accountHolderToken = null)
		{
			if (bankAccountInfo == null) throw new ArgumentNullException(nameof(bankAccountInfo));
			if (bankAccountHolderName == null) throw new ArgumentNullException(nameof(bankAccountHolderName));

			var encryptedBankAccountInfo = bankAccountInfo.Encrypt(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				transferableFundsHolder,
				encryptedBankAccountInfo,
				bankAccountHolderName,
				amount,
				category,
				asyncJournalAppendAction,
				batchID,
				requestComments,
				pendingEventComments,
				accountHolderToken);
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds.</param>
		/// <param name="bankingDetail">Banking details to be assigned to the request.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="category">Optional application-defined category for the request.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
		/// <param name="accountHolderToken">Optional token for specifying a different grouping of requests to the same holder in lines of the transfer file. Requests with null tokens are also grouped together.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolder transferableFundsHolder,
			IBankingDetail bankingDetail,
			decimal amount,
			int category = 0,
			Func<J, Task> asyncJournalAppendAction = null,
			long? batchID = null,
			string requestComments = null,
			string pendingEventComments = null,
			string accountHolderToken = null)
		{
			if (bankingDetail == null) throw new ArgumentNullException(nameof(bankingDetail));

			var encryptedBankAccountInfo = bankingDetail.EncryptedBankAccountInfo.Clone(this.DomainContainer);

			return await CreateFundsTransferRequestAsync(
				transferableFundsHolder,
				encryptedBankAccountInfo,
				bankingDetail.GetBankAccountHolderName(),
				amount,
				category,
				asyncJournalAppendAction,
				batchID,
				requestComments,
				pendingEventComments,
				accountHolderToken);
		}

		/// <summary>
		/// Request withdrawal from a holder of funds.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds and owner of bank account.</param>
		/// <param name="amount">The amount to withdraw.</param>
		/// <param name="category">Optional application-defined category for the request.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
		/// <param name="accountHolderToken">Optional token for specifying a different grouping of requests to the same holder in lines of the transfer file. Requests with null tokens are also grouped together.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		public async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolderWithBankAccount transferableFundsHolder,
			decimal amount,
			int category = 0,
			Func<J, Task> asyncJournalAppendAction = null,
			long? batchID = null,
			string requestComments = null,
			string pendingEventComments = null,
			string accountHolderToken = null)
		{
			if (transferableFundsHolder == null) throw new ArgumentNullException(nameof(transferableFundsHolder));

			var bankAccountDetail = transferableFundsHolder.BankingDetail;

			if (bankAccountDetail == null)
				throw new ArgumentException(
					"The BankingDetail of the funds holder is not set.",
					nameof(transferableFundsHolder));

			return await CreateFundsTransferRequestAsync(
				transferableFundsHolder,
				bankAccountDetail.EncryptedBankAccountInfo,
				bankAccountDetail.GetBankAccountHolderName(),
				amount,
				category,
				asyncJournalAppendAction,
				batchID,
				requestComments,
				pendingEventComments,
				accountHolderToken);
		}

		/// <summary>
		/// Record a message for a <see cref="FundsTransferBatch"/>.
		/// </summary>
		/// <param name="batch">The <see cref="FundsTransferBatch"/>.</param>
		/// <param name="messageType">The type of the message.</param>
		/// <param name="utcTime">The UTC time of the message.</param>
		/// <param name="comments">Optional comments to record in the message. Maximum length is <see cref="FundsTransferBatchMessage.CommentsLength"/>.</param>
		/// <param name="messageCode">Optional code to record inthe message. Maximum length is <see cref="FundsTransferBatchMessage.MessageCodeLength"/>.</param>
		/// <param name="messageGUID">Optional specification of the message GUID, else a new GUID will be assigned to it.</param>
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
			Guid? messageGUID = null)
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
					// Allow only one event of type Pending in a batch.
					case FundsTransferBatchMessageType.Pending:
						{
							bool messageTypeAlreadyExists = batch.Messages.Any(m => m.Type == messageType);

							if (messageTypeAlreadyExists)
								throw new AccountingException(
									$"A message of type '{messageType}' already exists for batch with ID '{batch.ID}'.");
						}
						break;
				}

				bool moreRecentMessageExists = batch.Messages.Any(m => m.Type > messageType);

				if (moreRecentMessageExists)
					throw new AccountingException(
						$"A more recent message already exists for batch with ID '{batch.ID}'.");

				var message = this.DomainContainer.FundsTransferBatchMessages.Create();
				this.DomainContainer.FundsTransferBatchMessages.Add(message);

				message.GUID = messageGUID ?? Guid.NewGuid();
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
		/// <param name="exception">Optional exception during digestion to record in the event.</param>
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
			long? batchMessageID = null,
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

			bool inhibitJournalAppend = false;

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				// Allow only one pending, success, failure or rejected event per request with no digestion errors.
				if (exception == null)
				{
					switch (eventType)
					{
						case FundsTransferEventType.Pending:
						case FundsTransferEventType.Succeeded:
						case FundsTransferEventType.Failed:
						case FundsTransferEventType.Rejected:
						case FundsTransferEventType.Returned:
							{
								var existingEventQuery = from e in this.DomainContainer.FundsTransferEvents
																				 where e.RequestID == request.ID && e.ExceptionData == null
																				 where eventType < e.Type
																				 || eventType != FundsTransferEventType.Failed && eventType != FundsTransferEventType.Rejected && e.Type == eventType
																				 orderby e.Time, e.CreationDate
																				 select e;

								var existingEvent = await existingEventQuery.FirstOrDefaultAsync();

								if (existingEvent != null)
									throw new AccountingException(
										$"A successfully digested event of type '{existingEvent.Type}' already exists for request with ID '{request.ID}'.");
							}
							break;
					}
				}

				bool eventIsNotnew = await
					this.DomainContainer.FundsTransferEvents
					.Where(e => e.Request.ID == request.ID && e.Type > eventType)
					.AnyAsync();

				if (eventIsNotnew)
					throw new AccountingException(
						"The added event is older than existing events of the request.");

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
						if (request.Amount > 0.0M && exception == null)
						{
							journal = CreateJournalForFundsTransferEvent(transferEvent);

							journal.Description = AccountingMessages.TRANSFER_RESERVE;

							P moveFromMainAccountPosting = CreatePostingForJournal(journal);

							moveFromMainAccountPosting.Amount = -request.Amount;
							moveFromMainAccountPosting.Account = request.MainAccount;
							moveFromMainAccountPosting.Description = AccountingMessages.MOVE_AMOUNT_FROM_MAIN_ACCOUNT;

							P moveToTransferAccountPosting = CreatePostingForJournal(journal);

							moveToTransferAccountPosting.Amount = request.Amount;
							moveToTransferAccountPosting.Account = request.TransferAccount;
							moveToTransferAccountPosting.Description = AccountingMessages.MOVE_AMOUNT_TO_TRANSFER_ACCOUNT;
						}
						break;

					case FundsTransferEventType.Failed:
					case FundsTransferEventType.Rejected:
					case FundsTransferEventType.Returned:
						if (exception == null)
						{
							if (request.Amount > 0.0M)
							{
								if (eventType == FundsTransferEventType.Returned)
								{
									// Did we catch returned item on time? Or do we have previous success?

									if (await SuccessEventExistsForRequestAsync(request.ID))
									{
										journal = CreateJournalForFundsTransferEvent(transferEvent);

										// Too late, handle late returned items.
										await OnOutgoingFundsTransferReturnAsync(transferEvent, journal);

										inhibitJournalAppend = true;

										break;
									}
								}
								else
								{
									if (await FailureEventExistsForRequestAsync(request.ID))
									{
										inhibitJournalAppend = true;

										break;
									}
								}

								journal = CreateJournalForFundsTransferEvent(transferEvent);

								journal.Description = AccountingMessages.TRANSFER_FAILED;

								P moveFromTransferAccountPosting = CreatePostingForJournal(journal);

								moveFromTransferAccountPosting.Amount = -request.Amount;
								moveFromTransferAccountPosting.Account = request.TransferAccount;
								moveFromTransferAccountPosting.Description = AccountingMessages.MOVE_AMOUNT_FROM_TRANSFER_ACCOUNT;

								P moveToMainAccountPosting = CreatePostingForJournal(journal);

								moveToMainAccountPosting.Amount = request.Amount;
								moveToMainAccountPosting.Account = request.MainAccount;
								moveToMainAccountPosting.Description = AccountingMessages.MOVE_AMOUNT_TO_MAIN_ACCOUNT;
							}
							else if (request.Amount < 0.0M)
							{
								switch (eventType)
								{
									case FundsTransferEventType.Returned:
										// Did we catch returned item on time? Or do we have previous success?
										if (await SuccessEventExistsForRequestAsync(request.ID))
										{
											// Too late, handle late returned items.

											journal = CreateJournalForFundsTransferEvent(transferEvent);

											await OnIngoingFundsTransferReturnAsync(transferEvent, journal);

											inhibitJournalAppend = true;
										}
										break;

									default:
										// Did we have a failure already?
										if (await FailureEventExistsForRequestAsync(request.ID))
										{
											inhibitJournalAppend = true;
										}
										break;
								}

							}
						}

						break;

					case FundsTransferEventType.Succeeded:
						if (exception == null)
						{
							journal = CreateJournalForFundsTransferEvent(transferEvent);

							journal.Description = AccountingMessages.TRANSFER_SUCCEEDED;

							{
								var remittance = CreateRemittanceForJournal(journal, batch.CreditSystemID);

								remittance.Amount = -request.Amount;
								remittance.FundsTransferEvent = transferEvent;
								remittance.TransactionID = request.GUID.ToString();

								if (request.Amount > 0.0M)
								{
									remittance.Account = request.TransferAccount;
									remittance.Description = AccountingMessages.DEPLETE_TRANSFER_ACCOUNT;
								}
								else
								{
									remittance.Account = request.MainAccount;
									remittance.Description = AccountingMessages.FUND_MAIN_ACCOUNT;
								}
							}
						}
						break;
				}

				this.DomainContainer.FundsTransferEvents.Add(transferEvent);

				if (exception == null)
				{
					if (asyncJournalAppendAction != null && !inhibitJournalAppend)
					{
						if (journal == null)
						{
							journal = CreateJournalForFundsTransferEvent(transferEvent);
						}

						await asyncJournalAppendAction(journal);
					}

					if (journal != null)
					{
						EnsureSufficientBalancesForFundsTransfer(transferEvent, journal);

						await ExecuteJournalAsync(journal);
					}
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
		/// <param name="exception">Optional exception during digestion to record in the event.</param>
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
			long? batchMessageID = null,
			string responseCode = null,
			string traceCode = null,
			string comments = null,
			Exception exception = null)
		{
			var request = await
				this.DomainContainer.FundsTransferRequests
				.Include(r => r.MainAccount)
				.Include(r => r.TransferAccount)
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
		/// Called when an event for a late return (post-success) of an ingoing funds transfer request is received.
		/// </summary>
		/// <param name="transferEvent">The funds transfer event for the late return.</param>
		/// <param name="journal">The journal to append handling of the late return, if needed.</param>
		/// <remarks>
		/// The default implementation does nothing.
		/// </remarks>
		protected virtual Task OnIngoingFundsTransferReturnAsync(FundsTransferEvent transferEvent, J journal)
			=> Task.CompletedTask;

		/// <summary>
		/// Called when an event for a late return (post-success) of an outgoing funds transfer request is received.
		/// </summary>
		/// <param name="transferEvent">The funds transfer event for the late return.</param>
		/// <param name="journal">The journal to append handling of the late return, if needed.</param>
		/// <remarks>
		/// The default implementation does nothing.
		/// </remarks>
		protected virtual Task OnOutgoingFundsTransferReturnAsync(FundsTransferEvent transferEvent, J journal)
			=> Task.CompletedTask;

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

			if (journal.HasBeenExecuted)
				throw new JournalAlreadyExecutedException();

			try
			{
				using (var transaction = this.DomainContainer.BeginTransaction())
				{
					this.DomainContainer.Journals.Add(journal);

					AmendAccounts(journal.Postings);
					AmendAccounts(journal.Remittances);

					journal.HasBeenExecuted = true;

					await transaction.CommitAsync();
				}
			}
			catch
			{
				journal.HasBeenExecuted = false;

				throw;
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
		/// Ensures that no account will fall to negative balance or move further into negative balance
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

			if (futureBalancesByAccount.Any(entry => entry.Value < 0.0M && entry.Value < entry.Key.Balance))
				throw new NegativeBalanceException(futureBalancesByAccount);
		}

		/// <summary>
		/// Ensures that no account will fall to negative balance or move further into negative balance
		/// after the execution of the journal.
		/// </summary>
		/// <param name="journal">The journal to test.</param>
		/// <param name="testedAccountPredicate">
		/// A predicate to select which accounts are tested for negative balance.
		/// </param>
		/// <exception cref="NegativeBalanceException">
		/// Thrown when at least one account balance would turn to negative
		/// if the journal would be executed.
		/// </exception>
		protected void EnsureSufficientBalances(J journal, Func<Account, bool> testedAccountPredicate)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));
			if (testedAccountPredicate == null) throw new ArgumentNullException(nameof(testedAccountPredicate));

			IReadOnlyDictionary<Account, decimal> futureBalancesByAccount = PredictAccountBalances(journal);

			if (futureBalancesByAccount.Any(entry => entry.Value < 0.0M && entry.Value < entry.Key.Balance && testedAccountPredicate(entry.Key)))
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

		/// <summary>
		/// Visit all postings and remittances of a journal and apply actions to them.
		/// </summary>
		/// <param name="journal">The journal.</param>
		/// <param name="postingAction">The action to be applied to each posting.</param>
		/// <param name="remittanceAction">The action to be applied to each remittance.</param>
		protected void VisitJournalLines(J journal, Action<P> postingAction = null, Action<R> remittanceAction = null)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));

			if (postingAction != null)
			{
				foreach (var posting in journal.Postings)
				{
					postingAction(posting);
				}
			}

			if (remittanceAction != null)
			{
				foreach (var remittance in journal.Remittances)
				{
					remittanceAction(remittance);
				}
			}
		}

		/// <summary>
		/// Visit all postings and remittances of a journal and apply actions to them.
		/// </summary>
		/// <param name="journal">The journal.</param>
		/// <param name="asyncPostingAction">The asynchronous action to be applied to each posting.</param>
		/// <param name="asyncRemittanceAction">The asynchronous action to be applied to each remittance.</param>
		protected async Task VisitJournalLinesAsync(J journal, Func<P, Task> asyncPostingAction = null, Func<R, Task> asyncRemittanceAction = null)
		{
			if (journal == null) throw new ArgumentNullException(nameof(journal));

			if (asyncPostingAction != null)
			{
				foreach (var posting in journal.Postings)
				{
					await asyncPostingAction(posting);
				}
			}

			if (asyncRemittanceAction != null)
			{
				foreach (var remittance in journal.Remittances)
				{
					await asyncRemittanceAction(remittance);
				}
			}
		}

		/// <summary>
		/// Ensure that there are sufficient balances for executing a journal during digestion of a funds transfer event.
		/// </summary>
		/// <param name="fundsTransferEvent">The funds transfer event being digested.</param>
		/// <param name="journal">The journal being executed.</param>
		/// <exception cref="NegativeBalanceException">
		/// Thrown when at least one account balance would turn to negative when in shouldn't
		/// if the journal would be executed.
		/// </exception>
		/// <remarks>
		/// The default implementation calls <see cref="EnsureSufficientBalances(J)"/> to ensure all accounts will no go negative.
		/// Override using <see cref="EnsureSufficientBalances(J, Func{Account, bool})"/> to specify which accounts may go negative.
		/// </remarks>
		protected virtual void EnsureSufficientBalancesForFundsTransfer(FundsTransferEvent fundsTransferEvent, J journal)
		{
			EnsureSufficientBalances(journal);
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
		/// Returns whether a success event exists for a funds transfer request.
		/// </summary>
		/// <param name="requestID">The ID of the funds transfer request.</param>
		private async Task<bool> SuccessEventExistsForRequestAsync(long requestID)
		{
			var previousSuccessEventQuery = from e in this.DomainContainer.FundsTransferEvents
																			where e.RequestID == requestID && e.Type == FundsTransferEventType.Succeeded
																			select e;

			return await previousSuccessEventQuery.AnyAsync();
		}

		/// <summary>
		/// Get, or create if it does not exit, a <see cref="FundsTransferRequestGroup"/> for the
		/// supplied encrypted banking info.
		/// </summary>
		/// <param name="encryptedBankAccountInfo">The encrypted banking info.</param>
		/// <param name="bankAccountHolderName">The name of the holder of the bank account.</param>
		/// <param name="accountHolderToken">Optional token identifying the holder of the bank account.</param>
		/// <returns>Returns the requested group.</returns>
		private async Task<FundsTransferRequestGroup> GetOrCreateFundsTransferRequestGroupAsync(
			EncryptedBankAccountInfo encryptedBankAccountInfo,
			string bankAccountHolderName,
			string accountHolderToken = null)
		{
			if (encryptedBankAccountInfo == null) throw new ArgumentNullException(nameof(encryptedBankAccountInfo));
			if (bankAccountHolderName == null) throw new ArgumentNullException(nameof(bankAccountHolderName));

			if (accountHolderToken != null)
			{
				if (accountHolderToken.Length > FundsTransferRequestGroup.AccountHolderTokenLength)
				{
					throw new ArgumentException(
						$"The length of the token of the account holder must not be greater than {FundsTransferRequestGroup.AccountHolderTokenLength}.",
						nameof(accountHolderToken));
				}
			}

			if (bankAccountHolderName.Length > FundsTransferRequestGroup.AccountHolderNameLength) // Clip length of bank account holder name if necessary
				bankAccountHolderName = bankAccountHolderName.Substring(0, FundsTransferRequestGroup.AccountHolderNameLength);

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				var groupQuery = from g in this.DomainContainer.FundsTransferRequestGroups
												 where g.EncryptedBankAccountInfo.AccountCode == encryptedBankAccountInfo.AccountCode
												 && g.EncryptedBankAccountInfo.BankNumber == encryptedBankAccountInfo.BankNumber
												 && g.EncryptedBankAccountInfo.EncryptedAccountNumber == encryptedBankAccountInfo.EncryptedAccountNumber
												 && g.EncryptedBankAccountInfo.EncryptedTransitNumber == encryptedBankAccountInfo.EncryptedTransitNumber
												 && g.AccountHolderName == bankAccountHolderName
												 && g.AccountHolderToken == accountHolderToken
												 select g;

				var group = await groupQuery.SingleOrDefaultAsync();

				if (group != null)
				{
					transaction.Pass();

					return group;
				}

				group = this.DomainContainer.FundsTransferRequestGroups.Create();
				this.DomainContainer.FundsTransferRequestGroups.Add(group);

				group.EncryptedBankAccountInfo = encryptedBankAccountInfo.Clone(this.DomainContainer);
				group.AccountHolderName = bankAccountHolderName;
				group.AccountHolderToken = accountHolderToken;

				await transaction.CommitAsync();

				return group;
			}
		}

		/// <summary>
		/// Create and persist a <see cref="FundsTransferRequest"/> and record
		/// a <see cref="FundsTransferEvent"/> of type <see cref="FundsTransferEventType.Pending"/>
		/// in it.
		/// </summary>
		/// <param name="encryptedBankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="bankAccountHolderName">The name of the holder of the bank account.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="mainAccount">The main account being charged.</param>
		/// <param name="transferAccount">The transfer account for outgoing funds, if <paramref name="amount"/> is positive, otherwise ignored.</param>
		/// <param name="category">Optional application-defined category for the request.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional ID of the batch.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
		/// <param name="accountHolderToken">Optional token for specifying a different grouping of requests to the same holder in lines of the transfer file. Requests with null tokens are also grouped together.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		private async Task<ActionResult> CreateFundsTransferRequestAsync(
			EncryptedBankAccountInfo encryptedBankAccountInfo,
			string bankAccountHolderName,
			decimal amount,
			Account mainAccount,
			Account transferAccount,
			int category = 0,
			Func<J, Task> asyncJournalAppendAction = null,
			long? batchID = null,
			string requestComments = null,
			string pendingEventComments = null,
			string accountHolderToken = null)
		{
			if (encryptedBankAccountInfo == null) throw new ArgumentNullException(nameof(encryptedBankAccountInfo));
			if (bankAccountHolderName == null) throw new ArgumentNullException(nameof(bankAccountHolderName));
			if (mainAccount == null) throw new ArgumentNullException(nameof(mainAccount));
			if (amount > 0.0M && transferAccount == null) throw new ArgumentNullException(nameof(transferAccount));
			if (amount == 0.0M) throw new ArgumentException("The amount must not be zero.", nameof(amount));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				var request = this.DomainContainer.FundsTransferRequests.Create();
				this.DomainContainer.FundsTransferRequests.Add(request);

				request.Amount = amount;
				request.GUID = Guid.NewGuid();
				request.BatchID = batchID;
				request.Category = category;
				request.MainAccount = mainAccount;
				request.TransferAccount = amount > 0.0M ? transferAccount : null; // Transfer is only needed during withdrawal.
				request.Group = await GetOrCreateFundsTransferRequestGroupAsync(encryptedBankAccountInfo, bankAccountHolderName, accountHolderToken);
				request.Comments = requestComments;

				long? pendingBatchMessageID = null;

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
					comments: pendingEventComments);

				await transaction.CommitAsync();

				return queueEvent;
			}
		}

		/// <summary>
		/// Create a funds transfer request.
		/// </summary>
		/// <param name="transferableFundsHolder">The holder of funds.</param>
		/// <param name="bankAccountHolderName">The name of the holder of the bank account.</param>
		/// <param name="encryptedBankAccountInfo">An account info to be assigned to the request.</param>
		/// <param name="amount">The amount of the transfer to the external system, positive for deposit, negative for withdrawal.</param>
		/// <param name="category">Optional application-defined category for the request.</param>
		/// <param name="asyncJournalAppendAction">An optional function to append lines to the associated journal.</param>
		/// <param name="batchID">Optional batch ID of the funds request.</param>
		/// <param name="requestComments">Optional comments for the request. Maximum length is <see cref="FundsTransferRequest.CommentsLength"/>.</param>
		/// <param name="pendingEventComments">Optional comments for the generated 'pending' event. Maximum length is <see cref="FundsTransferEvent.CommentsLength"/>.</param>
		/// <param name="accountHolderToken">Optional token for specifying a different grouping of requests to the same holder in lines of the transfer file. Requests with null tokens are also grouped together.</param>
		/// <returns>
		/// Returns the queuing event of the funds transfer request
		/// and optionally the journal which moves the amount to the retaining account of the holder,
		/// if the <paramref name="amount"/> is positive.
		/// </returns>
		private async Task<ActionResult> CreateFundsTransferRequestAsync(
			ITransferableFundsHolder transferableFundsHolder,
			EncryptedBankAccountInfo encryptedBankAccountInfo,
			string bankAccountHolderName,
			decimal amount,
			int category = 0,
			Func<J, Task> asyncJournalAppendAction = null,
			long? batchID = null,
			string requestComments = null,
			string pendingEventComments = null,
			string accountHolderToken = null)
		{
			if (transferableFundsHolder == null) throw new ArgumentNullException(nameof(transferableFundsHolder));
			if (bankAccountHolderName == null) throw new ArgumentNullException(nameof(bankAccountHolderName));

			return await CreateFundsTransferRequestAsync(
				encryptedBankAccountInfo,
				bankAccountHolderName,
				amount,
				transferableFundsHolder.MainAccount,
				transferableFundsHolder.TransferAccount,
				category,
				asyncJournalAppendAction,
				batchID,
				requestComments,
				pendingEventComments,
				accountHolderToken);
		}

		/// <summary>
		/// Amend account balances according to a collection of journal lines.
		/// </summary>
		private void AmendAccounts(IEnumerable<JournalLine<U>> journalLines)
		{
			if (journalLines == null) throw new ArgumentNullException(nameof(journalLines));

			foreach (var line in journalLines)
			{
				line.Account.Balance += line.Amount;
			}
		}

		/// <summary>
		/// Return true whether a request contains a successfully digested event of
		/// type <see cref="FundsTransferEventType.Failed"/>, <see cref="FundsTransferEventType.Rejected"/>
		/// or <see cref="FundsTransferEventType.Returned"/>.
		/// </summary>
		/// <param name="requestID">The ID of the request.</param>
		private async Task<bool> FailureEventExistsForRequestAsync(long requestID)
		{
			var query = from e in this.DomainContainer.FundsTransferEvents
									where e.RequestID == requestID && e.ExceptionData == null
									where e.Type == FundsTransferEventType.Failed || e.Type == FundsTransferEventType.Rejected
									|| e.Type == FundsTransferEventType.Returned
									select e;

			return await query.AnyAsync();
		}

		#endregion
	}

	/// <summary>
	/// An <see cref="IDisposable"/> session for accounting actions. 
	/// CAUTION: All actions taking entities as parameters
	/// should have the entities connected via the <see cref="AccountingSession{U, BST, P, R, J, D}.DomainContainer"/> of the class.
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
	/// <typeparam name="ILTC">The type of invoice line tax components, derived from <see cref="InvoiceLineTaxComponent{U, P, R}"/>.</typeparam>
	/// <typeparam name="IL">The type of invoice line, derived from <see cref="InvoiceLine{U, P, R, ILTC}"/>.</typeparam>
	/// <typeparam name="IE">The type of invoice event, derived from <see cref="InvoiceEvent{U, P, R}"/>.</typeparam>
	/// <typeparam name="I">The type of invoices, derived from <see cref="Invoice{U, P, R, ILTC, IL, IE}"/>.</typeparam>
	/// <typeparam name="D">The type of domain container for entities.</typeparam>
	public class AccountingSession<U, BST, P, R, J, ILTC, IL, IE, I, D> : AccountingSession<U, BST, P, R, J, D>
		where U : User
		where BST : StateTransition<U>
		where P : Posting<U>
		where R : Remittance<U>
		where J : Journal<U, BST, P, R>
		where ILTC : InvoiceLineTaxComponent<U, P, R>
		where IL : InvoiceLine<U, P, R, ILTC>
		where IE : InvoiceEvent<U, P, R>
		where I : Invoice<U, P, R, ILTC, IL, IE>
		where D : IDomosDomainContainer<U, BST, P, R, J, ILTC, IL, IE, I>
	{
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
			: base(configurationSectionName, domainContainer, agent)
		{
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
			: base(configurationSectionName, domainContainer, agentPickPredicate)
		{
		}

		/// <summary>
		/// Create using an own <see cref="AccountingSession{U, BST, P, R, J, D}.DomainContainer"/>
		/// specified in <see cref="Settings"/>.
		/// </summary>
		/// <param name="configurationSectionName">The element name of a Unity configuration section.</param>
		/// <param name="agentPickPredicate">A predicate to select a user.</param>
		public AccountingSession(string configurationSectionName, Expression<Func<U, bool>> agentPickPredicate)
			: base(configurationSectionName, agentPickPredicate)
		{
		}

		#endregion

		#region Public methods

		#region Invoices

		/// <summary>
		/// Delete an invoice with all its lines and tax components. Must not have any events recorded.
		/// </summary>
		/// <param name="invoiceID">The ID of the invoice to delete.</param>
		/// <returns>Returns true if the invoice was found and deleted, else false.</returns>
		/// <exception cref="AccountingException">Thrown if the invoice has events recorded.</exception>
		public async Task<bool> DeleteInvoiceAsync(long invoiceID)
		{
			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				try
				{
					bool invoiceExists = await this.DomainContainer.Invoices.AnyAsync(i => i.ID == invoiceID);

					if (!invoiceExists)
					{
						transaction.Pass();

						return false;
					}

					bool eventsExist = await this.DomainContainer.InvoiceEvents.AnyAsync(ie => ie.InvoiceID == invoiceID);

					if (eventsExist)
						throw new AccountingException($"The invoice with ID {invoiceID} cannot be deleted because it has events recorded.");

					var taxComponentsQuery = from tc in this.DomainContainer.InvoiceLineTaxComponents
																	 join l in this.DomainContainer.InvoiceLines on tc.LineID equals l.ID
																	 where l.InvoiceID == invoiceID
																	 select tc;

					await taxComponentsQuery.DeleteAsync();

					var linesQuery = from l in this.DomainContainer.InvoiceLines
													 where l.InvoiceID == invoiceID
													 select l;

					await linesQuery.DeleteAsync();

					var invoiceQuery = from i in this.DomainContainer.Invoices
														 where i.ID == invoiceID
														 select i;

					await invoiceQuery.DeleteAsync();

					await transaction.CommitAsync();

					return true;
				}
				catch (SystemException ex)
				{
					throw this.DomainContainer.TranslateException(ex);
				}
			}
		}

		/// <summary>
		/// Add an invoice. Can contain lines and tax components.
		/// </summary>
		/// <param name="invoice">The invoice to add.</param>
		public async Task AddInvoiceAsync(I invoice)
		{
			this.DomainContainer.Invoices.Add(invoice);

			await this.DomainContainer.SaveChangesAsync();
		}

		#endregion

		#region Invoice events

		/// <summary>
		/// Filter a set of invoices by applying a predicate on their last invoice event.
		/// </summary>
		/// <param name="invoices">The set of invoices to filter.</param>
		/// <param name="invoiceEventPredicate">The predicate to be applied on each invoice's last event.</param>
		/// <returns>Returns the filtered set of invoices.</returns>
		public IQueryable<I> FilterInvoicesByLastEvent(IQueryable<I> invoices, Expression<Func<IE, bool>> invoiceEventPredicate)
		{
			if (invoices == null) throw new ArgumentNullException(nameof(invoices));
			if (invoiceEventPredicate == null) throw new ArgumentNullException(nameof(invoiceEventPredicate));

			return invoices
				.Select(i => i.Events.OrderByDescending(ie => ie.Time).FirstOrDefault()) // Select the last events
				.Where(le => le != null)
				.Where(invoiceEventPredicate) // Filter the last events by the predicate
				.Join(this.DomainContainer.Invoices, ie => ie.InvoiceID, i => i.ID, (ie, i) => i); // Join the invoices corresponding to the filtered last events.
		}

		/// <summary>
		/// Add an event to an invoice.
		/// </summary>
		/// <param name="invoice">The invoice.</param>
		/// <param name="invoiceEvent">The event to add to the invoice.</param>
		/// <exception cref="AccountingException">Thrown when previous events of the invoice are incompatible to the added event.</exception>
		public async Task AddEventToInvoiceAsync(I invoice, IE invoiceEvent)
		{
			if (invoice == null) throw new ArgumentNullException(nameof(invoice));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				var lastInvoiceEvent = invoice.Events.OrderByDescending(i => i.Time).FirstOrDefault();

				ValidateNewInvoiceEvent(invoiceEvent, lastInvoiceEvent);

				invoice.Events.Add(invoiceEvent);

				await transaction.CommitAsync();
			}
		}

		/// <summary>
		/// Add an event to an invoice.
		/// </summary>
		/// <param name="invoiceID">The ID of the invoice.</param>
		/// <param name="invoiceEvent">The event to add to the invoice.</param>
		/// <exception cref="AccountingException">Thrown when previous events of the invoice are incompatible to the added event.</exception>
		public async Task AddEventToInvoiceAsync(long invoiceID, IE invoiceEvent)
		{
			if (invoiceEvent == null) throw new ArgumentNullException(nameof(invoiceEvent));

			using (var transaction = this.DomainContainer.BeginTransaction())
			{
				var lastInvoiceEventQuery = from i in this.DomainContainer.Invoices
																		where i.ID == invoiceID
																		let le = i.Events.OrderByDescending(e => e.Time).FirstOrDefault()
																		select le;

				var lastInvoiceEvent = await lastInvoiceEventQuery.SingleOrDefaultAsync();

				ValidateNewInvoiceEvent(invoiceEvent, lastInvoiceEvent);

				this.DomainContainer.InvoiceEvents.Add(invoiceEvent);
				invoiceEvent.InvoiceID = invoiceID;

				await transaction.CommitAsync();
			}
		}

		#endregion

		#region Funds transfer requests

		/// <summary>
		/// Get the funds transfer requests which service a set of invoices.
		/// </summary>
		/// <param name="invoices">The set of invoices.</param>
		public IQueryable<FundsTransferRequest> GetServicingRequestsForInvoices(IQueryable<I> invoices)
		{
			if (invoices == null) throw new ArgumentNullException(nameof(invoices));

			return from r in this.DomainContainer.FundsTransferRequests
						 where invoices.Any(i => i.ServicingFundsTransferRequests.Any(sr => r.ID == sr.ID))
						 select r;
		}

		/// <summary>
		/// Get the invoices being serviced by a set of funds transfer requests.
		/// </summary>
		/// <param name="requests">The set of funds transfer requests.</param>
		public IQueryable<I> GetServicedInvoicesOfRequests(IQueryable<FundsTransferRequest> requests)
		{
			if (requests == null) throw new ArgumentNullException(nameof(requests));

			return from i in this.DomainContainer.Invoices
						 where i.ServicingFundsTransferRequests.Any(sr => requests.Any(r => sr.ID == r.ID))
						 select i;
		}

		#endregion

		#endregion

		#region Private methods

		/// <summary>
		/// Validate a new invoice event.
		/// </summary>
		/// <param name="invoiceEvent">The event to validate.</param>
		/// <param name="lastInvoiceEvent">The last pre-existing event of the invoice or null if there is none.</param>
		/// <exception cref="AccountingException">Thrown when the event is not valid.</exception>
		private static void ValidateNewInvoiceEvent(IE invoiceEvent, IE lastInvoiceEvent = null)
		{
			if (invoiceEvent == null) throw new ArgumentNullException(nameof(invoiceEvent));

			if (lastInvoiceEvent != null)
			{
				if (lastInvoiceEvent.Time >= invoiceEvent.Time)
					throw new AccountingException("The invoice being added has Time property less than the last event of the invoice.");

				switch (lastInvoiceEvent.InvoiceState)
				{
					case InvoiceState.PartiallyPaid:
						if (invoiceEvent.InvoiceState < lastInvoiceEvent.InvoiceState)
							throw new AccountingException($"The new event must have at least state {InvoiceState.PartiallyPaid} or higher.");

						break;

					case InvoiceState.Paid:
						throw new AccountingException("The invoice is already fully paid. No new invoice event is acceptable.");

					default:
						if (invoiceEvent.InvoiceState <= lastInvoiceEvent.InvoiceState)
							throw new AccountingException($"The new event must have at state higher than {lastInvoiceEvent.InvoiceState} or higher.");
						break;
				}
			}
		}

		#endregion
	}
}
