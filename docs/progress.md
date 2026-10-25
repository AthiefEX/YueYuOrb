# FloatDesk Progress Ledger

Plan: `01-产品与技术规格.md`, `02-Agent开发计划.md`,
`03-验收与兼容性矩阵.md`.

- T01 baseline scaffold: complete.
- Product runtime tests: not started.
- Windows 10 capability evidence: not tested.
- Windows 11 capability evidence: no test host available yet.

Ruling: the plan names `FloatDesk.sln`; the .NET 10 template initially
created `.slnx`, so an explicit classic `.sln` was generated to match the
execution commands. Cost if wrong: newer solution-only tooling expects
`.slnx`, while all Visual Studio and `dotnet` commands in this plan remain
valid.

Ruling: `UseWindowsForms` reports WFO0003 because the app manifest also
declares PerMonitorV2. The manifest remains authoritative because WPF is the
UI framework; the WinForms rule is suppressed for this mixed-framework app.
Cost if wrong: WinForms controls could use a different DPI initialization
path, but no WinForms windows are used.
