# Hometray

WinUI-3-App für den Infobereich der Windows-Taskleiste (Architektur wie Audio-Tray):
Ein Klick auf das Haus-Symbol öffnet ein Flyout mit **Kacheln** für deine Home-Assistant-Geräte –
direkt bedienbar, live aktualisiert.

## Funktionen

- Flyout über der Taskleiste, schließt bei Fokusverlust, folgt Hell/Dunkel-Modus (Mica)
- Live-Zustände über die Home-Assistant-**WebSocket-API** (`state_changed`-Events), automatische Wiederverbindung
- Kachel anklicken = Hauptaktion; zusätzlich je Typ eine Bedienleiste:

  | Typ | Klick | Bedienleiste |
  |---|---|---|
  | Licht | an/aus (Icon in Lichtfarbe) | Helligkeits-Slider |
  | Schalter, Hilfsschalter, Ventilator, Automation, Gruppe | an/aus | – |
  | Rollladen / Tor | umschalten | ▲ ■ ▼ |
  | Klima | – | − / + Zieltemperatur |
  | Medien | Wiedergabe/Pause | ⏮ ⏯ ⏭ |
  | Schloss | ver-/entriegeln | – |
  | Szene, Skript, Taste | ausführen (siehe unten: Skripte mit Eingaben) | – |
  | Sensor, Binärsensor, Wetter, Person, … | nur Anzeige | – |

- **Detail-Panel:** Kachel länger als 1 Sekunde gedrückt halten (Maus oder Touch; Rechtsklick geht auch) öffnet
  weitere Einstellungen des Geräts, soweit es sie unterstützt. Escape, ✕ oder ein Klick daneben schließt es.

  | Typ | Einstellungen |
  |---|---|
  | Licht | Helligkeit, Farbtemperatur, Farbe (Schnellfarben, Farbton, Sättigung), Effekt |
  | Rollladen | Position, Lamellen |
  | Klima | Modus, Zieltemperatur, Voreinstellung |
  | Ventilator | Stufe, Voreinstellung, Oszillation |
  | Medien | Lautstärke, Stumm, Quelle |
  | Zahl-/Auswahl-Helfer (`input_number`, `number`, `input_select`, `select`) | Regler bzw. Auswahlliste (öffnet schon beim normalen Klick) |

- **Skripte mit Eingaben:** Hat ein Skript Eingabefelder (`fields`), fragt ein Dialog die Werte ab, bevor es startet.
  Pro Feld erscheint das passende Steuerelement (Text, Zahl als Regler oder Feld, Schalter, Auswahlliste,
  Entität, Uhrzeit, Datum, Dauer, Farbe, Farbtemperatur; JSON/Objekt und übrige Arten als Text). Pflichtfelder (`*`) müssen
  gefüllt sein, Vorgabewerte sind vorbelegt, leere optionale Felder werden nicht mitgeschickt. Gestartet wird mit
  `script.turn_on` und den Werten als `variables`. Skripte ohne Eingaben starten wie bisher sofort. Mit dem Kontrollkästchen
  *Eingaben merken* startet das Skript künftig direkt mit diesen Werten; fehlt dem Skript später ein neues Pflichtfeld, fragt der
  Dialog wieder (vorbelegt). Gemerkte Werte ändert oder löscht man per langem Druck / Rechtsklick auf die Kachel (*Ändern …* /
  *Vergessen*). Passwortfelder werden nie gemerkt (die Werte stehen im Klartext in der Einstellungsdatei). Nach jedem
  angenommenen Start zeigt die Kachel kurz einen Haken, lehnt Home Assistant ab, erscheint dessen Fehlertext im Flyout.
- Einstellungen: Adresse + Token (Verbindung wird vor dem Speichern getestet), Entitäten suchen und nach
  **Typ** und **Raum** filtern, Reihenfolge per Drag & Drop oder ▲/▼, Kachelnamen überschreiben,
  2–4 Kacheln pro Zeile, Autostart
- **Räume:** Der Raumfilter nutzt die Bereiche aus Home Assistant (Raum der Entität, sonst der ihres Geräts).
  Dafür braucht das Token einen Administrator-Benutzer; sonst bleibt das Raum-Dropdown deaktiviert.
- **Gruppen:** *Gruppe hinzufügen* fügt eine Überschrift in die Kachelliste ein. Alle Kacheln darunter bis zur
  nächsten Überschrift bilden einen Abschnitt im Flyout; Kacheln vor der ersten Gruppe stehen ohne Titel
  oben, leere Gruppen werden nicht angezeigt
- Das Zugriffstoken wird mit DPAPI verschlüsselt (nur für dein Windows-Konto) in
  `%AppData%\Hometray\settings.json` gespeichert

## Einrichtung

1. In Home Assistant: **Profil → Sicherheit → Langlebige Zugriffstoken → Token erstellen**
2. App starten, Adresse (z. B. `http://homeassistant.local:8123`) und Token eintragen → *Verbinden und speichern*
3. Unter *Kacheln hinzufügen* Geräte anhaken

Rechtsklick auf das Symbol: *Kacheln anzeigen*, *Home Assistant im Browser öffnen*, *Einstellungen*, *About*
(Versionsfenster im Stil von Audio-Tray, schließt mit *Schließen* oder Esc) und *Beenden*.

Ein zweiter Start der EXE öffnet das Flyout der laufenden Instanz. Beenden: Rechtsklick auf das Symbol → *Beenden*.
Kommandozeile: `--settings` öffnet die Einstellungen, `--tray` (Autostart) startet still.

## Bauen

```
build.bat
```

Benötigt Visual Studio 2022 mit .NET-Desktop-Workload (der WinUI-XAML-Compiler braucht MSBuild).
Ergebnis: `Hometray\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hometray.exe`
(self-contained, kein Windows-App-SDK-Runtime nötig).

## Aufbau

```
HomeAssistant\HaClient.cs            WebSocket-Client (Auth, get_states, Events, call_service, Reconnect)
HomeAssistant\HaEntity.cs            Zustandsmodell
HomeAssistant\EntityPresentation.cs  Icon, deutscher Statustext, Aktiv-Zustand je Typ
EntityTile.cs                        Kachel inkl. Bedienleisten
MainWindow.xaml(.cs)                 Flyout
SettingsWindow.xaml(.cs)             Einstellungen
TrayIcon.cs / TrayController.cs      Win32-Infobereichs-Symbol + Menü (aus Audio-Tray)
```

## Sprachen

Deutsch, English, Español, 中文 (vereinfacht), Русский, Português (Brasilien), Italiano und Svenska.
Standard ist „Automatisch“ (Windows-Anzeigesprache, sonst Englisch); in den Einstellungen unter *Allgemein → Sprache*
lässt sich eine Sprache fest wählen. Die Umstellung gilt sofort, ohne Neustart. Zahlen (z. B. `21,5` / `21.5`) und
Datum folgen der gewählten Sprache.

Alle Texte stehen in `Hometray\Strings.cs` (eine Zeile pro Text, acht Sprachen in der Reihenfolge de, en, es, zh, ru,
pt, it, sv; Platzhalter `{0}`, `{1}` müssen in allen Sprachen gleich sein). In XAML werden sie mit
`{local:Loc Key=...}` eingebunden, im Code mit `Loc.T("...")`. Eine neue Sprache braucht eine Spalte in
`Strings.cs` und einen Eintrag in `Loc.Languages`.

Die Namen und Zustände aus Home Assistant selbst (Gerätenamen, Räume, Medientitel) werden unverändert angezeigt, so wie
sie dort heißen.

## Sicherheit

- **Rückfrage bei kritischen Aktionen** (in den Einstellungen abschaltbar, Standard: an): vor dem Entriegeln eines
  Schlosses und bei Garagen-, Tor- und Türantrieben (Klick, Bedienleiste und Detail-Panel). Abbrechen ist die
  Vorauswahl. Verriegeln und Stopp fragen nie.
- **Unverschlüsselte Verbindung:** Bei `http://` zu einem Rechner außerhalb des Heimnetzes (nicht
  10.x / 172.16–31.x / 192.168.x / 169.254.x / 100.64–127.x, kein `.local`, `.lan`, `.home`, `.home.arpa`,
  `.internal`, `.fritz.box`, kein einzelner Rechnername) warnt die App vor dem Verbinden und im Status.
  Für Zugriff von außen `https://` verwenden.
- **Token:** DPAPI-verschlüsselt (nur dein Windows-Konto, nicht auf anderen PCs lesbar). Programme, die unter
  deinem Konto laufen, können es trotzdem entschlüsseln – wie bei jedem lokal gespeicherten Zugangsdatum.
- **Eigener Benutzer empfohlen:** Ein langlebiges Token kann alles, was sein Benutzer darf. Lege in Home Assistant
  einen eigenen Benutzer für die App an (nicht dein Administrator-Konto) und erstelle das Token dort. Der Raumfilter
  braucht Administratorrechte; ohne ihn funktioniert der Rest unverändert. Verdacht auf Verlust: Token unter
  *Profil → Sicherheit* löschen.
- Zertifikate werden normal geprüft (selbstsignierte werden abgelehnt). Nachrichten des Servers sind auf 64 MB
  begrenzt. Keine Telemetrie, keine Verbindung außer zu deinem Home Assistant.
- Die EXE ist nicht signiert; Windows SmartScreen kann beim ersten Start warnen.

## Microsoft Store (MSIX)

Der normale Build (`build.bat`) bleibt unpackaged. Das Store-Paket baut `build-msix.bat`:

```
build-msix.bat x64 store     – unsigniertes .msix für Partner Center (Standard)
build-msix.bat ARM64 store   – dasselbe für ARM64
build-msix.bat x64 test      – mit selbstsigniertem Zertifikat signiert, zum lokalen Installieren
```

Ergebnis in `AppPackages\Hometray_<Version>_<Plattform>_Test\Hometray_<Version>_<Plattform>.msix`, Log in `build-msix.log`.
Hochgeladen wird nur die `.msix` (die übrigen Dateien im Ordner sind für das Sideloading). Der Build nutzt eigene Ordner
(`bin-msix\`, `obj-msix\`) und schaltet das Projekt über `-p:Packaged=true` um (MSIX statt unpackaged, Windows App SDK als
Framework-Paket, das der Store automatisch mitinstalliert).

| Datei | Zweck |
|---|---|
| `Hometray/Package.appxmanifest` | Paket-Manifest (Identität, Sprachen, Startaufgabe, Fähigkeiten) |
| `Hometray/Assets/Package/` | Kachel- und Store-Logos, aus `Assets/icon.svg` erzeugt |
| `build-msix.bat`, `msix-test-cert.ps1` | Paket bauen / Testzertifikat für den Modus `test` |
| `StoreListing.md` | Texte für Partner Center (Deutsch, English) und Hinweise für die Zertifizierung |
| `PRIVACY.md` | Datenschutzerklärung (Pflicht im Store, als Webseite veröffentlichen und die URL eintragen) |

**Vor dem ersten Hochladen (Partner Center):**

1. Produktnamen **„Hometray“ reservieren** und die drei Werte aus *Produktverwaltung → Produktidentität* in
   `Package.appxmanifest` eintragen: `Identity/Name`, `Identity/Publisher`, `PublisherDisplayName`. Die aktuellen Werte
   sind nur Platzhalter nach dem Muster von Audio-Tray; stimmen sie nicht exakt, wird der Upload abgelehnt.
2. Version vor jedem weiteren Upload erhöhen (`Identity/Version`, das letzte Segment bleibt `0`).
3. Datenschutz-URL, Altersfreigabe (IARC), Preis, mindestens einen Screenshot und die Texte aus `StoreListing.md` eintragen.
4. Unter *Hinweise für die Zertifizierung* einen **erreichbaren Test-Home-Assistant mit Zugriffstoken** angeben (Vorlage
   in `StoreListing.md`) – ohne einen solchen kann die Prüfung die App nicht bedienen.
5. Die eingeschränkte Fähigkeit `runFullTrust` begründen (Win32-Infobereichssymbol, siehe Vorlage).

**Unterschiede der Store-Version:** Autostart läuft über die Windows-Startaufgabe `HometrayStartup` (statt Registry-Eintrag;
Windows kann sie in *Einstellungen → Apps → Autostart* abschalten). Das Symbol kann die App nicht selbst in der
Taskleiste anheften (ihre Registry ist virtualisiert); die Einstellungen verweisen stattdessen auf die Windows-Taskleisten-
Einstellungen. Beim Start aus dem Startmenü zeigt die App sofort die Kacheln; beim Start mit Windows bleibt sie still im Hintergrund.

## App-Icon

Quelle ist `Hometray\Assets\icon.svg`. Daraus sind `appicon.png` (512 px, About-Fenster und Titelleiste der
Einstellungen) und `app.ico` (16–256 px; EXE-Icon und Fenster-Icons) erzeugt. Das Tray-Symbol bleibt das Haus
und wird zur Laufzeit aus der Windows-Symbolschrift gezeichnet. Bei einer Änderung am SVG müssen beide Dateien
neu erzeugt werden.

## Bekannte Grenzen

- Icons sind Segoe Fluent Icons je Domain/Geräteklasse; die `mdi:`-Icons aus Home Assistant werden nicht übernommen
- Nur HTTP(S) mit gültigem Zertifikat bzw. unverschlüsselt im LAN; selbstsignierte Zertifikate werden abgelehnt
- Das MSIX-Paket ist gebaut und strukturell geprüft, aber noch nicht auf einem Rechner installiert und mit dem
  Zertifizierungskit (WACK) getestet
