# Changelog

Alle relevanten Änderungen an den Minicon.SimpleAdmin-Paketen. Neueste Version oben.

## [2.36.0] – 2026-10-22 – BizTalk-Elemente aus der Problemliste deaktivieren (einzeln und in Gruppe)

**Aus der Probleme-Ansicht lassen sich gemeldete BizTalk-Elemente jetzt direkt deaktivieren — einzeln oder mehrere ausgewählte Elemente gesammelt. Die Aktion läuft über einen Auftragskanal zum Zielserver, die WebUI braucht keine BizTalk-Zugangsdaten.**

### Neu
- Aktionsschaltfläche **Deaktivieren** in der Zeile eines BizTalk-Problems und **Auswahl deaktivieren** in der Bulk-Leiste (Punkte 3 und 4 aus MIN-37). Beide Wege öffnen einen Dialog mit optionalem Grund.
- Aktionskanal (Request-Kanal, dateibasiert): Die WebUI schreibt einen Auftrag in ein gemeinsames Austauschverzeichnis (`features.bizTalk.actionsDirectory`), der Worker auf dem BizTalk-Server führt ihn über die Management-API aus und schreibt das Ergebnis zurück. Die WebUI liest den Status über `GET /api/biztalkactions/server/{serverId}`.
- `IBizTalkApiClient` hat Schreibmethoden: `StopApplicationAsync` (`PUT /Applications/{name}/Stop`), `StopOrchestrationAsync` (`PUT /Orchestrations/{applicationName}/{orchestrationName}/Stop`), `StopSendPortAsync` (`PUT /SendPorts/{name}/Stop`), `DisableReceiveLocationAsync` (`PUT /ReceiveLocations/Disable/{receivePortName}/{receiveLocationName}`).
- `IBizTalkActionStore` / `BizTalkActionStore` (Shared): Aufträge und Ergebnisse als JSON-Dateien unter `requests/{serverId}/` und `results/{serverId}/`, atomar geschrieben. Jeder Worker bearbeitet nur die Aufträge seines Servers.
- `IBizTalkActionExecutor` / `BizTalkActionExecutor` (Checkers): führt die offenen Aufträge aus; ein fehlgeschlagener Auftrag bricht die übrigen nicht ab.
- Neue Konfiguration in `config.json`: `features.bizTalk.actionsEnabled` (Hauptschalter) und `features.bizTalk.actionsDirectory` (UNC-Pfad). Oberfläche unter *Einstellungen → Feature-Toggles → BizTalk → Deaktivierung aus der Problemliste*.
- Neue REST-Endpunkte: `POST /api/biztalkactions` (Einzelaktion), `POST /api/biztalkactions/bulk` (Gruppenaktion), `GET /api/biztalkactions/server/{serverId}`, `GET /api/biztalkactions/{serverId}/{requestId}`, `GET /api/biztalkactions/enabled`.
- Dokumentation (Hilfe-Seite) zu Ablauf, Konfiguration, Sicherheitsmodell und den verwendeten Management-API-Aufrufen.

### Geändert
- Die BizTalk-Probleme tragen im Kontext jetzt die zugehörige Anwendung (Orchestrierung) bzw. den Empfangsport (Empfangsort). Die Management-API verlangt diese Angaben im URL-Pfad; die WebUI leitet sie aus dem veröffentlichten Status ab, nicht aus der Anfrage.
- Der Worker führt die BizTalk-Prüfung und anschließend die offenen Deaktivierungsaufträge aus, so dass der nächste Zyklus bereits den neuen Zustand abbildet.

### Sicherheit
- Die WebUI speichert und benötigt keine BizTalk-Zugangsdaten. Die Management-API wird ausschließlich vom Worker auf dem BizTalk-Server aufgerufen (integrierte Windows-Authentifizierung).

[2.36.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.35.0...v2.36.0

## [2.35.0] – 2026-10-22 – Domains mit Umgebungszuordnung und Namenskonvention

**Domains (DNS-Namen) lassen sich jetzt einer Umgebung, Servern dieser Umgebung und optional einem NetScaler-vServer zuordnen; die Namenskonvention liefert Vorschläge und Warnungen.**

### Neu
- `domains` in der `config.json`: `name`, `environment` (Schlüssel aus `environments`, immer explizit), `servers`, optional `netScalerVServer` (mehrere Domains dürfen denselben teilen), `healthCheck`, `description`, `allowNameMismatch`. Fehlt der Block, gilt eine leere Liste.
- Namenskonvention: `<name>-dev` → development, `<name>-ref` → ref, `<name>-prod` und `<name>` ohne Suffix → production. Beide PROD-Varianten sind eigenständige Einträge mit eigenen Servern und vServern. Weitere Suffixe/Umgebungen über `domainSuffixMap` (z. B. `{ "-test": "test" }`).
- WebUI-Menüpunkt **Domains**: Liste gruppiert nach Umgebung mit Filter, Anlegen/Bearbeiten/Löschen. Die Umgebung wird aus dem Namen vorbelegt, die Serverauswahl zeigt nur Server der gewählten Umgebung und aktualisiert sich beim Wechsel. Passt der Name nicht zur Umgebung, wird erst mit „Ausnahme zulassen“ gespeichert.
- Serverliste: Spalte und Filter **Umgebung**.
- Statische Übersichtsseiten zeigen die Domains jeder Umgebung unter der Umgebungsüberschrift.
- Dokumentation (Hilfe-Seite und `documentation.html`, Kapitel 19) zu Domains, Konvention, PROD-Varianten, Ausnahme-Flag und `domainSuffixMap`.

### Geändert
- Umbenennen oder Löschen eines Servers pflegt die Serverlisten der Domains mit.

### Behoben
- Die zentrale Statusübersicht zeigt jetzt auch Server aus weiteren Umgebungen (bisher nur production, ref, development).

### Intern
- `DomainNamingConvention` (Shared) mit Tests für Suffix-Erkennung, Override-Map, Mismatch/Ausnahme, Deserialisierung und Serverfilter nach Umgebung.

[2.35.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.34.2...v2.35.0
