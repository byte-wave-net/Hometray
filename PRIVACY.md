# Datenschutzerklärung Hometray

Stand: 02.10.2026

## Kurz gesagt

Hometray sammelt keine personenbezogenen Daten für uns und überträgt nichts an Byte-Wave oder an Dritte. Die App verbindet sich ausschließlich mit dem Home Assistant, dessen Adresse du selbst einträgst. Alles, was sie speichert, bleibt auf deinem Gerät.

## Verantwortlich

Byte-Wave GmbH
Bahnhofstr. 25d
8575 Bürglen TG
Schweiz

E-Mail: info@byte-wave.net

## Welche Daten die App verarbeitet

Hometray ist eine Steuerzentrale für deinen eigenen Home Assistant und arbeitet lokal auf deinem Windows-PC:

- **Verbindung zu deinem Home Assistant:** Die App baut eine Verbindung zu der Adresse auf, die du in den Einstellungen einträgst (im Heimnetz oder z. B. über Nabu Casa). Sie überträgt dein langlebiges Zugriffstoken zur Anmeldung und die Befehle, die du auslöst (z. B. Licht einschalten, Schloss entriegeln). Sie empfängt die Zustände deiner Geräte sowie Räume und Gerätenamen, um die Kacheln anzuzeigen. Diese Angaben verwendet die App nur im Arbeitsspeicher und speichert sie nicht.
- **Einstellungen:** Adresse deines Home Assistant, Auswahl und Reihenfolge deiner Kacheln samt selbst vergebenen Namen und Gruppen, auf Wunsch gemerkte Eingabewerte für Skripte (nie Passwörter), Sprache und Optionen (z. B. Spaltenanzahl, Rückfrage bei Schlössern) speichert die App in einer Datei im Ordner `%AppData%\Hometray`. Bei der Version aus dem Microsoft Store legt Windows diese Datei in einem privaten Bereich der App ab.
- **Zugriffstoken:** Das Token wird mit der Windows-Datenschutz-Schnittstelle (DPAPI) verschlüsselt gespeichert und kann nur von deinem Windows-Benutzerkonto auf diesem PC entschlüsselt werden.
- **Keine Protokolle:** Die App schreibt keine Protokolldateien.

Diese Daten verlassen dein Gerät nur in Richtung deines eigenen Home Assistant. Wir haben keinen Zugriff darauf.

## Dein Home Assistant und Dritte

Für die Datenverarbeitung auf deinem Home-Assistant-Server bist du selbst verantwortlich. Nutzt du für den Fernzugriff Dienste wie Nabu Casa oder einen eigenen Reverse-Proxy, gelten deren Datenschutzbestimmungen. Hometray ist eine unabhängige App und steht in keiner Verbindung zu Home Assistant oder Nabu Casa.

Verbindest du dich über `http://` außerhalb deines Heimnetzes, werden Token und Befehle unverschlüsselt übertragen. Die App warnt davor und empfiehlt `https://`.

## Keine Übermittlung, kein Tracking

- Keine Server von Byte-Wave, keine Übermittlung deiner Daten an uns.
- Keine Analyse- oder Tracking-Dienste, keine Werbung.
- Kein Benutzerkonto, keine Registrierung.

## Microsoft Store und Windows

Kauf, Download und Updates laufen über den Microsoft Store. Dabei verarbeitet Microsoft Daten nach eigener Datenschutzerklärung: https://privacy.microsoft.com/de-de/privacystatement. Je nach deinen Windows-Einstellungen sendet Windows Absturzberichte an Microsoft. Microsoft stellt uns daraus nur zusammengefasste, anonyme Statistiken (z. B. Anzahl der Abstürze) zur Verfügung.

## Daten löschen

Deinstallierst du die Version aus dem Microsoft Store, entfernt Windows die App samt Einstellungen. Bei der Version außerhalb des Stores kannst du zusätzlich löschen:

- Ordner `%AppData%\Hometray`
- Autostart-Eintrag `Hometray` in `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`

Willst du den Zugriff der App auf deinen Home Assistant beenden, lösche das verwendete Zugriffstoken in Home Assistant unter *Profil → Sicherheit*.

## Deine Rechte

Da wir keine personenbezogenen Daten über dich erhalten, können wir dazu keine Auskunft geben und nichts löschen. Fragen zum Datenschutz beantworten wir gern unter der oben genannten E-Mail-Adresse. Es gelten das Schweizer Datenschutzgesetz (revDSG) und, soweit anwendbar, die DSGVO.

## Änderungen

Ändert sich die Datenverarbeitung, passen wir diese Erklärung an. Es gilt die jeweils aktuelle Fassung.

---

# Privacy Policy Hometray

Last updated: October 2, 2026

## Summary

Hometray does not collect personal data for us and does not transmit anything to Byte-Wave or to third parties. The app only connects to the Home Assistant whose address you enter yourself. Everything it stores stays on your device.

## Controller

Byte-Wave GmbH
Bahnhofstr. 25d
8575 Bürglen TG
Switzerland

Email: info@byte-wave.net

## Data processed by the app

Hometray is a control panel for your own Home Assistant and works locally on your Windows PC:

- **Connection to your Home Assistant:** The app connects to the address you enter in the settings (in your home network or, for example, via Nabu Casa). It transmits your long-lived access token to sign in and the commands you trigger (e.g. switching on a light, unlocking a lock). It receives the states of your devices as well as rooms and device names to show the tiles. The app only uses this information in memory and does not store it.
- **Settings:** The address of your Home Assistant, the selection and order of your tiles including custom names and groups, input values for scripts if you ask the app to remember them (never passwords), language and options (e.g. number of columns, confirmation for locks) are stored by the app in a file in the folder `%AppData%\Hometray`. For the Microsoft Store version, Windows keeps this file in a private area of the app.
- **Access token:** The token is stored encrypted with the Windows Data Protection API (DPAPI) and can only be decrypted by your Windows user account on this PC.
- **No logs:** The app does not write log files.

This data only leaves your device towards your own Home Assistant. We have no access to it.

## Your Home Assistant and third parties

You are responsible for the data processing on your Home Assistant server. If you use services such as Nabu Casa or your own reverse proxy for remote access, their privacy terms apply. Hometray is an independent app and is not affiliated with Home Assistant or Nabu Casa.

If you connect via `http://` outside your home network, the token and commands are transmitted unencrypted. The app warns about this and recommends `https://`.

## No transmission, no tracking

- No Byte-Wave servers, no transmission of your data to us.
- No analytics or tracking services, no advertising.
- No user account, no registration.

## Microsoft Store and Windows

Purchase, download and updates are handled by the Microsoft Store. Microsoft processes data according to its own privacy statement: https://privacy.microsoft.com/en-us/privacystatement. Depending on your Windows settings, Windows sends crash reports to Microsoft. Microsoft only provides us with aggregated, anonymous statistics from these (e.g. number of crashes).

## Deleting data

When you uninstall the Microsoft Store version, Windows removes the app together with its settings. For the version outside the Store you can additionally delete:

- Folder `%AppData%\Hometray`
- Autostart entry `Hometray` in `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`

To end the app's access to your Home Assistant, delete the access token it uses in Home Assistant under *Profile → Security*.

## Your rights

As we do not receive any personal data about you, there is nothing we could disclose or delete. We are happy to answer privacy questions at the email address above. The Swiss Federal Act on Data Protection (revFADP) and, where applicable, the GDPR apply.

## Changes

If the data processing changes, we will update this policy. The current version applies.
