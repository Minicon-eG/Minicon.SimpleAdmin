# Changelog

Alle relevanten Änderungen an den Minicon.SimpleAdmin-Paketen. Neueste Version oben.

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
