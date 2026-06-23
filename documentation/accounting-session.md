# Accounting Session

`AccountingSession<U, BST, P, R, J, D>` is the base accounting session for systems with users, workflow transitions, postings, remittances and journals.

The session can create funds transfer batches, create funds transfer requests, enroll requests into batches, add funds transfer events, filter requests or batches by latest events and recover serialized exception data from events.

`AccountingSession<U, BST, P, R, J, ILTC, IL, IE, I, D>` adds optional invoice support.

The session owns the active accounting agent. Constructors can receive the agent directly, receive a predicate to pick the agent from a supplied domain container or create a domain container from configuration.
