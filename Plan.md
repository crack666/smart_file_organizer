**ChatGPT:**

Gern — hier ist eine saubere Vorlage für die nächste Agent-Coding-Session.

# Projektzusammenfassung

**Arbeitstitel:**  
Smart File Organizer

**Ziel:**  
Eine plattformübergreifende Desktop-Anwendung auf Basis von **.NET 10 + Avalonia**, die große Dateibestände auf lokalen Datenträgern analysiert, rekursiv scannt, Ordner- und Dateistrukturen verständlich visualisiert und lokale KI-Modelle über **Ollama** nutzt, um Dateien semantisch zu bewerten, zu klassifizieren und sinnvolle Zielordner-Vorschläge zu machen.

**Wichtige Abgrenzung:**  
Die Anwendung soll **keine HTML-Reports als Hauptoberfläche** verwenden, sondern eine **echte interaktive GUI-Anwendung** mit Review-, Filter-, Korrektur- und Freigabefunktionen.  
**Avalonia Accelerate / kostenpflichtige Zusatzcontrols werden bewusst nicht verwendet.**

## Kern-Use-Case

Der Benutzer möchte alte Festplatten, USB-Sticks, Backup-Ordner oder Benutzerverzeichnisse rekursiv scannen und dabei:

* große Speicherfresser erkennen
* relevante Benutzerdaten von irrelevanten System-/Softwarestrukturen trennen
* Bilder, Dokumente, Videos und sonstige Dateien automatisch klassifizieren
* für Dateien Zielkategorien vorschlagen lassen, z. B.:
    * `photo`
    * `documents`
    * `video`
    * `software`
    * `review_needed`
    * `trash_candidates`
* problematische oder unklare Fälle manuell reviewen
* später bestätigte Dateioperationen ausführen, z. B. kopieren, verschieben oder archivieren

## Wesentliche Produktidee

Die Anwendung besteht aus zwei klar getrennten Bereichen:

### 1. Core / Engine

Verantwortlich für:

* rekursiven Dateiscan
* Pfad- und Strukturregeln
* Skip-/Descend-/Shallow-Logik
* Persistenz von Scan-Ergebnissen
* Ollama-Analyse
* Jobsteuerung
* Fortschritt, Pause, Resume, Retry
* Aggregation von Ordnerstatistiken

### 2. GUI / Desktop-App

Verantwortlich für:

* Scan-Konfiguration
* Live-Fortschritt
* Tree- und Tabellenansichten
* Review-Oberfläche
* manuelle Korrekturen
* Filter- und Suchfunktionen
* spätere Aktionsfreigabe

---

# Geplante Features

## Scan und Analyse

* rekursiver Scan eines gewählten Root-Verzeichnisses
* heuristische Erkennung von:
    * Benutzerdatenpfaden
    * System-/Software-/Spielstrukturen
    * tief verschachtelten, irrelevanten Strukturen
* konfigurierbare Blacklist-/Whitelist-Ordnerregeln
* konfigurierbare maximale Tiefe je nach Pfadtyp
* Erzeugung eines persistenten Scanmodells in SQLite

## Dateiklassifikation

* Dateityp-Erkennung per Extension und Metadaten
* Sonderbehandlung für:
    * Bilder
    * Dokumente
    * Videos
    * Archive
    * Audio
    * Code
    * Software/Installer
* bei Bildern / Dokumenten / Videos: semantische Bewertung über Ollama
* nur eine begrenzte Stichprobe je Ordner für Bilder analysieren
* restliche Bilder eines Ordners heuristisch von der Stichprobe ableiten können

## KI-gestützte Bewertung

Für geeignete Dateien soll die Engine bestimmen:

* `category`
* `importance`
* `confidence`
* `summary`
* `suggested_target`

Beispiele:

* Bild → `document_scan`, `screenshot`, `photo`
* Dokument → `invoice`, `personal_document`, `letter`, `other`
* Video → `tutorial`, `mitschnitt`, `other`

## Ordnerbewertung

Ordner sollen als eigene Entitäten sichtbar sein:

* `directory_status`: z. B. `scanned`, `skip`, `shallow`
* `dir_reason`
* Aggregationen:
    * direkte Dateianzahl
    * rekursive Dateianzahl
    * Unterordneranzahl
    * Gesamtgröße
    * dominante Dateitypen
    * dominante Kategorien
    * empfohlener Zielbereich

## Review und Freigabe

* unklare Dateien manuell prüfen
* Vorschläge überschreiben
* einzelne Dateien oder ganze Gruppen bestätigen
* später bestätigte Dateioperationen gesammelt ausführen

## Persistenz

SQLite als lokale Datenbank für:

* Scan-Jobs
* Dateiknoten
* Ordnerknoten
* KI-Ergebnisse
* Benutzer-Overrides
* Jobstatus
* Fehler und Retry-Informationen

## Langlaufende Jobs

* Start / Pause / Resume / Cancel
* scannt große Verzeichnisse über Stunden oder Tage hinweg robust
* Teilfortschritt wird regelmäßig persistiert
* App muss nach Neustart Jobs wieder aufnehmen können

---

# Geplante GUI-Struktur

## Hauptfenster

Eine klassische Desktop-Anwendung mit mehreren Bereichen.

### A. Toolbar / Topbar

* Root-Verzeichnis wählen
* Scan starten
* Pause
* Fortsetzen
* Abbrechen
* Filter öffnen
* Einstellungen
* Apply/Execute Actions

### B. Linke Seitenleiste

* Scan-Jobs
* Root-Verzeichnisse
* Schnellfilter
* evtl. gespeicherte Ansichten

### C. Hauptbereich links: Baumansicht

Eine explorerartige Ordnerstruktur:

* aufklappbare Ordner
* Icons für Ordnerstatus
* Größe, Dateianzahl, Status-Badges optional
* lazy loading / virtuelle Darstellung

**Wichtig:**  
Kein Avalonia Accelerate `TreeDataGrid`.  
Stattdessen:

* klassischer `TreeView` oder
* selbst modellierte, flache virtualisierte Baumdarstellung

### D. Hauptbereich rechts oben: Detailtabelle

Eine tabellarische Ansicht für den aktuell ausgewählten Ordner oder Filter:

* Name
* Typ
* Kategorie
* Größe
* Letzte Änderung
* Konfidenz
* Vorgeschlagenes Ziel
* Review-Status
* Aktion
* Benutzer-Override

Hierfür zunächst:

* Avalonia `DataGrid` oder `ItemsRepeater` + eigene Tabellenstruktur

### E. Hauptbereich rechts unten oder als Tab: Vorschau / Details

* Dateidetails
* KI-Zusammenfassung
* Review-Hinweise
* Pfad
* Dateimetadaten
* spätere Vorschau:
    * Bildvorschau
    * Textauszug
    * Videometadaten

### F. Optionaler Tab: Treemap / Speicheranalyse

Langfristig eine WizTree-/TreeSize-artige Treemap:

* große Blöcke für große Dateien
* Cluster für Dateigruppen
* farbliche Gruppierung nach Typ oder Zielbereich

**Nicht für MVP nötig**, aber architektonisch vorbereiten.

---

# Technologiestack

* **.NET 10**
* **C#**
* **Avalonia UI**
* **MVVM**
* **SQLite**
* **Dapper oder EF Core**  
    Für dieses Projekt würde sich **Dapper** oder ein sehr schlanker Datenzugriff eher anbieten.
* **Ollama HTTP API**
* Logging mit strukturiertem Logging
* keine Browser- oder HTML-UI
* keine Avalonia-Accelerate-Komponenten

---

# Architekturprinzipien

## Schichten

* `App/UI`
* `Application`
* `Domain`
* `Infrastructure`
* `Persistence`
* `Ollama Integration`
* `Scanning`

## Trennung

* GUI darf niemals direkt den Dateiscan implementieren
* GUI darf niemals direkt SQLite-SQL zusammenbauen
* GUI darf niemals direkt Ollama-Requests formulieren
* alles über klar definierte Services und Use Cases

## Scanmodell

Dateisystemeinträge sollen intern als Knoten modelliert werden:

* `DirectoryNode`
* `FileNode`

Gemeinsame Basiseigenschaften:

* Id
* FullPath
* Name
* ParentPath
* RootPath
* RelativePath
* RelativeDir
* Depth
* Size
* LastWriteTime
* Status

---

# Initialprompt für die neue Agent-Coding-Session

Kopiere am besten den folgenden Block in die neue Session.

Wir starten ein neues Agent-Coding-Projekt für eine Desktop-Anwendung namens „Smart File Organizer“.  
  
Ziel ist eine echte interaktive GUI-Anwendung auf Basis von .NET 10 + Avalonia, ohne Avalonia Accelerate oder andere kostenpflichtige Zusatzcontrols. Die Anwendung soll große lokale Dateibestände rekursiv scannen, semantisch analysieren und dem Benutzer eine komfortable Review- und Organisationsoberfläche bieten.  
  
Wichtige Anforderungen:  
  
1. Die App soll plattformübergreifend ausgelegt sein, mindestens Windows und Linux Desktop.  
2. Die App soll große Verzeichnisbäume robust und langfristig scannen können, auch über viele Stunden oder Tage hinweg.  
3. Der Dateiscan und die KI-Analyse sind klar von der GUI getrennt.  
4. Die Anwendung soll SQLite für Persistenz verwenden.  
5. Für semantische Dateianalyse soll die lokale Ollama HTTP API verwendet werden.  
6. Bilder, Dokumente und Videos sollen gezielt analysiert werden. Sonstige Dateien sollen heuristisch klassifiziert werden.  
7. Ordner sind eigene Entitäten und müssen ebenfalls bewertet und aggregiert dargestellt werden.  
8. Die GUI soll keine HTML-Reports verwenden, sondern echte interaktive Ansichten.  
9. Wir wollen eine Explorer-/TreeSize-/WizTree-inspirierte Bedienung:  
   - links Baumansicht  
   - rechts Detailtabelle  
   - unten oder als Tab Details/Vorschau  
   - optional später Treemap  
10. Performance, Virtualisierung, Lazy Loading und Hintergrundjobs sind zentrale Anforderungen.  
11. Wir wollen bewusst keine Abhängigkeit von kostenpflichtigen Avalonia-Komponenten.  
  
Geplante Hauptfeatures:  
- rekursiver Dateiscan mit Skip-/Descend-/Shallow-Regeln  
- konfigurierbare Blacklist-/Whitelist-Verzeichnisse  
- Speicherung aller Scanergebnisse in SQLite  
- KI-basierte Klassifikation per Ollama  
- Konfidenzbewertung, Zusammenfassungen und Zielordner-Vorschläge  
- Review-Workflow mit manuellen Overrides  
- Pause / Resume / Cancel für Scan-Jobs  
- später Copy/Move/Archive-Aktionen auf bestätigte Dateien  
  
Architekturwunsch:  
- saubere Schichtung  
- MVVM  
- GUI getrennt von Core/Engine  
- testbare Services  
- Domain-zentriertes Modell  
- robuste Persistenz  
- keine monolithische Codebasis  
  
Ich möchte, dass du zunächst:  
1. eine sinnvolle Projekt-/Solution-Struktur vorschlägst,  
2. die zentralen Domain-Modelle definierst,  
3. das SQLite-Schema vorschlägst,  
4. die wichtigsten Services und Interfaces skizzierst,  
5. danach ein minimales, aber sinnvolles Avalonia-MVVM-App-Gerüst erzeugst.  
  
Wichtig:  
- Bevor du UI-Details implementierst, priorisiere die Architektur.  
- Baue nichts auf Avalonia Accelerate oder TreeDataGrid auf.  
- Wenn für große Datenmengen eigene virtualisierte Darstellungen sinnvoller sind, berücksichtige das architektonisch.  
- Denke in Richtung eines langfristig wartbaren Desktop-Produkts, nicht in Richtung eines schnellen Demoscripts.

---

# Coding Guidelines

## Allgemein

* Schreibe klaren, nüchternen, wartbaren Produktionscode.
* Bevorzuge Verständlichkeit vor cleveren Tricks.
* Jede neue Komponente soll einen klaren Verantwortungsbereich haben.
* Vermeide God Objects und riesige Klassen.

## Architektur

* Nutze MVVM sauber.
* ViewModels enthalten keine Dateisystemlogik und keine SQL-Logik.
* Businesslogik gehört in Application-/Domain-Services.
* Infrastrukturzugriffe werden abstrahiert.
* Jeder Layer kennt nur die darunter erlaubten Abhängigkeiten.

## Persistenz

* SQLite ist zentrale lokale Persistenz.
* Keine Logik nur im Speicher halten, wenn sie für Resume relevant ist.
* Long-running Jobs müssen checkpointfähig sein.
* Datenmodelle und Domainmodelle sauber trennen, falls nötig.

## Scanning

* Der Scanner muss cancelbar sein.
* Der Scanner muss resumable sein.
* Der Scanner darf den UI-Thread niemals blockieren.
* Große Läufe iterativ und batchweise verarbeiten.
* Frühzeitiges Pruning irrelevanter Verzeichnisse ist Pflicht.
* Keine rekursiven Monsterfunktionen ohne Abbruch- und Fortschrittsmechanismen.

## KI-Integration

* Ollama-Zugriffe über dedizierten Service kapseln.
* Prompts versionieren oder zentral verwalten.
* Modellantworten robust parsen und validieren.
* KI-Ausfälle dürfen den Scan nicht zerstören.
* Ergebnisse und Fehlerzustände persistent speichern.

## UI

* Kein HTML im Produkt.
* Keine kostenpflichtigen Avalonia-Zusatzcontrols.
* Große Listen/Bäume nur virtualisiert oder lazy laden.
* Die GUI soll auch bei vielen Datensätzen responsiv bleiben.
* Der Benutzer soll immer erkennen können:
    * was gerade gescannt wird
    * was analysiert wurde
    * was reviewbedürftig ist
    * welche Aktion vorgeschlagen ist

## Codequalität

* Verwende aussagekräftige Namen.
* Nutze `async/await` konsequent.
* Nutze `CancellationToken` überall dort, wo Jobs lange laufen.
* Fehlerbehandlung explizit und strukturiert.
* Logging an allen wichtigen Pipeline-Stellen.
* Keine still geschluckten Exceptions.

## Tests

* Domain- und Application-Logik testbar halten.
* Scanner-Heuristiken isoliert testbar machen.
* Parser für Ollama-Antworten separat testbar machen.
* Dateisystemzugriffe möglichst über Interfaces abstrahieren.

## Produktstrategie

* Erst Architektur und Datenmodell sauber aufbauen.
* Dann MVP mit:
    * Scan-Job
    * SQLite
    * Basis-GUI
    * Ordnerbaum
    * Dateitabelle
    * einfache Ollama-Analyse
* Erst danach Treemap, Vorschau und komplexere UX ergänzen.

---

# Empfohlener MVP-Scope

Für den ersten echten Build würde ich den Umfang bewusst klein halten:

## MVP 1

* Root-Verzeichnis auswählen
* Scan starten
* Ergebnisse in SQLite speichern
* Ordnerbaum anzeigen
* Dateien im ausgewählten Ordner tabellarisch anzeigen
* Bilder/Dokumente/Videos über Ollama bewerten
* einfache Status- und Fortschrittsanzeige

## MVP 2

* Filter
* Review-Queue
* manuelle Overrides
* Pause/Resume
* Batch-Freigabe

## MVP 3

* Treemap
* Dateivorschau
* Dateibewegungen / Copy / Move
* erweiterte Regeln / Profile je Datenträger

Wenn du willst, kann ich dir darauf aufbauend direkt noch eine **konkrete Solution-Struktur mit Projektnamen, Ordnern und ersten Interfaces** formulieren.