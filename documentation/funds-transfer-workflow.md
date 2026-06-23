# Funds Transfer Workflow

Funds transfer operations revolve around requests, events, batches and batch messages.

Creating a funds transfer request records banking detail, the requested amount and related event/journal information. Requests can be grouped by encrypted bank account information through `FundsTransferRequestGroup`.

Enrolling requests into a batch checks that none already belong to a batch, creates a pending batch message and uses set-based update operations to attach pending events and requests to the batch.

Adding events records changes returned by an external transfer system. Event types distinguish pending, succeeded, failed and other transfer states. Logic-layer funds transfer managers build on these methods to import and export files.
