# Overview

`Grammophone.Domos.Accounting` provides accounting and funds transfer operations over a Domos domain container.

The central class is `AccountingSession`. It is designed to be derived or composed by application-specific logic and to run under the higher `Grammophone.Domos.Logic` layer.

The library works with generic domain types so concrete applications can specialize users, state transitions, postings, remittances, journals, invoices, lines, events and tax components.

The session manages domain-container transactions, installs an entity listener for the active accounting agent and uses provider-neutral query extensions for async queries and set-based mutations.
