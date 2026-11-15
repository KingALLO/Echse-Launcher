# Echse Launcher

Ein lokaler Windows-Launcher mit WinUI 3 und nativem Windows-Acrylic-Hintergrund: eine Spiele-Storefront mit konfigurierbarem Katalog, qBittorrent-Anbindung und persönlicher Bibliothek.

## Starten

Voraussetzungen: Windows 10 ab Version 2004 / Windows 11, x64, .NET-8-SDK und Windows-SDK-Buildwerkzeuge. Für die Entwicklung eignet sich Visual Studio 2022 mit „WinUI-Anwendungsentwicklung“ bzw. den Windows-/ .NET-Desktop-Werkzeugen. Windows steuert die Verfügbarkeit von Desktop Acrylic; bei fehlender Unterstützung, deaktivierten Transparenzeffekten oder Energiesparmodus kann ein solider Systemhintergrund erscheinen.

Die App deklariert im eingebetteten Windows-Manifest **Per-Monitor-DPI-Awareness V2**. Dadurch rendert WinUI Text und Bedienelemente mit der Skalierung des jeweiligen Monitors, statt Windows das gesamte Fenster als Bitmap vergrößern zu lassen. Acrylic zeichnet nur den Hintergrund weich. Nach Änderungen am DPI-Manifest die App vollständig beenden und neu starten.

Titelbereich, Seitenleiste und Hauptbereich teilen sich eine Acrylic-Fläche. Ein eigener `SystemBackdrop` verwendet den nativen `DesktopAcrylicController` mit neutralgrauer Tönung und expliziter Tint-/Luminositätsdeckkraft statt der farbverändernden Standardkonfiguration. Die weinrote Grundfarbe **`#4D0B20`** gilt für die gesamte App: Entdecken, Bibliothek, Spielseiten, Downloads, Profil und Einstellungen einschließlich Titelleiste, Navigation, Karten, Eingabefeldern und Dialogen. Durchscheinende weinrote Flächen und dunklere Abstufungen lassen den nativen Hintergrund weiterhin durchscheinen; die Acrylic-Tönung selbst bleibt neutral. Windows-eigene Materialkörnung und Hintergrundverarbeitung bleiben Teil des nativen Effekts; farbmetrisch identische Hintergrundpixel werden nicht garantiert. Im Windows-Kontrastmodus gelten stattdessen die Systemfarben. Bei inaktivem Fenster kann Windows Acrylic durch eine deckende Ersatzfarbe ersetzen; den Effekt deshalb auch mit aktivem Fenster prüfen.

```powershell
dotnet build .\src\Echse.App\Echse.App.csproj
dotnet run --project .\src\Echse.App\Echse.App.csproj
```

Die App startet mit einem **Online-Katalog von Steam**, ohne API-Schlüssel oder Steam-Anmeldung. Unter „Entdecken“ stehen eine linke Spieleliste, horizontal blätterbare Cover-Reihen und eine Online-Suche zur Verfügung. Die Bibliothek enthält weiterhin ausschließlich selbst hinzugefügte Spiele. Es werden keine Downloads oder Spieldateien mitgeliefert.

Das App-Projekt verwendet standardmäßig x64, damit Build und `dotnet run` denselben Ausgabeordner verwenden. Eine zusätzliche Angabe von `-p:Platform=x64` ist nicht nötig.

```powershell
dotnet test .\tests\Echse.Core.Tests\Echse.Core.Tests.csproj
dotnet publish .\src\Echse.App\Echse.App.csproj -c Release -p:Platform=x64 -o .\publish
```

Die veröffentlichte App ist unpackaged und enthält .NET sowie die Windows-App-SDK-Laufzeit. Den gesamten Ausgabeordner weitergeben, nicht nur die EXE. Der Publish-Schritt übernimmt auch die zum Start erforderlichen WinUI-Ressourcen (`.xbf` und `.pri`).

## Funktionsumfang

- Steam-basierter Online-Katalog mit Bestseller-, Neuerscheinungs-, Angebots- und Vorschau-Reihen, Online-Suche und direkter Eingabe einer Steam-App-ID.
- Steam-inspirierte Spielseiten mit großer auswählbarer Bildergalerie, Infospalte, Beschreibung, Entwickler, Publisher, Veröffentlichung und Plattformen. Bei schmalen Fenstern stehen Galerie und Informationen untereinander.
- Deutlicher Steam-Preis inklusive verfügbarer Rabatt-/Streichpreisinformationen und Preisstand; separater, ausdrücklich ungeprüfter Keyforsteam-Ab-Preis mit Quellenlink.
- Fensterfüllende Bildansicht in Originalauflösung, ohne Abschneiden: Galerie-Bild anklicken, mit Pfeiltasten oder mittig links/rechts liegenden Bildpfeilen wechseln, bis 4-fach zoomen und mit Escape schließen.
- Technische Mindest- und empfohlene Anforderungen von Steam mit getrennten Angaben für Windows, macOS und Linux, soweit verfügbar.
- Separater konfigurierbarer Download-Katalog mit lokaler Suche nach Titel, Genre und Tags sowie eigenen Popularitätswerten.
- Lokale Store-Empfehlungen anhand von Detailaufrufen, Bibliothek, Favoriten und Gesamtspielzeit; ohne Historie zählt Popularität. Bereits hinzugefügte Spiele werden aus Store-Empfehlungen ausgeschlossen.
- Bibliothek nach der Figma-Vorlage: durchsuchbare alphabetische Spieleleiste mit kleinen Bildern, horizontale Cover-Reihen „Zuletzt gespielt“ und „Als Nächstes spielen“ sowie ein sortierbares Hochkant-Cover-Raster mit Spielzeit. Die erste Karte jeder Reihe bietet einen Direktstart, sofern eine EXE verknüpft ist. „Als Nächstes“ bevorzugt Favoriten und noch nicht gespielte bzw. länger nicht gestartete Titel der eigenen Sammlung; es werden keine fremden Spiele oder erfundenen Spielzeiten eingefügt. Ohne Starthistorie erscheint ein Hinweis statt erfundener letzter Starts. Auf schmalen Fenstern entfällt die Seitenleiste und die Suche wandert in den Hauptbereich. Spieldetails, EXE-Verknüpfungen, Favoriten und lokales Hinzufügen bleiben erreichbar.
- Responsive Profilübersicht mit Avatar, Beschreibung, Sammlung, Spielstatistiken und zuletzt gespielten Titeln. „Profil bearbeiten“ öffnet eine separate Ansicht für Anzeigename, Beschreibung und eigenes Profilbild (PNG/JPEG bis 5 MiB); „Abbrechen“ verwirft die Eingaben.
- qBittorrent-Anmeldung, explizit bestätigte Magnet-Downloads und Fortschrittsanzeige.
- Native Windows-Acrylic-Systemkulisse, DPI-scharfe Oberfläche, weinrote Akzente, eigenes skalierbares Eidechsenlogo und Genre-Grafiken als Cover-Ersatz.

Dies ist ein **lokales MVP**, kein Online-Netzwerk. Profile sind keine authentifizierten Konten. Kommentare, Freunde, Cloud-Synchronisation, Installer und automatische Updates sind nicht implementiert.

## Spielekatalog konfigurieren

### Steam-Metadaten

„Entdecken“ verwendet standardmäßig öffentliche Steam-Store-Endpunkte (`featuredcategories`, `storesearch`, `appdetails`) mit deutscher Sprache und Region DE. Ein API-Schlüssel wird dafür nicht benötigt. Diese Store-Endpunkte sind keine garantierte Vollkatalog-API: Formate, Erreichbarkeit, Anfragegrenzen und regionale Verfügbarkeit können sich ändern.

Die Startseite lädt eine **Auswahl**, nicht alle Steam-Spiele auf einmal. Die Suche fragt Steam nach dem eingegebenen Namen und liefert dessen begrenzte Trefferauswahl; für weitere Treffer gibt es einen Link zur vollständigen Steam-Suche im Browser. Eine numerische App-ID öffnet direkt deren Detailseite. Einträge können auch DLCs und Demos sein. Hardware oder andere Nicht-Spiele werden beim Laden der Details ausdrücklich abgelehnt. Entfernte und regional gesperrte Titel sind möglicherweise nicht verfügbar.

Spielseiten laden vollständige Informationen erst beim Öffnen. HTML aus Beschreibungen wird in einfachen Text umgewandelt; es werden keine externen Skripte oder Webseiten in der App ausgeführt. Hochkant-Cover werden vom Steam-CDN geladen; fehlt das optionale Motiv, wird das vom Katalog gelieferte Bild verwendet. Auch Banner und Screenshots sind optional. Quelle und Rechteinhaber bleiben auf der Spielseite kenntlich.

Für lokale Bibliotheksspiele ohne Bilder und ohne Steam-ID sucht Echse beim Öffnen der Bibliothek automatisch anhand des Spieltitels im öffentlichen Steam-Katalog nach Covern. Bei bekannter Steam-ID werden fehlende Bilder direkt darüber nachgeladen. Nach der Zuordnung werden die hochauflösenden Header- und Hochkant-Bilder statt kleiner Suchvorschaubilder verwendet. Nur ein eindeutiger, abgesehen von Groß-/Kleinschreibung und Satzzeichen identischer Titel wird automatisch übernommen; Editionen und mehrdeutige Titel werden nicht geraten. Pro Durchlauf werden maximal 20 Titel mit einer Sekunde Abstand geprüft, beim Seitenwechsel wird abgebrochen. „Cover laden“ verarbeitet weitere fehlende Titel bzw. wiederholt erfolglose Zuordnungen. In den Spieldetails bietet „Online-Cover auswählen“ eine Namenssuche mit Vorschau und expliziter Trefferauswahl. Nichttreffer und Netzwerkfehler werden angezeigt, nicht durch erfundene Cover ersetzt. Es werden nur Bildadressen gespeichert: Spiel-ID, EXE, Spielzeit, Favoriten und die Steam-Zuordnung bleiben unverändert. Steam erhält den gesuchten Spieltitel, aber keine EXE-Pfade oder Spielzeiten. Die Bilder werden anschließend direkt vom Steam-CDN geladen; es werden keine Spiele automatisch zur Bibliothek hinzugefügt.

Die Galerie lädt Vorschaubilder für die Bildauswahl und verwendet, soweit Steam sie bereitstellt, die vollständigen Screenshot-URLs für das große Motiv und die vergrößerte Ansicht. Alte Cache-Einträge mit ausschließlich kleinen Bildern bleiben lesbar und werden beim nächsten erfolgreichen Detailabruf aktualisiert. Der Steam-Preis bezieht sich auf Region DE; bei fehlendem Einzelpreis wird weder „kostenlos“ noch ein erfundener Betrag angezeigt.

Technische Anforderungen werden aus `pc_requirements`, `mac_requirements` und `linux_requirements` geladen. Ein HTML-Parser wandelt die Angaben in umbrochenen Klartext um; Skripte und Styles werden nicht angezeigt oder ausgeführt. Fehlende Plattformen und empfohlene Werte bleiben ausdrücklich als nicht verfügbar gekennzeichnet. Die Angaben werden nicht mit der Hardware des PCs abgeglichen.

Die letzte Startseiten-Auswahl und bis zu 100 zuletzt geladene Detaildatensätze werden lokal gespeichert. Bei Fehlern bleiben bereits gespeicherte Daten sichtbar, mit explizitem Fehlerhinweis und erneuter Lademöglichkeit. Laufende Such-/Detailanfragen werden bei einem Seitenwechsel abgebrochen, damit verspätete Antworten nicht die aktuelle Ansicht überschreiben. Es gibt keine Hintergrund-Vollindexierung und keine Massenabfrage aller Steam-Details.

Steam-Metadaten enthalten **keine Torrent-Links**, importieren keinen Steam-Besitz und gewähren keine Downloadberechtigung. Das Hinzufügen zur Bibliothek verknüpft keinen Steam-Account; zum lokalen Starten ist weiterhin eine manuell gewählte EXE notwendig. Empfehlungen arbeiten nur mit den geladenen Titeln und bereits bekannten Genres, nicht mit einer globalen Analyse aller Steam-Spiele.

### Keyforsteam-Preisvergleich

Nach erfolgreichem Laden einer Steam-Spielseite fragt Echse die öffentliche Keyforsteam-Suche ab. Produkt-URLs werden bevorzugt aus eindeutig passenden Suchtreffern übernommen; Weiterleitungen direkt zum Produkt werden ebenfalls erkannt. Liefert eine erfolgreiche Suche keine serverseitigen Treffer, wird wie bisher ein aus dem Namen abgeleiteter URL-Kandidat geprüft, damit bekannte Produktseiten weiterhin funktionieren. **Ein passender URL-Name genügt niemals als Zuordnung:** Nur wenn die strukturierten Produktdaten denselben Titel und eine PC-Kategorie enthalten, wird deren `AggregateOffer.lowPrice` mit Währung und Abrufzeit als **ungeprüfter Ab-Preis** angezeigt. Abweichende Editionen/Konsolenprodukte und mehrdeutige Suchtreffer werden nicht als Treffer übernommen. Geänderte Produktdaten können ohne Preis bleiben; die Keyforsteam-Suche bleibt verlinkt. Echse verwendet keine privaten API-Zugangsdaten und führt keine fremden Seitenskripte aus.

**Dieser öffentliche Gesamtpreis ist nicht der verifizierte günstigste Steam-Key für Deutschland.** Die Quelldaten können Accounts, DLC-Angebote, andere Aktivierungsplattformen, Regionen oder Editionen sowie Gutscheine enthalten. Händler, Aktivierbarkeit, Zahlungsgebühren und tatsächlich gültigen Endpreis deshalb auf Keyforsteam und beim Verkäufer prüfen. Es werden daraus keine garantierten Ersparnisse gegenüber Steam berechnet. Öffentlich sichtbare Zusammenfassungen und strukturierte Preise können voneinander abweichen; Echse zeigt ausschließlich den gekennzeichneten strukturierten Ab-Preis.

Anfragen sind pro App-Instanz serialisiert, mit mindestens fünf Sekunden Abstand zwischen einzelnen Seiten entsprechend dem veröffentlichten Crawl-Delay. Ergebnisse einschließlich eindeutiger Nichttreffer werden 15 Minuten im Arbeitsspeicher zwischengespeichert (maximal 200 Titel). Jede Seite hat ein Zeitlimit von 20 Sekunden einschließlich Antwortinhalt. Verbindungsfehler, Zeitüberschreitungen, HTTP-Fehler und nicht lesbare Daten werden getrennt angezeigt, mit erneuter Lademöglichkeit statt Ersatzpreis. Eine nicht erreichbare Gegenstelle lässt sich nicht durch eine andere Titelzuordnung reparieren. Keyforsteam erhält dabei den abgefragten Spieltitel, aber keine Spielzeit oder Profilinformationen. Die Erreichbarkeit und Struktur der Website sind nicht garantiert.

### Eigener Download-Katalog

qBittorrent ist ein Download-Client, kein Spieleindex. In **Einstellungen** werden deshalb Katalog-URL und qBittorrent-Adresse getrennt eingerichtet. Unter „Entdecken“ kann über die Quellenauswahl zum eigenen Download-Katalog gewechselt werden. Ein konfigurierter Katalog wird beim App-Start, mit „Katalog aktualisieren“ bzw. beim Speichern der Einstellungen geladen. Dessen Suche und Empfehlungen verwenden den lokalen Snapshot. Bei Netzwerkfehlern bleibt der letzte erfolgreiche Snapshot erhalten; der Fehler wird sichtbar angezeigt. Nach einem Quellenwechsel wird der alte Cache entfernt. Ohne URL erscheint in dieser Ansicht ein ausdrücklich gekennzeichneter Demo-Katalog quelloffener Spiele mit Beispiel-Rangfolge.

Die Katalog-URL muss per HTTP(S) ein JSON-Array liefern, siehe [examples/catalog.json](examples/catalog.json):

```json
[
  {
    "id": "stable-game-id",
    "title": "Mein Spiel",
    "description": "Kurzbeschreibung",
    "genre": "Strategie",
    "tags": ["Aufbau", "Indie"],
    "popularity": 125,
    "coverUrl": null,
    "magnetUri": null
  }
]
```

- `id`: nicht leer, stabil und innerhalb des Katalogs eindeutig.
- `title`: nicht leer. `description`, `genre` und `tags` sind optional mit leeren Standardwerten; explizites `null` ist dort ungültig.
- `popularity`: nicht negative Ganzzahl, durch den Betreiber zu berechnen, etwa aus Abrufen oder Seedern. Echse erfindet keine Serverstatistik.
- `coverUrl`: optionaler absoluter HTTP(S)-Bildlink. Die App lädt Bilder direkt von dieser Adresse; nur vertrauenswürdige Quellen konfigurieren.
- `portraitUrl`: optionaler absoluter HTTP(S)-Bildlink für ein Hochkant-Cover, unabhängig von einer Steam-ID.
- `magnetUri`: optionaler Magnet-Link mit gültigem `xt=urn:btih:…` (40 Hex- oder 32 Base32-Zeichen). Ohne Link ist das Spiel nur als Katalog-/Bibliothekseintrag verfügbar.
- `steamAppId`: optionale positive Steam-App-ID zum Nachladen von Banner und Details. Ohne diese ID werden keine Steam-Metadaten oder Downloadrechte zugeordnet; die reine Coversuche für hinzugefügte Bibliotheksspiele ist davon unabhängig. Bei Verwendung derselben `id` wie im Steam-Katalog (`steam:APPID`) wird derselbe Bibliothekseintrag genutzt; eigene Magnet-Links bleiben beim Aktualisieren vorhandener Bibliothekseinträge erhalten.
- Maximal 5000 Spiele und 4 MiB JSON. Eingebettete URL-Zugangsdaten sind nicht erlaubt. Katalog-Authentifizierung und Stremio-Add-ons werden noch nicht unterstützt.

Der Katalog enthält nur Metadaten, keine EXE-Pfade oder auszuführenden Befehle. Verwende ausschließlich Inhalte, deren Nutzung und Verbreitung erlaubt ist.

## qBittorrent einrichten

1. In qBittorrent die Weboberfläche aktivieren und Zugangsdaten setzen.
2. In Echse beispielsweise `http://localhost:8080` sowie Benutzername/Passwort eintragen.
3. „qBittorrent testen“, anschließend „Speichern & Katalog laden“ wählen.
4. In den Spieldetails einen angebotenen Download explizit bestätigen.

Reverse-Proxy-Unterpfade werden unterstützt. Für entfernte Server **HTTPS** verwenden: HTTP überträgt das Passwort im Klartext. Zertifikatsprüfung bleibt aktiv; Redirects zu anderen Adressen werden bei qBittorrent nicht automatisch verfolgt.

Downloads bekommen das Tag `echse`; nur diese Torrents erscheinen unter Downloads, mit Aktualisierung alle fünf Sekunden. Der Server legt Dateien in seinem konfigurierten Downloadverzeichnis ab. Remote-Dateien müssen separat auf den Spiele-PC übertragen werden. Installation, Prüfung und Auswahl der lokalen EXE bleiben manuell. Echse startet niemals automatisch heruntergeladene Programme.

## Spielzeiterfassung und lokale Daten

Nur über Echse gestartete EXE-Prozesse werden gemessen. Die Messung erfolgt mit einer monotonen Uhr, wird ungefähr alle 15 Sekunden gespeichert und beim regulären Beenden des Spiels oder Launchers abgeschlossen. Beim abrupten Abbruch kann die Zeit seit dem letzten erfolgreichen Speicherpunkt verloren gehen. Bleibt ein Speicherfehler bestehen, weist die App darauf hin.

Der Launcher muss zur Erfassung geöffnet bleiben. Das Schließen beendet **nicht** das Spiel. Startprogramme, die einen anderen Prozess erzeugen und sofort enden, müssen durch die eigentliche Spiel-EXE ersetzt werden; Prozessbäume, Anti-Cheat-Launcher und externe Starts werden nicht automatisch erkannt. Es kann nur eine Instanz pro Bibliothekseintrag über Echse gestartet werden.

Daten liegen unter `%LOCALAPPDATA%\EchseLauncher`:

- `state.json`: lokales Profil mit stabiler ID, Bibliothek, Statistiken, Einstellungen, Aufrufzähler, eigener Katalog sowie Steam-Startseiten- und Detailcache.
- `avatars`: kopierte Profilbilder.
- `errors.log`: begrenztes technisches Fehlerprotokoll ohne Passwörter und ohne möglicherweise vertrauliche Server-URLs aus Fehlermeldungen.

Schreibvorgänge sind serialisiert und ersetzen die Zustandsdatei atomar. Eine beschädigte Datei wird nicht stillschweigend zurückgesetzt. Vor einem manuellen Zurücksetzen die Daten sichern. Eine Windows-Mutex verhindert, dass zwei Launcher-Instanzen gleichzeitig auf dieselben Daten zugreifen.

Das gespeicherte qBittorrent-Passwort ist per Windows-DPAPI an das aktuelle Windows-Konto gebunden. Andere Profildaten und Serveradressen sind nicht verschlüsselt. Es gibt keine Telemetrie und keine Übertragung der Spielhistorie an einen Empfehlungsdienst. Steam erhält Suchbegriffe, abgefragte App-IDs und Bildabrufe; manuelle Suchanfragen finden erst mit Enter oder „Suchen“ statt. Die automatische Coversuche sendet beim Öffnen der Bibliothek die Titel von Spielen ohne Bilder und ohne Steam-ID. Keyforsteam erhält beim Öffnen von Steam-Spielseiten den Titel für den Preisvergleich. Benutzerdefinierte Katalog-, Cover- und qBittorrent-Server erhalten die jeweils notwendigen Netzwerkanfragen.

## Projektstruktur

- [src/Echse.App](src/Echse.App): native WinUI-Oberfläche, Dateiauswahl, Acrylic, Windows-Passwortschutz.
- [src/Echse.Core](src/Echse.Core): Steam-Metadaten, Katalogvalidierung, Empfehlungen, JSON-Persistenz, qBittorrent und Spielzeiterfassung.
- [tests/Echse.Core.Tests](tests/Echse.Core.Tests): fokussierte Tests ohne laufenden Torrent-Server.
- [tests/Echse.TestGame](tests/Echse.TestGame): kurz laufender Testprozess zur Prüfung von Start, Spielzeit und regulärem Beenden.
