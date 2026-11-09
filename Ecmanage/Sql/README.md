# Ecmanage – handmatige DNN-databasescripts

Wijzigingen aan de DNN-database (stored procedures, indexen, host settings) die bij onze fork van DNN 9.11.2 horen.

- DNN voert deze scripts **nooit zelf** uit. Ze staan bewust buiten `DNN Platform/Website/Providers/DataProviders/SqlDataProvider`
  en hebben geen versienummer in de naam. Een incrementeel DNN-script (`09.11.02.xx.SqlDataProvider`) zou de site bij uitrol
  in upgrade-modus zetten (redirect naar de UpgradeWizard).
- Draai ze per omgeving met SSMS op de DNN-database, eerst op test/acceptatie.
- De scripts gaan uit van `databaseOwner` = `dbo` en een lege `objectQualifier`.
- Na een DNN-upgrade opnieuw draaien: een upgrade kan de stored procedures overschrijven.

| Script | Inhoud |
|---|---|
| `2026-11-09 performance fixes.sql` | `EnsureLocalizationExists` (bugfix), index op `EventLog`, nieuwe `PurgeEventLog`, `PurgeEventLogBatchCount` 10000, `GetTabPaths` zonder catch-all |
