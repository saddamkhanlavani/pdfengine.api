> ## ℹ️ REFERENCE / HISTORICAL
>
> This file is architecture, principles, requirements or a decision log — not a
> capability claim. Where it states or implies a capability status, the
> [capability registry](./PDFENGINE_CAPABILITY_REGISTRY.md) overrides it.

# PDFEngine Customer Scenarios

To ensure PDFEngine satisfies real-world business requirements, our rendering pipeline is validated against 100+ common customer scenarios categorized across key industries.

---

## ➔ Document Scenarios Categories

### 1. Finance & Banking
1.  **Monthly Bank Statements**: Dense tables listing daily balances, deposits, and fee credits.
2.  **Investment Portfolios**: Render layouts showing SVG pie charts of asset allocations and currency summaries.
3.  **Loan Amortization Schedules**: Dynamic multi-page reports with 120+ rows mapping interest and principal payments.
4.  **Credit Card Billing Statements**: Double-column payment layouts, transaction grids, and payment remittance forms.
5.  **Tax Audit Documents**: Dense official grids displaying federal asset deductions.
6.  **Payroll Paystubs**: Itemized pay structures showing pre-tax deductions, gross pay, and direct deposit details.
7.  **Stock Option Grant Agreements**: Executive agreements with complex vesting schedules and signature fields.
8.  **Quarterly Profit & Loss Statements**: Multi-column comparison tables detailing fiscal revenue margins.
9.  **Expense Reports**: Employee travel summaries side-by-side with invoice receipt photo overlays.
10. **Insurance Claim Determinations**: Multi-page policies detailing claim status and covered benefits.

### 2. Healthcare & Clinical Labs
11. **Clinical Diagnostic Panels**: Patient metabolic details, glucose graphs, and warning flag statuses.
12. **Medical Admission Records**: Patient demographics, physician observations, and treatment schedules.
13. **Pharmacy Prescription Sheets**: Drug dosage tables, safety warnings, and doctor execution signatures.
14. **Dental History Logs**: Graphical teeth layout grids and payment history.
15. **Radiology Diagnostic Reports**: Medical imaging references, measurement tables, and findings descriptions.
16. **Patient Intake Forms**: Checkboxes and radio buttons mapping patient history.
17. **Clinical Trial Logs**: Telemetry monitoring statistics of patient responses.
18. **Immunization Records**: Vaccine histories, batch numbers, and health provider credentials.
19. **Discharge Summaries**: Medication directions, rehabilitation timelines, and follow-up coordinates.
20. **Health Insurance ID Cards**: Compact card layouts utilizing barcodes and security guidelines.

### 3. Legal & Contracts
21. **Master Service Agreements (MSA)**: 40+ page contracts with revision history tables, sub-clause lists, and signing fields.
22. **Non-Disclosure Agreements (NDA)**: Structured paragraphs outlining confidentiality bounds.
23. **Notary Public Certificates**: Official double-bordered pages with notary public seal SVGs.
24. **SOW (Statement of Work)**: Deliverables schedules, milestones, and payment grids.
25. **Terms of Service Updates**: Small-font legal notices outlining policy adjustments.
26. **Privacy Policy Disclosures**: Bulleted listings detailing data utilization.
27. **Lease Agreements**: Property rules, deposit matrices, and tenant signing fields.
28. **Purchase & Sale Contracts**: Real estate transaction schedules and notary signatures.
29. **Employment Contracts**: Base salary details, commission tiers, and HR initials.
30. **Board Meeting Minutes**: Corporate resolution logs, attendance registers, and votes indexes.

### 4. Shipping & Logistics
31. **Cargo Manifests**: Dense cargo inventory listing unit weights and safety codes.
32. **Customs Declarations (CN22)**: Official forms declaring international value.
33. **Dangerous Goods Transport Statements**: Hazard alerts (UN3481) and CHEMTREC coordinates.
34. **Pallet Load Sheets**: Solid-block Pallet ID barcodes and gross weight values.
35. **Bill of Lading**: Carrier trailer details, seal numbers, and driver signature boxes.
36. **Delivery Address Labels**: Compact shipping labels utilizing Code 128 barcodes.
37. **Warehouse Inventory Logs**: Rack locations tables and inventory status flags.
38. **Courier Waybills**: Route mapping flowcharts and customer receipt slips.
39. **Proof of Delivery Receipts**: Customer signature captures and timestamp details.
40. **Import/Export Licenses**: Official custom validation stamps and routing codes.

### 5. Corporate Admin & HR
41. **SOC 2 Compliance Reports**: 120-page security audits showing control mapping tables and YAML logs.
42. **Annual Financial Reports**: CEO letter, income statements, balance sheets, and SVG segment charts.
43. **Employee Onboarding Checklists**: Task checklists and training milestones tables.
44. **Performance Reviews**: 360-degree feedback matrices and grading charts.
45. **Course Certificates**: High-fidelity awards with double-borders and executive signatures.
46. **University Transcripts**: Densely packed course grade history and registry stamps.
47. **Conference Slide Decks**: Landscape presentations with roadmaps and grids.
48. **Restaurant Menus**: Multi-column price lists and food item descriptions.
49. **Product Catalogs**: Image matrices and product dimension specifications.
50. **Utility Bills**: Electricity load usage graphs, balance details, and remittance slips.
51. **ISO 27001 Audit Evidence**: Continuous security checkmarks checklists.
52. **Vendor Assessment Briefs**: Security vulnerability scorecards.
53. **Incident Escalation Reports**: Root-cause analysis timelines and resolution summaries.
54. **API Integration Docs**: Monospace code blocks and routing guidelines.
... (validated up to 100+ business scenarios inside testing pipelines).
