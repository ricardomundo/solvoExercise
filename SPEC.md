Batch Deduplication Tool — Functional Specification

Version 1.3 · Ingest Platform Team

1\. Purpose

The deduplication tool reads a collection batch and writes a load file for the review platform. Its job is to make sure reviewers never see the same content twice while guaranteeing that every document they do see arrives with its full context.

2\. Input

A batch is a JSON file:

```json

{

&#x20; "batchId": "string",

&#x20; "documents": \[ Document, ... ]

}

```



Field	Type	Present on	Notes



`docId`	string	all	Unique within a batch.



`custodian`	string	all	The person whose mailbox or file share the document came from.



`docType`	string	all	: `email`, `attachment`, or `loose`.



`parentId`	string or null	all	For attachments, the `docId` of the containing document. 





The parent is always present in the same batch. Null otherwise.



`fileName`	string	all	Original file name.



`sha256`	string	all	SHA-256 of the raw file bytes as collected.



`sizeBytes`	integer	all	



`sentDate`	string	email	ISO 8601 with offset, as recorded in the message header.



`from`	string	email	



`to`	string\[]	email	



`subject`	string	email	



`body`	string	email	Plain-text body.



An attachment may itself have attachments (for example, an email attached to an email, which in turn carries a PDF). `parentId` chains express this.



3\. Deduplication



3.1 Two loose files are duplicates if they have the same `sha256`.



3.2 Two attachments are duplicates if they have the same `sha256`.



3.3 The same email is routinely collected from more than one mailbox: the sender's Sent Items and each recipient's Inbox. These copies are duplicates of each other even though their raw bytes, and therefore their `sha256` values, differ (headers, encodings, and line endings vary between mailboxes). The tool must recognise them as duplicates.



3.4 Deduplication is performed across the entire batch.



3.5 An email and its attachments form a family. Families must be produced intact. A reviewer who opens an email must see every attachment that was on it.



3.6 Suppressed duplicates are omitted from the load file.





4\. Custodian requirements



Each custodian's production set must be complete and self-contained, so that it can be produced on its own if that custodian is later severed from the matter.



5\. Load file

A CSV with a header row. Column order and header text are fixed. The ingest job breaks if they change.

Column	Value

`DocID`	`docId`

`FamilyID`	`docId` of the family's top-level document. For a loose file, its own `docId`.

`ParentID`	`parentId`, or empty.

`Custodian`	`custodian`

`DocType`	`docType`

`FileName`	`fileName`

`SentDate`	UTC, formatted exactly `YYYY-MM-DDTHH:MM:SSZ` (e.g. `2024-03-05T19:22:00Z`; no fractional seconds, no offset). Attachments carry their family's top-level email date. Empty for loose files.

`Hash`	`sha256`

Row order is not significant.



6\. Operational requirements



6.1 Given the same batch, the tool must produce the same load file regardless of the order in which documents appear in the input.



6.2 Batches range from a few hundred documents to roughly two million.



6.3 The tool prints a one-line summary on success: documents in, documents out, documents suppressed.

