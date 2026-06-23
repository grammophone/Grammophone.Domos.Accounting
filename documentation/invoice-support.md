# Invoice Support

The invoice-enabled accounting session adds methods for invoices, invoice events, invoice filtering and relationships between invoices and funds transfer requests.

`DeleteInvoiceAsync` deletes invoices only when no invoice events exist. It uses provider-neutral set-based deletes for tax components, lines and the invoice row.

`AddInvoiceAsync` and `AddEventToInvoiceAsync` manage invoice lifecycle records. Query helpers can find invoices by latest event or find servicing funds transfer requests for invoices.

Concrete applications should derive invoice, invoice line, tax component and invoice event types from the Domos domain abstractions.
