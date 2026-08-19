# Permanent Fixture Corpus

Target: 100+ document classes, committed and stable.

Families: financial (annual report, invoice, ledger, balance sheet, audit) · legal
(contract, MSA, SLA, privacy policy) · healthcare (patient/lab report, multi-language
record) · logistics (bill of lading, customs, manifest) · education (certificate,
transcript, textbook) · publishing (magazine, book, academic paper) · technical
(API docs, architecture report, code-heavy).

Scale levels: L0 hello-world · L1 simple · L2 10-page · L3 50-100 page ·
L4 500-page · L5 1000-page adversarial.

Adversarial set (Render Torture Lab): 1px/0px/huge elements · 10,000 rows ·
100 columns · rowspan across page break · long unbreakable word · very long URL ·
nested flex/grid · grid+table · table+SVG · RTL+LTR mixed inline · CJK · emoji ·
variable/missing font · 404 CSS · slow/infinite JS · huge/deep DOM · sticky/fixed ·
transforms · negative margins · overflow.

RULE: a fixture is only retired with a written reason. Never delete one to get green CI.
